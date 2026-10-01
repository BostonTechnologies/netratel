using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Application.ClientAuth;
using NetRatel.Infrastructure.Services;

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
                // A rejected/revoked refresh token is terminal for this stored
                // identity. Keep it so a restart cannot mistake the install for
                // a first run and consume a pending installer enrollment grant.
                _cachedToken = null;
                _expiresAtUtc = DateTimeOffset.MinValue;
                var detail = await BuildErrorDetailAsync(response, "Refresh token is invalid or revoked.", ct).ConfigureAwait(false);
                throw new AgentClientAuthException(detail, (int)response.StatusCode, shouldClearCredentials: false,
                    code: "refresh_token_rejected");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var payload = await TryReadProblemPayloadAsync(response, ct).ConfigureAwait(false);
                var agentWasDeleted = string.Equals(payload?.Code, "agent_not_found", StringComparison.OrdinalIgnoreCase);
                var isDisabled = string.Equals(payload?.Code, "agent_disabled", StringComparison.OrdinalIgnoreCase);
                var detail = agentWasDeleted
                    ? "Agent identity was not found."
                    : isDisabled
                        ? "Agent is disabled."
                        : "Agent token request was forbidden.";
                // Do not mutate durable identity here. Startup may recover an unknown agent
                // only after it validates and successfully consumes explicit enrollment input.
                // Clearing first would make an invalid or absent grant look like a fresh
                // install on the next service start.
                if (agentWasDeleted)
                {
                    _cachedToken = null;
                    _expiresAtUtc = DateTimeOffset.MinValue;
                }

                throw new AgentClientAuthException(
                    detail,
                    (int)response.StatusCode,
                    shouldClearCredentials: false,
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
        var correlation = SafeDiagnosticToken(payload?.CorrelationId);
        var errorCode = SafeDiagnosticToken(payload?.Code);
        var suffixParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            suffixParts.Add($"code={errorCode}");
        }

        if (!string.IsNullOrWhiteSpace(correlation))
        {
            suffixParts.Add($"correlationId={correlation}");
        }

        if (suffixParts.Count == 0)
        {
            return $"status={(int)response.StatusCode}; detail={fallback}";
        }

        return $"status={(int)response.StatusCode}; detail={fallback}; {string.Join("; ", suffixParts)}";
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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            static string? ReadStringProperty(JsonElement element, string name)
                => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            var title = ReadStringProperty(root, "title");
            var detail = ReadStringProperty(root, "detail");
            var code = ReadStringProperty(root, "code");
            string? correlationId = null;
            if (root.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object)
            {
                correlationId = ReadStringProperty(ext, "correlationId");
                if (string.IsNullOrWhiteSpace(code)) code = ReadStringProperty(ext, "code");
            }

            if (string.IsNullOrWhiteSpace(correlationId)) correlationId = ReadStringProperty(root, "correlationId");

            return new ProblemPayload(title, detail, code, correlationId);
        }
        catch (JsonException)
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
                throw new AgentClientAuthException("Stored Agent identity is invalid.", shouldClearCredentials: false, code: "agent_id_invalid");
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

    private static string? SafeDiagnosticToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? value
            : null;
}
