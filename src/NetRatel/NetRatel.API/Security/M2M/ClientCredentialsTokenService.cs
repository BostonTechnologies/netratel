using System.Security.Cryptography;
using System.Text.Json;
using Duende.IdentityModel.Client;

namespace NetRatel.API.Security.M2M;

public interface IClientCredentialsTokenService
{
    Task<string> GetTokenAsync(DownstreamApiOptions target, CancellationToken ct = default);
}

internal sealed class ClientCredentialsTokenService(IHttpClientFactory httpClientFactory) : IClientCredentialsTokenService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _cache = new(StringComparer.Ordinal);

    public async Task<string> GetTokenAsync(DownstreamApiOptions target, CancellationToken ct = default)
    {
        if (target.CurrentAuthority is not null && !await target.CurrentAuthority(ct)) throw new InvalidOperationException("The current downstream service grant is unavailable.");
        if (string.IsNullOrWhiteSpace(target.ClientId) || string.IsNullOrWhiteSpace(target.ClientSecret)) throw new InvalidOperationException("A complete downstream client-credential profile is required.");
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            target.ProfileIdentity, target.ProfileRevision, target.CredentialRevision, target.BaseUrl, target.Authority,
            target.TokenEndpoint, target.ClientId, target.ClientSecret, target.Audience, target.Scope,
            target.PeerInstanceId, target.PeerTenantId, target.LocalTenantId, target.ClientSecretPost
        })));
        await _gate.WaitAsync(ct);
        try
        {
            if (target.CurrentAuthority is not null && !await target.CurrentAuthority(ct)) { _cache.Remove(key); throw new InvalidOperationException("The current downstream service grant is unavailable."); }
            var now = DateTimeOffset.UtcNow;
            if (_cache.TryGetValue(key, out var cached) && cached.Expires - now > TimeSpan.FromSeconds(60)) return cached.Token;
            foreach (var expired in _cache.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) _cache.Remove(expired);
            if (_cache.Count >= 256) _cache.Remove(_cache.OrderBy(x => x.Value.Expires).First().Key);
            var http = httpClientFactory.CreateClient("oidc");
            var tokenEndpoint = target.TokenEndpoint;
            if (string.IsNullOrWhiteSpace(tokenEndpoint))
            {
                if (string.IsNullOrWhiteSpace(target.Authority)) throw new InvalidOperationException("Downstream authority or token endpoint is required.");
                var discovery = await http.GetDiscoveryDocumentAsync(new DiscoveryDocumentRequest { Address = target.Authority, Policy = { RequireHttps = true } }, ct);
                if (discovery.IsError || string.IsNullOrWhiteSpace(discovery.TokenEndpoint)) throw new InvalidOperationException("Downstream token discovery failed.");
                tokenEndpoint = discovery.TokenEndpoint;
            }
            var response = await http.RequestClientCredentialsTokenAsync(new ClientCredentialsTokenRequest
            {
                Address = tokenEndpoint, ClientId = target.ClientId, ClientSecret = target.ClientSecret, Scope = target.Scope,
                ClientCredentialStyle = target.ClientSecretPost ? ClientCredentialStyle.PostBody : ClientCredentialStyle.AuthorizationHeader
            }, ct);
            if (response.IsError || string.IsNullOrWhiteSpace(response.AccessToken) || response.ExpiresIn <= 0) throw new InvalidOperationException("Downstream client-credentials token acquisition failed.");
            if (target.CurrentAuthority is not null && !await target.CurrentAuthority(ct)) throw new InvalidOperationException("The downstream service grant changed during token acquisition.");
            _cache[key] = (response.AccessToken, DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn));
            return response.AccessToken;
        }
        finally { _gate.Release(); }
    }
}
