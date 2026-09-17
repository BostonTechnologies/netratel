using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Services;
using NetRatel.API.Security.M2M;

namespace NetRatel.API.Services.Orchestration;

public sealed class NetRatelSystemTokenService(
    OidcSigningService signingService,
    IOptions<AgentAuthOptions> authOptions,
    IOptions<NetRatelExternalServiceCallbackOptions> callbackOptions,
    IOptions<M2MOptions> m2mOptions) : INetRatelSystemTokenService
{
    private readonly OidcSigningService _signingService = signingService;
    private readonly AgentAuthOptions _authOptions = authOptions.Value;
    private readonly NetRatelExternalServiceCallbackOptions _callbackOptions = callbackOptions.Value;
    private readonly M2MOptions _m2mOptions = m2mOptions.Value;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private string? _cachedToken;
    private string? _cachedAudience;
    private DateTimeOffset _cachedExpiresAt;

    public async Task<string> GetTokenAsync(string audience, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(_cachedToken)
            && string.Equals(_cachedAudience, audience, StringComparison.Ordinal)
            && _cachedExpiresAt - now > TimeSpan.FromMinutes(1))
        {
            return _cachedToken!;
        }

        await _cacheGate.WaitAsync(ct);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(_cachedToken)
                && string.Equals(_cachedAudience, audience, StringComparison.Ordinal)
                && _cachedExpiresAt - now > TimeSpan.FromMinutes(1))
            {
                return _cachedToken!;
            }

            var key = await _signingService.GetActiveSigningKeyAsync(ct);
            var signing = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);
            var expires = now.AddMinutes(15);
            var clientId = string.IsNullOrWhiteSpace(_callbackOptions.ClientId)
                ? "netratel.api"
                : _callbackOptions.ClientId.Trim();

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, clientId),
                new("azp", clientId),
                new("client_id", clientId),
                new("role", "service"),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
            };

            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = ResolveIssuer(),
                Audience = audience,
                Subject = new ClaimsIdentity(claims),
                NotBefore = now.UtcDateTime,
                IssuedAt = now.UtcDateTime,
                Expires = expires.UtcDateTime,
                SigningCredentials = signing
            };

            var handler = new JwtSecurityTokenHandler();
            var token = handler.CreateToken(descriptor);
            _cachedToken = handler.WriteToken(token);
            _cachedAudience = audience;
            _cachedExpiresAt = expires;
            return _cachedToken;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private string ResolveIssuer()
    {
        if (!string.IsNullOrWhiteSpace(_m2mOptions.Authority))
        {
            return _m2mOptions.Authority.TrimEnd('/');
        }

        return _authOptions.Issuer.TrimEnd('/');
    }
}
