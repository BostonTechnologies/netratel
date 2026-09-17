using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NetRatel.API.Security.M2M;
using NetRatel.Infrastructure.Services;

namespace NetRatel.API.Endpoints;

public static class M2MTokenEndpoints
{
    public static IEndpointRouteBuilder MapM2MTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/token", async (
            HttpContext ctx,
            IConfiguration cfg,
            OidcSigningService signingService,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("NetRatel.API.M2MToken");
            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "invalid_request", error_description = "Expected application/x-www-form-urlencoded body." });
            }

            var form = await ctx.Request.ReadFormAsync(ct);
            var grantType = form["grant_type"].ToString().Trim();
            var clientId = form["client_id"].ToString().Trim();
            var clientSecret = form["client_secret"].ToString();
            var scopeRaw = form["scope"].ToString().Trim();

            if (!string.Equals(grantType, "client_credentials", StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "unsupported_grant_type" });
            }

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                logger.LogWarning("M2M token rejected: missing client id or secret. clientIdPresent={ClientIdPresent}, secretPresent={SecretPresent}", !string.IsNullOrWhiteSpace(clientId), !string.IsNullOrWhiteSpace(clientSecret));
                return Results.Unauthorized();
            }

            if (!TryGetClientRegistration(cfg, clientId, out var registration) || registration is null)
            {
                logger.LogWarning("M2M token rejected: client registration not found. clientId={ClientId}", clientId);
                return Results.Unauthorized();
            }

            if (!SecureEquals(clientSecret, registration.Secret))
            {
                logger.LogWarning(
                    "M2M token rejected: client secret mismatch. clientId={ClientId}, providedLength={ProvidedLength}, configuredLength={ConfiguredLength}, allowedAudiences={AllowedAudienceCount}, allowedScopes={AllowedScopeCount}",
                    clientId,
                    clientSecret.Length,
                    registration.Secret?.Length ?? 0,
                    registration.AllowedAudiences.Length,
                    registration.AllowedScopes.Length);
                return Results.Unauthorized();
            }

            var m2m = cfg.GetSection("M2M").Get<M2MOptions>();
            if (m2m is null || string.IsNullOrWhiteSpace(m2m.Authority) || string.IsNullOrWhiteSpace(m2m.Audience))
            {
                logger.LogError("M2M token rejected: M2M configuration is incomplete.");
                return Results.Problem("M2M configuration is incomplete.", statusCode: StatusCodes.Status500InternalServerError);
            }

            var requestedScopes = scopeRaw
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (requestedScopes.Length == 0)
            {
                return Results.BadRequest(new { error = "invalid_scope", error_description = "At least one scope is required." });
            }

            if (!requestedScopes.Contains(m2m.Audience, StringComparer.Ordinal))
            {
                return Results.BadRequest(new { error = "invalid_scope", error_description = "Requested scope does not match NetRatel M2M audience." });
            }

            if (registration.AllowedScopes.Length > 0 &&
                requestedScopes.Any(s => !registration.AllowedScopes.Contains(s, StringComparer.Ordinal)))
            {
                return Results.BadRequest(new { error = "invalid_scope", error_description = "Requested scope is not allowed for this client." });
            }

            if (registration.AllowedAudiences.Length > 0 &&
                !registration.AllowedAudiences.Contains(m2m.Audience, StringComparer.Ordinal))
            {
                logger.LogWarning("M2M token rejected: audience not allowed. clientId={ClientId}, audience={Audience}, allowedAudiences={AllowedAudiences}", clientId, m2m.Audience, string.Join(",", registration.AllowedAudiences));
                return Results.Unauthorized();
            }

            var now = DateTimeOffset.UtcNow;
            var lifetimeMinutes = Math.Clamp(m2m.AccessTokenLifetimeMinutes, 5, 10);
            var expires = now.AddMinutes(lifetimeMinutes);
            var signingKey = await signingService.GetActiveSigningKeyAsync(ct);
            var signing = new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256);

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, clientId),
                new("client_id", clientId),
                new("scope", string.Join(' ', requestedScopes)),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
            };

            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = m2m.Authority.TrimEnd('/'),
                Audience = m2m.Audience,
                Subject = new ClaimsIdentity(claims),
                NotBefore = now.UtcDateTime,
                IssuedAt = now.UtcDateTime,
                Expires = expires.UtcDateTime,
                SigningCredentials = signing
            };

            var handler = new JwtSecurityTokenHandler();
            var token = handler.CreateToken(descriptor);
            var accessToken = handler.WriteToken(token);

            return Results.Ok(new
            {
                access_token = accessToken,
                token_type = "Bearer",
                expires_in = (int)(expires - now).TotalSeconds
            });
        })
        .AllowAnonymous()
        .WithTags("OIDC");

        return app;
    }

    private static bool SecureEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided ?? string.Empty);
        var expectedBytes = Encoding.UTF8.GetBytes(expected ?? string.Empty);
        if (providedBytes.Length != expectedBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static bool TryGetClientRegistration(IConfiguration cfg, string clientId, out M2MClientRegistration? registration)
    {
        var clients = cfg.GetSection("M2MClients").Get<Dictionary<string, M2MClientRegistration>>(o => o.ErrorOnUnknownConfiguration = false)
            ?? new Dictionary<string, M2MClientRegistration>(StringComparer.Ordinal);
        if (clients.TryGetValue(clientId, out registration) && registration is not null && !string.IsNullOrWhiteSpace(registration.Secret))
        {
            registration = ApplyExactEnvironmentOverrides(clientId, registration);
            return true;
        }

        var section = cfg.GetSection($"M2MClients:{clientId}");
        if (section.Exists())
        {
            registration = section.Get<M2MClientRegistration>(o => o.ErrorOnUnknownConfiguration = false);
            if (registration is not null && !string.IsNullOrWhiteSpace(registration.Secret))
            {
                registration = ApplyExactEnvironmentOverrides(clientId, registration);
                return true;
            }
        }

        registration = BuildRegistrationFromExactEnvironment(clientId);
        return registration is not null && !string.IsNullOrWhiteSpace(registration.Secret);
    }

    private static M2MClientRegistration ApplyExactEnvironmentOverrides(string clientId, M2MClientRegistration registration)
    {
        var secret = ReadClientEnvironmentValue(clientId, "Secret");
        var allowedAudiences = ReadClientEnvironmentArray(clientId, "AllowedAudiences");
        var allowedScopes = ReadClientEnvironmentArray(clientId, "AllowedScopes");

        return new M2MClientRegistration
        {
            Secret = string.IsNullOrWhiteSpace(secret) ? registration.Secret : secret,
            AllowedAudiences = allowedAudiences.Length == 0 ? registration.AllowedAudiences : allowedAudiences,
            AllowedScopes = allowedScopes.Length == 0 ? registration.AllowedScopes : allowedScopes
        };
    }

    private static M2MClientRegistration? BuildRegistrationFromExactEnvironment(string clientId)
    {
        var secret = ReadClientEnvironmentValue(clientId, "Secret");
        if (string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        return new M2MClientRegistration
        {
            Secret = secret,
            AllowedAudiences = ReadClientEnvironmentArray(clientId, "AllowedAudiences"),
            AllowedScopes = ReadClientEnvironmentArray(clientId, "AllowedScopes")
        };
    }

    private static string? ReadClientEnvironmentValue(string clientId, string name)
    {
        foreach (var keyPrefix in GetClientEnvironmentPrefixes(clientId))
        {
            var value = Environment.GetEnvironmentVariable($"{keyPrefix}__{name}");
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string[] ReadClientEnvironmentArray(string clientId, string name)
    {
        foreach (var keyPrefix in GetClientEnvironmentPrefixes(clientId))
        {
            var values = ReadExactEnvironmentArray($"{keyPrefix}__{name}");
            if (values.Length > 0)
            {
                return values;
            }
        }

        return Array.Empty<string>();
    }

    private static IEnumerable<string> GetClientEnvironmentPrefixes(string clientId)
    {
        yield return $"M2MClients__{clientId}";

        var normalized = new string(clientId.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (!string.Equals(normalized, clientId, StringComparison.Ordinal))
        {
            yield return $"M2MClients__{normalized}";
        }
    }

    private static string[] ReadExactEnvironmentArray(string prefix)
    {
        return Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(entry => new
            {
                Key = entry.Key?.ToString() ?? string.Empty,
                Value = entry.Value?.ToString() ?? string.Empty
            })
            .Select(entry => new
            {
                entry.Value,
                Match = System.Text.RegularExpressions.Regex.Match(
                    entry.Key,
                    "^" + System.Text.RegularExpressions.Regex.Escape(prefix) + "__(?<index>\\d+)$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            })
            .Where(entry => entry.Match.Success && !string.IsNullOrWhiteSpace(entry.Value))
            .OrderBy(entry => int.Parse(entry.Match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Select(entry => entry.Value)
            .ToArray();
    }
}
