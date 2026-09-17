using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NetRatel.Web.Configuration;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace NetRatel.Web.Services.Authentication;

public interface IMachineTokenValidator
{
    Task<MachineTokenValidationResult> ValidateAsync(string token, CancellationToken cancellationToken = default);
}

public sealed record MachineTokenValidationResult(ClaimsPrincipal Principal, DateTimeOffset ExpiresAt);

public sealed class OidcMachineTokenValidator : IMachineTokenValidator
{
    private readonly MachineTokenOptions _options;
    private readonly IConfigurationManager<OpenIdConnectConfiguration>? _configurationManager;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public OidcMachineTokenValidator(IOptions<MachineTokenOptions> options)
    {
        _options = options.Value;

        if (_options.Enabled && !string.IsNullOrWhiteSpace(_options.Authority))
        {
            var metadataAddress = $"{_options.Authority.TrimEnd('/')}/.well-known/openid-configuration";
            _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever());
        }
    }

    public async Task<MachineTokenValidationResult> ValidateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            throw new SecurityTokenValidationException("Machine-token authentication is disabled.");
        }

        if (_configurationManager is null)
        {
            throw new SecurityTokenValidationException("Machine-token OIDC discovery is not configured.");
        }

        var oidc = await _configurationManager.GetConfigurationAsync(cancellationToken);
        var authority = _options.Authority.TrimEnd('/');
        var principal = _tokenHandler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = [authority, $"{authority}/"],
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = oidc.SigningKeys,
            ValidateLifetime = true,
            ValidAlgorithms = _options.AllowedSigningAlgorithms,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = "preferred_username",
            RoleClaimType = ClaimTypes.Role
        }, out var validatedToken);

        EnsureRequiredGroups(principal, _options.RequiredGroups);

        var identity = new ClaimsIdentity(
            principal.Claims.Select(claim => new Claim(claim.Type, claim.Value)),
            CookieAuthenticationDefaults.AuthenticationScheme,
            "preferred_username",
            ClaimTypes.Role);

        foreach (var role in _options.SessionRoles.Where(role => !string.IsNullOrWhiteSpace(role)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
            identity.AddClaim(new Claim("roles", role));
        }

        identity.AddClaim(new Claim("auth_mode", "machine_token"));
        identity.AddClaim(new Claim("identity_provider", "oidc_machine_token"));

        var expiresAt = validatedToken switch
        {
            JwtSecurityToken jwt => new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero),
            JsonWebToken jsonWebToken => new DateTimeOffset(jsonWebToken.ValidTo, TimeSpan.Zero),
            _ => DateTimeOffset.UtcNow.AddMinutes(15)
        };

        return new MachineTokenValidationResult(new ClaimsPrincipal(identity), expiresAt);
    }

    private static void EnsureRequiredGroups(ClaimsPrincipal principal, IEnumerable<string> requiredGroups)
    {
        var actualGroups = principal.FindAll("groups").Select(claim => claim.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingGroups = requiredGroups
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Where(group => !actualGroups.Contains(group))
            .ToArray();

        if (missingGroups.Length > 0)
        {
            throw new SecurityTokenValidationException(
                $"Machine token is missing required groups: {string.Join(", ", missingGroups)}");
        }
    }
}
