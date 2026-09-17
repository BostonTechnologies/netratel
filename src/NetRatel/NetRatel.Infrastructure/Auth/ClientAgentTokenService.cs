using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Application.ClientAuth;
using NetRatel.Infrastructure.Services;
using System.Text.RegularExpressions;

namespace NetRatel.Infrastructure.Auth;

public sealed class ClientAgentTokenService : IAgentTokenService
{
    private readonly HttpClient _http;
    private readonly IAgentCredentialStore _credentialStore;
    private readonly IAgentDeviceKeyStore _deviceKeyStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _expiresAtUtc;

    public ClientAgentTokenService(HttpClient http, IAgentCredentialStore credentialStore, IAgentDeviceKeyStore deviceKeyStore)
    {
        _http = http;
        _credentialStore = credentialStore;
        _deviceKeyStore = deviceKeyStore;
    }

    public async Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(_cachedToken) && _expiresAtUtc > now.AddMinutes(1))
            {
                return (_cachedToken, _expiresAtUtc);
            }

            var creds = await _credentialStore.LoadAsync().ConfigureAwait(false);
            if (creds is null)
            {
                throw new AgentClientAuthException("No local agent credentials found.", shouldClearCredentials: false);
            }

            var response = await PostTokenWithRetryAsync(new TokenRequest(creds.Value.AgentId, creds.Value.RefreshToken), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await _credentialStore.ClearRefreshCredentialsAsync().ConfigureAwait(false);
                _cachedToken = null;
                _expiresAtUtc = DateTimeOffset.MinValue;
                var detail = await BuildErrorDetailAsync(response, "Refresh token is invalid or revoked.", ct).ConfigureAwait(false);
                throw new AgentClientAuthException(detail, (int)response.StatusCode, shouldClearCredentials: true);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var payload = await TryReadProblemPayloadAsync(response, ct).ConfigureAwait(false);
                var detail = payload?.Detail ?? payload?.Title ?? "Agent is disabled.";
                // A disabled agent must retain its credential so that an administrator can
                // re-enable it. An unknown agent is different: its server-side identity was
                // deleted or replaced, so retaining the local credential can only cause a
                // permanent restart loop. Clear it immediately and allow an explicitly
                // supplied enrollment bootstrap to establish a new identity on the next run.
                var agentWasDeleted = string.Equals(payload?.Code, "agent_not_found", StringComparison.OrdinalIgnoreCase);
                if (agentWasDeleted)
                {
                    await _credentialStore.ClearRefreshCredentialsAsync().ConfigureAwait(false);
                    _cachedToken = null;
                    _expiresAtUtc = DateTimeOffset.MinValue;
                }

                throw new AgentClientAuthException(
                    detail,
                    (int)response.StatusCode,
                    shouldClearCredentials: agentWasDeleted,
                    code: payload?.Code);
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = await BuildErrorDetailAsync(response, $"Token request failed with {(int)response.StatusCode}.", ct).ConfigureAwait(false);
                throw new AgentClientAuthException(detail, (int)response.StatusCode);
            }

            var dto = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct).ConfigureAwait(false);
            if (dto is null || string.IsNullOrWhiteSpace(dto.AccessToken) || dto.ExpiresIn <= 0)
            {
                throw new AgentClientAuthException("Token response was invalid.");
            }

            if (!string.IsNullOrWhiteSpace(dto.RefreshToken))
            {
                await _credentialStore.SaveAsync(creds.Value.AgentId, dto.RefreshToken).ConfigureAwait(false);
            }

            _cachedToken = dto.AccessToken;
            _expiresAtUtc = now.AddSeconds(dto.ExpiresIn);
            return (_cachedToken, _expiresAtUtc);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void InvalidateCache()
    {
        _cachedToken = null;
        _expiresAtUtc = DateTimeOffset.MinValue;
    }

    private async Task<HttpResponseMessage> PostTokenWithRetryAsync(TokenRequest request, CancellationToken ct)
    {
        const int maxAttempts = 3;
        var delay = TimeSpan.FromMilliseconds(500);
        var key = await _deviceKeyStore.GetOrCreateAsync(ct).ConfigureAwait(false);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var reqMessage = await BuildRequestMessageAsync(request, key, ct).ConfigureAwait(false);
                response = await _http.SendAsync(reqMessage, ct).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500 && attempt < maxAttempts)
                {
                    response.Dispose();
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                response?.Dispose();
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
            }
        }

        throw new AgentClientAuthException("Token endpoint is unreachable.");
    }

    private static async Task<string> BuildErrorDetailAsync(HttpResponseMessage response, string fallback, CancellationToken ct)
    {
        var payload = await TryReadProblemPayloadAsync(response, ct).ConfigureAwait(false);
        var detail = payload?.Detail ?? payload?.Title ?? fallback;
        var correlation = payload?.CorrelationId;
        var errorCode = payload?.Code;
        var body = await TryReadRawBodyAsync(response, ct).ConfigureAwait(false);
        var suffixParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            suffixParts.Add($"code={errorCode}");
        }

        if (!string.IsNullOrWhiteSpace(correlation))
        {
            suffixParts.Add($"correlationId={correlation}");
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            suffixParts.Add($"body={TrimForLog(body)}");
        }

        if (suffixParts.Count == 0)
        {
            return $"status={(int)response.StatusCode} {response.ReasonPhrase}; detail={detail}";
        }

        return $"status={(int)response.StatusCode} {response.ReasonPhrase}; detail={detail}; {string.Join("; ", suffixParts)}";
    }

    private static async Task<ProblemPayload?> TryReadProblemPayloadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? title = root.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
            string? detail = root.TryGetProperty("detail", out var detailValue) ? detailValue.GetString() : null;
            string? code = root.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : null;
            string? correlationId = null;
            if (root.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object)
            {
                if (ext.TryGetProperty("correlationId", out var corrExt))
                {
                    correlationId = corrExt.GetString();
                }

                if (string.IsNullOrWhiteSpace(code) && ext.TryGetProperty("code", out var codeExt))
                {
                    code = codeExt.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(correlationId) && root.TryGetProperty("correlationId", out var corrValue))
            {
                correlationId = corrValue.GetString();
            }

            return new ProblemPayload(title, detail, code, correlationId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> TryReadRawBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private sealed record TokenRequest(string AgentId, string RefreshToken);
    private sealed record TokenResponse(string AccessToken, int ExpiresIn, string? RefreshToken);
    private sealed record ProblemPayload(string? Title, string? Detail, string? Code, string? CorrelationId);

    private async Task<HttpRequestMessage> BuildRequestMessageAsync(TokenRequest request, AgentDeviceKeyMaterial key, CancellationToken ct)
    {
        var dto = new
        {
            agentId = request.AgentId,
            refreshToken = request.RefreshToken,
            requestedScopes = new[] { "netratel:connect" }
        };
        var body = JsonSerializer.Serialize(dto);
        if (!Guid.TryParse(request.AgentId, out var agentId))
        {
            throw new AgentClientAuthException("Stored Agent identity is invalid.", shouldClearCredentials: true, code: "agent_id_invalid");
        }

        var bodyHash = PopSignatureService.ComputeTokenBodyHash(agentId, request.RefreshToken, ["netratel:connect"]);
        var nonce = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        var message = PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/token", timestamp, nonce, bodyHash);
        var signature = PopSignatureService.Sign(key.Algorithm, key.PrivateKey, message);

        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/token")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_cachedToken))
        {
            msg.Headers.Authorization = new AuthenticationHeaderValue("PoP", _cachedToken);
        }
        msg.Headers.Add("X-NetRatel-Signature", signature);
        msg.Headers.Add("X-NetRatel-Nonce", nonce);
        msg.Headers.Add("X-NetRatel-Timestamp", timestamp.UtcDateTime.ToString("O"));
        return msg;
    }

    private static string TrimForLog(string value)
    {
        const int max = 2048;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = RedactSecrets(value);
        var compact = sanitized.Replace(Environment.NewLine, " ").Trim();
        return compact.Length <= max ? compact : compact[..max] + "...";
    }

    private static string RedactSecrets(string value)
    {
        var result = value;
        result = Regex.Replace(result, "(\"refreshToken\"\\s*:\\s*\")[^\"]+\"", "$1[REDACTED]\"", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, "(\"accessToken\"\\s*:\\s*\")[^\"]+\"", "$1[REDACTED]\"", RegexOptions.IgnoreCase);
        return result;
    }
}
