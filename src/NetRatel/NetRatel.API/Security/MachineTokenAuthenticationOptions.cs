using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
    public static void Configure(JwtBearerOptions bearer, MachineTokenAuthenticationOptions settings, bool development)
    {
        settings.Validate();
        bearer.MapInboundClaims = false;
        if (!settings.Enabled)
        {
            bearer.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    context.NoResult();
                    return Task.CompletedTask;
                }
            };
            return;
        }

        bearer.Authority = settings.Authority;
        bearer.Audience = settings.Audience;
        bearer.RequireHttpsMetadata = !development;
        bearer.IncludeErrorDetails = development;
        bearer.TokenValidationParameters = CreateValidationParameters(settings);
        bearer.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var groups = context.Principal?.FindAll("groups").Select(c => c.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                if (settings.RequiredGroups.Where(g => !string.IsNullOrWhiteSpace(g)).Any(g => !groups.Contains(g)))
                {
                    context.Fail("Machine token is missing one or more required groups.");
                    return Task.CompletedTask;
                }

                // A multi-audience credential cannot identify a unique credential class.
                // Require one dedicated audience even when it shares the human issuer.
                if (context.Principal?.FindAll("aud").Select(c => c.Value).Distinct().Count() != 1)
                {
                    context.Fail("Machine tokens require exactly one dedicated audience.");
                    return Task.CompletedTask;
                }

                if (context.Principal.Identity is ClaimsIdentity identity)
                {
                    foreach (var role in settings.SessionRoles.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        identity.AddClaim(new Claim(ClaimTypes.Role, role));
                        identity.AddClaim(new Claim("roles", role));
                    }

                    identity.AddClaim(new Claim("auth_mode", "machine_token"));
                    identity.AddClaim(new Claim("identity_provider", "oidc_machine_token"));
                }
                return Task.CompletedTask;
            }
        };
    }

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
