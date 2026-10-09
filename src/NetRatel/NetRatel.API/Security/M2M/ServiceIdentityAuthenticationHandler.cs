using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.SystemPairing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace NetRatel.API.Security.M2M;

public sealed class ServiceIdentityAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger, UrlEncoder encoder, ServiceSigningKeyStore keys, IServicePrincipalRegistry registry,
    IServiceIdentityRuntimeOptions issuerOptions, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string SchemeName = "ManagedService";

    /// <summary>Routing is only parsing. This dedicated handler then validates every cryptographic/current grant boundary.</summary>
    public static bool SelectServiceIssuer(JwtSecurityToken token, string configuredIssuer) =>
        token.Claims.Any(x => x.Type == "token_use" && x.Value == ServiceIdentityClaims.Purpose);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header[7..].Trim();
        if (token.Length is < 20 or > 16384) return AuthenticateResult.Fail("Invalid service token.");
        try
        {
            var settings = await issuerOptions.GetAsync(Context.RequestAborted);
            if (!settings.Enabled) return AuthenticateResult.Fail("Service issuer disabled.");
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 16384 };
            var jwt = handler.ReadJwtToken(token);
            if (jwt.Header.Alg != SecurityAlgorithms.RsaSha256 || jwt.Header.Typ != "at+jwt" || string.IsNullOrWhiteSpace(jwt.Header.Kid)) return AuthenticateResult.Fail("Invalid service token type or signing algorithm.");
            if (jwt.Audiences.Count() != 1 || !string.Equals(jwt.Audiences.Single(), settings.Audience, StringComparison.Ordinal))
                return AuthenticateResult.Fail("Wrong service audience.");
            var validationKeys = await keys.GetValidationKeysAsync(Context.RequestAborted);
            var selected = validationKeys.SingleOrDefault(x => x.KeyId == jwt.Header.Kid);
            if (selected is null) return AuthenticateResult.Fail("Unknown service signing key.");
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = settings.Issuer,
                ValidateAudience = true, ValidAudience = settings.Audience,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidateIssuerSigningKey = true, IssuerSigningKey = selected,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], RequireSignedTokens = true,
                ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(settings.ClockSkewSeconds),
                LifetimeValidator = (notBefore, expires, _, _) => notBefore.HasValue && expires.HasValue &&
                    notBefore.Value <= time.GetUtcNow().UtcDateTime.AddSeconds(settings.ClockSkewSeconds) &&
                    expires.Value > time.GetUtcNow().UtcDateTime.AddSeconds(-settings.ClockSkewSeconds) &&
                    expires.Value > notBefore.Value && (expires.Value - notBefore.Value).TotalSeconds <= settings.AccessTokenLifetimeSeconds,
                NameClaimType = "client_id", RoleClaimType = "service_role_unused"
            }, out _);
            foreach (var name in new[] { "sub", "client_id", "token_use", "auth_mode", ServiceIdentityClaims.PrincipalId, ServiceIdentityClaims.CredentialRevision, ServiceIdentityClaims.GrantRevision,
                         ServiceIdentityClaims.TenantId, ServiceIdentityClaims.PeerInstanceId, ServiceIdentityClaims.PeerTenantId, ServiceIdentityClaims.LinkRevision, "scope", "target_instance_id", "target_tenant_id", "caller_instance_id", "caller_tenant_id" })
                if (principal.FindAll(name).Count() != 1) return AuthenticateResult.Fail("Ambiguous service identity.");
            foreach (var name in new[] { ServiceIdentityClaims.LinkId, ServiceIdentityClaims.GrantHash })
                if (principal.FindAll(name).Count() != 1) return AuthenticateResult.Fail("Ambiguous connection binding.");
            if (principal.FindFirstValue("target_instance_id") != settings.InstanceId || principal.FindFirstValue("target_tenant_id") != principal.FindFirstValue(ServiceIdentityClaims.TenantId) ||
                principal.FindFirstValue("caller_instance_id") != principal.FindFirstValue(ServiceIdentityClaims.PeerInstanceId) || principal.FindFirstValue("caller_tenant_id") != principal.FindFirstValue(ServiceIdentityClaims.PeerTenantId)) return AuthenticateResult.Fail("Wrong service instance or tenant.");
            if (await registry.ResolvePrincipalAsync(principal, null, Context.RequestAborted) is null) return AuthenticateResult.Fail("The current service grant is unavailable.");
            return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException or InvalidOperationException or PairingException or ServiceClientConflictException)
        {
            return AuthenticateResult.Fail("Invalid or unavailable service authentication.");
        }
    }
}
