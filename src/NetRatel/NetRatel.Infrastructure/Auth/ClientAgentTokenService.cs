using System.Net;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NetRatel.Application.ClientAuth;
using NetRatel.Infrastructure.Services;

namespace NetRatel.Infrastructure.Auth;

public sealed class ClientAgentTokenService : IAgentTokenService
{
    private readonly HttpClient _http;
    private readonly IAgentCredentialStore _credentialStore;
    private readonly IAgentDeviceKeyStore _deviceKeyStore;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CacheState _cache = new(null, DateTimeOffset.MinValue);
    private bool _supportsRefreshExchange;

    public ClientAgentTokenService(HttpClient http, IAgentCredentialStore credentialStore, IAgentDeviceKeyStore deviceKeyStore)
        : this(http, credentialStore, deviceKeyStore, TimeProvider.System) { }

    internal ClientAgentTokenService(HttpClient http, IAgentCredentialStore credentialStore,
        IAgentDeviceKeyStore deviceKeyStore, TimeProvider timeProvider)
    {
        _http = http;
        _credentialStore = credentialStore;
        _deviceKeyStore = deviceKeyStore;
        _timeProvider = timeProvider;
    }

    public async Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = Volatile.Read(ref _cache);
            var now = _timeProvider.GetUtcNow();
            if (previous.Token is not null && previous.ExpiresAtUtc > now.AddMinutes(1))
                return (previous.Token, previous.ExpiresAtUtc);

            try
            {
                var acquired = await AcquireAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var current = Interlocked.CompareExchange(ref _cache, acquired, previous);
                // Conditional invalidation can race acquisition. Never overwrite a
                // different current generation with a late completion.
                if (!ReferenceEquals(current, previous) && current.Token is not null && current.ExpiresAtUtc > _timeProvider.GetUtcNow())
                    return (current.Token, current.ExpiresAtUtc);
                return (acquired.Token!, acquired.ExpiresAtUtc);
            }
            catch (AgentClientAuthException ex) when (!ct.IsCancellationRequested && ex.IsRecoverable && previous.Token is not null &&
                previous.ExpiresAtUtc > _timeProvider.GetUtcNow() && ReferenceEquals(previous, Volatile.Read(ref _cache)))
            {
                // Refresh is proactive while the current token remains authorized.
                // Its original expiry still bounds presence and renewal.
                return (previous.Token, previous.ExpiresAtUtc);
            }
            catch (AgentCredentialStoreException ex)
            {
                throw new AgentClientAuthException("Protected native credential state requires local attention; the installation identity was preserved.",
                    code: "credential_store_integrity", endpointRole: AgentAuthEndpointRole.Token,
                    failureKind: AgentAuthFailureKind.LocalConfiguration, innerException: ex);
            }
        }
        finally { _gate.Release(); }
    }

    public bool InvalidateAccessToken(string rejectedAccessToken)
    {
        var current = Volatile.Read(ref _cache);
        if (current.Token is null || !string.Equals(current.Token, rejectedAccessToken, StringComparison.Ordinal)) return false;
        return ReferenceEquals(Interlocked.CompareExchange(ref _cache, new CacheState(null, DateTimeOffset.MinValue), current), current);
    }

    public void InvalidateCache() => Interlocked.Exchange(ref _cache, new CacheState(null, DateTimeOffset.MinValue));

    public DateTimeOffset GetAuthorityExpiryUtc(string accessToken, DateTimeOffset cacheExpiresAtUtc) =>
        GetJwtExpiry(accessToken) ?? cacheExpiresAtUtc;

    private async Task<CacheState> AcquireAsync(CancellationToken ct)
    {
        var credentials = await _credentialStore.LoadAsync(ct).ConfigureAwait(false);
        if (credentials is null)
            throw new AgentClientAuthException("No local agent credentials found.", code: "credentials_missing",
                endpointRole: AgentAuthEndpointRole.Token, failureKind: AgentAuthFailureKind.LocalConfiguration);
        if (!Guid.TryParse(credentials.Value.AgentId, out var agentId))
            throw new AgentClientAuthException("Stored agent identity is invalid.", code: "agent_id_invalid",
                endpointRole: AgentAuthEndpointRole.Token, failureKind: AgentAuthFailureKind.LocalConfiguration);

        var key = await _deviceKeyStore.GetOrCreateAsync(ct).ConfigureAwait(false);
        var scopes = new[] { "netratel:connect" };
        PendingAgentRefreshExchange? pending = null;
        var exchanges = _credentialStore as IAgentRefreshExchangeStore;
        if (exchanges is not null)
        {
            pending = await exchanges.LoadPendingExchangeAsync(ct).ConfigureAwait(false);
            if (pending is null && await SupportsRefreshExchangeAsync(ct).ConfigureAwait(false))
            {
                pending = await exchanges.BeginRefreshExchangeAsync(new PendingAgentRefreshExchange(1, Guid.NewGuid(),
                    credentials.Value.AgentId, credentials.Value.RefreshToken, GetDeviceKeyHash(key), scopes), ct).ConfigureAwait(false);
            }
            if (pending is not null && (pending.Version != 1 || pending.AgentId != credentials.Value.AgentId ||
                pending.ParentRefreshToken != credentials.Value.RefreshToken || pending.DeviceKeyHash != GetDeviceKeyHash(key) ||
                !pending.RequestedScopes.SequenceEqual(scopes, StringComparer.Ordinal)))
                throw new AgentClientAuthException("Pending native refresh exchange does not match this installation.",
                    code: "refresh_exchange_binding_mismatch", endpointRole: AgentAuthEndpointRole.Token,
                    failureKind: AgentAuthFailureKind.LocalConfiguration);
        }

        using var request = BuildRequestMessage(agentId, credentials.Value.RefreshToken, scopes, key, pending);
        using var response = await SendAsync(request, AgentAuthEndpointRole.Token, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildExceptionAsync(response, AgentAuthEndpointRole.Token, ct).ConfigureAwait(false);
        TokenResponse? dto;
        try { dto = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct).ConfigureAwait(false); }
        catch (JsonException ex) { throw ProtocolFailure("Token response was invalid.", ex); }
        if (dto is null || string.IsNullOrWhiteSpace(dto.AccessToken) || dto.ExpiresIn <= 0 ||
            (pending is not null && (dto.ExchangeId != pending.ExchangeId || string.IsNullOrWhiteSpace(dto.RefreshToken))))
            throw ProtocolFailure("Token response was invalid or did not match the pending exchange.");

        if (pending is not null)
            await exchanges!.CompleteRefreshExchangeAsync(pending, dto.RefreshToken!, ct).ConfigureAwait(false);
        else if (!string.IsNullOrWhiteSpace(dto.RefreshToken))
            await _credentialStore.SaveAsync(credentials.Value.AgentId, dto.RefreshToken, ct).ConfigureAwait(false);
        return new CacheState(dto.AccessToken, GetCacheExpiry(dto.AccessToken, _timeProvider.GetUtcNow().AddSeconds(dto.ExpiresIn)));
    }

    private async Task<bool> SupportsRefreshExchangeAsync(CancellationToken ct)
    {
        if (_supportsRefreshExchange) return true;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agents/token-capabilities");
        using var response = await SendAsync(request, AgentAuthEndpointRole.Capabilities, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            return false;
        if (!response.IsSuccessStatusCode)
            throw await BuildExceptionAsync(response, AgentAuthEndpointRole.Capabilities, ct).ConfigureAwait(false);
        try
        {
            var dto = await response.Content.ReadFromJsonAsync<TokenCapabilities>(cancellationToken: ct).ConfigureAwait(false);
            if (dto is null || dto.RefreshExchangeVersion is not (0 or 1))
                throw ProtocolFailure("Native token capabilities are unsupported.", endpointRole: AgentAuthEndpointRole.Capabilities);
            // Negative negotiation is rechecked before a later legacy acquisition:
            // the backend may be upgraded or rotation enabled while this process lives.
            _supportsRefreshExchange = dto.RefreshExchangeVersion == 1 && dto.RotationEnabled;
            return _supportsRefreshExchange;
        }
        catch (JsonException ex) { throw ProtocolFailure("Native token capabilities were invalid.", ex, AgentAuthEndpointRole.Capabilities); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, AgentAuthEndpointRole role, CancellationToken ct)
    {
        // One actual send per request. Operational recovery owns all scheduling.
        try { return await _http.SendAsync(request, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex)
        {
            var kind = ex.HttpRequestError == HttpRequestError.SecureConnectionError
                ? AgentAuthFailureKind.Trust : AgentAuthFailureKind.Transport;
            throw new AgentClientAuthException("Native authentication endpoint could not be reached.",
                code: kind == AgentAuthFailureKind.Trust ? "tls_trust_failed" : "transport_unavailable", endpointRole: role,
                failureKind: kind, innerException: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AgentClientAuthException("Native authentication request timed out.", code: "request_timeout",
                endpointRole: role, failureKind: AgentAuthFailureKind.Timeout, innerException: ex);
        }
    }

    private async Task<AgentClientAuthException> BuildExceptionAsync(HttpResponseMessage response, AgentAuthEndpointRole role, CancellationToken ct)
    {
        var payload = await TryReadProblemPayloadAsync(response, ct).ConfigureAwait(false);
        var code = payload?.Code;
        if (string.IsNullOrWhiteSpace(code) && response.StatusCode == HttpStatusCode.Unauthorized)
            code = "refresh_token_rejected";
        var correlation = SafeDiagnosticToken(payload?.CorrelationId);
        var safeCode = SafeDiagnosticToken(code);
        var detail = $"Native authentication failed; status={(int)response.StatusCode}";
        if (safeCode is not null) detail += $"; code={safeCode}";
        if (correlation is not null) detail += $"; correlationId={correlation}";
        var hint = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
            ? ReadRetryAfter(response) : (null, false);
        return new AgentClientAuthException(detail, (int)response.StatusCode, code: code, endpointRole: role,
            retryAfter: hint.Item1, retryAfterWasCapped: hint.Item2);
    }

    private (TimeSpan?, bool) ReadRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values)) return (null, false);
        using var iterator = values.GetEnumerator();
        if (!iterator.MoveNext()) return (null, false);
        var value = iterator.Current.Trim();
        if (iterator.MoveNext() || value.Length == 0) return (null, false);
        var digitsOnly = value.All(character => character is >= '0' and <= '9');
        if (digitsOnly)
        {
            if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return (TimeSpan.FromSeconds(600), true);
            if (seconds == 0) return (null, false);
            return (TimeSpan.FromSeconds(Math.Min(seconds, 600UL)), seconds > 600);
        }
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return (null, false);
        var delay = date - _timeProvider.GetUtcNow();
        if (delay <= TimeSpan.Zero) return (null, false);
        var ceiling = TimeSpan.FromSeconds(600);
        return (delay > ceiling ? ceiling : delay, delay > ceiling);
    }

    private static AgentClientAuthException ProtocolFailure(string message, Exception? innerException = null,
        AgentAuthEndpointRole endpointRole = AgentAuthEndpointRole.Token) => new(message, code: "native_protocol_invalid",
            endpointRole: endpointRole, failureKind: AgentAuthFailureKind.Protocol, innerException: innerException);

    private static string GetDeviceKeyHash(AgentDeviceKeyMaterial key) =>
        PopSignatureService.ComputeBodyHash($"{key.Algorithm.ToLowerInvariant()}:{key.PublicKey}");

    private static DateTimeOffset GetCacheExpiry(string accessToken, DateTimeOffset responseExpiry)
    {
        // JWT NumericDate uses whole seconds, while the response hint can retain
        // subsecond precision. Reading exp only shortens cache reuse; the gateway
        // still validates the signature and all authorization claims.
        var jwtExpiry = GetJwtExpiry(accessToken);
        return jwtExpiry is { } expiry && expiry < responseExpiry ? expiry : responseExpiry;
    }

    private static DateTimeOffset? GetJwtExpiry(string accessToken)
    {
        var handler = new JsonWebTokenHandler();
        if (!handler.CanReadToken(accessToken))
        {
            return null;
        }

        try
        {
            var token = handler.ReadJsonWebToken(accessToken);
            if (token.TryGetPayloadValue<long>(JwtRegisteredClaimNames.Exp, out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException or JsonException)
        {
            // Opaque or unreadable tokens retain the endpoint's lifetime hint.
            return null;
        }

        return null;
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

    private HttpRequestMessage BuildRequestMessage(Guid agentId, string refreshToken, string[] scopes,
        AgentDeviceKeyMaterial key, PendingAgentRefreshExchange? pending)
    {
        object dto = pending is null
            ? new { agentId, refreshToken, requestedScopes = scopes }
            : new { agentId, refreshToken, requestedScopes = scopes, exchangeVersion = pending.Version, exchangeId = pending.ExchangeId };
        var bodyHash = pending is null
            ? PopSignatureService.ComputeTokenBodyHash(agentId, refreshToken, scopes)
            : PopSignatureService.ComputeTokenBodyHash(agentId, refreshToken, scopes, pending.Version, pending.ExchangeId);
        var nonce = Guid.NewGuid().ToString("N");
        var timestamp = _timeProvider.GetUtcNow();
        var message = PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/token", timestamp, nonce, bodyHash);
        var signature = PopSignatureService.Sign(key.Algorithm, key.PrivateKey, message);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agents/token")
        {
            Content = new StringContent(JsonSerializer.Serialize(dto), Encoding.UTF8, "application/json")
        };
        // The device signature is sufficient. An optional cached PoP JWT can be
        // expired or invalidated and must never poison refresh acquisition.
        request.Headers.Add("X-NetRatel-Signature", signature);
        request.Headers.Add("X-NetRatel-Nonce", nonce);
        request.Headers.Add("X-NetRatel-Timestamp", timestamp.UtcDateTime.ToString("O"));
        return request;
    }

    private sealed record CacheState(string? Token, DateTimeOffset ExpiresAtUtc);
    private sealed record TokenResponse(string AccessToken, int ExpiresIn, string? RefreshToken, Guid? ExchangeId);
    private sealed record TokenCapabilities([property: JsonRequired] int RefreshExchangeVersion,
        [property: JsonRequired] bool RotationEnabled);
    private sealed record ProblemPayload(string? Title, string? Detail, string? Code, string? CorrelationId);
    private static string? SafeDiagnosticToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? value : null;
}
