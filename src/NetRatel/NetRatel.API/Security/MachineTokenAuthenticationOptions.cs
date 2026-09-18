using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace NetRatel.API.Security;

internal sealed class MachineTokenAuthenticationOptions
{
    public bool Enabled { get; init; }
    public string Authority { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string[] RequiredGroups { get; init; } = [];
    public string[] SessionRoles { get; init; } = [];
    public string[] AllowedSigningAlgorithms { get; init; } = ["RS256", "ES256"];

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        var errors = new List<string>();
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority) || authority.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("Authentication:MachineToken:Authority must be an absolute HTTPS URI when enabled.");
        }

        if (string.IsNullOrWhiteSpace(Audience))
        {
            errors.Add("Authentication:MachineToken:Audience is required when enabled.");
        }

        if (RequiredGroups.All(string.IsNullOrWhiteSpace))
        {
            errors.Add("Authentication:MachineToken:RequiredGroups must contain at least one group when enabled.");
        }

        if (AllowedSigningAlgorithms.All(string.IsNullOrWhiteSpace))
        {
            errors.Add("Authentication:MachineToken:AllowedSigningAlgorithms must contain at least one algorithm when enabled.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }
    }
}

internal static class MachineTokenAuthentication
{
    /// <summary>
    /// Determines only the receiving scheme from untrusted JWT metadata.
    /// Authentication still occurs in JwtBearer using signature validation.
    /// </summary>
    public static bool IsCandidate(JwtSecurityToken token, MachineTokenAuthenticationOptions options)
        => options.Enabled
           && !string.IsNullOrWhiteSpace(options.Authority)
           && string.Equals(token.Issuer.TrimEnd('/'), options.Authority.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
           && token.Audiences.Contains(options.Audience, StringComparer.Ordinal);

    public static TokenValidationParameters CreateValidationParameters(MachineTokenAuthenticationOptions options)
    {
        var authority = options.Authority.TrimEnd('/');
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = [authority, $"{authority}/"],
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidAlgorithms = options.AllowedSigningAlgorithms,
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = "preferred_username",
            ClockSkew = TimeSpan.FromMinutes(10)
        };
    }
}
