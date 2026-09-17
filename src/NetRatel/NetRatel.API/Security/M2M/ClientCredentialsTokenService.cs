using Duende.IdentityModel.Client;

namespace NetRatel.API.Security.M2M;

public interface IClientCredentialsTokenService
{
    Task<string> GetTokenAsync(DownstreamApiOptions target, CancellationToken ct = default);
}

internal sealed class ClientCredentialsTokenService : IClientCredentialsTokenService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresAt;

    public ClientCredentialsTokenService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetTokenAsync(DownstreamApiOptions target, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedToken is not null && _expiresAt - now > TimeSpan.FromSeconds(60))
            return _cachedToken;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && _expiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60))
                return _cachedToken;

            var http = _httpClientFactory.CreateClient("oidc");
            string tokenEndpoint;

            if (!string.IsNullOrWhiteSpace(target.TokenEndpoint))
            {
                tokenEndpoint = target.TokenEndpoint;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(target.Authority))
                    throw new InvalidOperationException("Downstream Authority or TokenEndpoint is required to acquire a client-credentials token.");

                var disco = await http.GetDiscoveryDocumentAsync(new DiscoveryDocumentRequest
                {
                    Address = target.Authority,
                    Policy = { RequireHttps = true }
                }, ct);
                if (disco.IsError || string.IsNullOrWhiteSpace(disco.TokenEndpoint))
                    throw new InvalidOperationException($"Discovery failed: {disco.Error}");
                tokenEndpoint = disco.TokenEndpoint;
            }

            if (string.IsNullOrWhiteSpace(target.ClientId) || string.IsNullOrWhiteSpace(target.ClientSecret))
                throw new InvalidOperationException("Downstream client credentials (ClientId/ClientSecret) are required for client-credentials token acquisition.");

            var resp = await http.RequestClientCredentialsTokenAsync(new ClientCredentialsTokenRequest
            {
                Address = tokenEndpoint,
                ClientId = target.ClientId,
                ClientSecret = target.ClientSecret,
                Scope = target.Scope
            }, ct);

            if (resp.IsError)
                throw new InvalidOperationException($"Token request failed: {resp.Error}");

            _cachedToken = resp.AccessToken!;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(resp.ExpiresIn);
            return _cachedToken!;
        }
        finally
        {
            _gate.Release();
        }
    }
}
