using System.Linq;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using NetRatel.Application.Common;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Services;

namespace NetRatel.API.Endpoints.Systems;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/system").WithTags("System");

        group.MapGet("/version", (IHostEnvironment environment) =>
        {
            var assembly = typeof(SystemEndpoints).Assembly;
            var informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            var fullVersion = string.IsNullOrWhiteSpace(informationalVersion)
                ? assembly.GetName().Version?.ToString() ?? "dev"
                : informationalVersion;

            return Results.Ok(new
            {
                serviceName = "NetRatel.API",
                displayVersion = FormatDisplayVersion(fullVersion),
                informationalVersion = fullVersion,
                assemblyVersion = assembly.GetName().Version?.ToString() ?? "unknown",
                environment = environment.EnvironmentName
            });
        }).AllowAnonymous();

        group.MapGet("/m2m/ping", (HttpContext ctx) =>
        {
            var user = ctx.User;
            var issuer = user.FindFirst("iss")?.Value;
            var audienceValues = user.FindAll("aud").Select(c => c.Value).ToArray();
            var audience = audienceValues.Length switch
            {
                0 => null,
                1 => audienceValues[0],
                _ => string.Join(' ', audienceValues)
            };
            var clientId = user.FindFirst("client_id")?.Value
                ?? user.FindFirst("azp")?.Value
                ?? user.FindFirst("sub")?.Value;

            var claims = user.Claims
                .GroupBy(c => c.Type, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToArray(), StringComparer.Ordinal);

            return Results.Ok(new
            {
                issuer,
                audience,
                client_id = clientId,
                claims
            });
        })
        .RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = "M2M",
            Policy = "M2MOnly"
        });

        group.MapPost("/token", (
            HttpContext http,
            IConfiguration configuration,
            IHostEnvironment environment) =>
        {
            if (!environment.IsDevelopment() || !configuration.GetValue<bool>("DevelopmentOperator:Enabled"))
            {
                return Results.NotFound();
            }

            var providedSecret = http.Request.Headers["X-System-Secret"].FirstOrDefault();
            var expectedSecret = configuration["SystemTokenSecret"];
            if (string.IsNullOrWhiteSpace(expectedSecret) ||
                !string.Equals(providedSecret, expectedSecret, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            var issuer = configuration["SystemToken:Issuer"];
            var audience = configuration["SystemToken:Audience"];
            var secret = configuration["SystemTokenSecret"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(issuer) ||
                string.IsNullOrWhiteSpace(audience) ||
                string.IsNullOrWhiteSpace(secret))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "System token configuration is incomplete.");
            }

            var displayName = configuration["DevelopmentOperator:DisplayName"] ?? "Local Development Operator";
            var preferredUsername = configuration["DevelopmentOperator:PreferredUsername"] ?? "operator@localhost";
            var subject = configuration["DevelopmentOperator:Subject"] ?? "development-operator";
            var groups = configuration.GetSection("DevelopmentOperator:Groups").Get<string[]>() ?? ["Operator"];
            var lifetimeMinutes = Math.Max(5, configuration.GetValue<int?>("DevelopmentOperator:TokenLifetimeMinutes") ?? 30);
            var now = DateTimeOffset.UtcNow;

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, subject),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
                new("preferred_username", preferredUsername),
                new("name", displayName),
                new("auth_mode", "development"),
                new("token_use", "system")
            };

            foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                claims.Add(new Claim("groups", group));
                claims.Add(new Claim("roles", group));
            }

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var expires = now.AddMinutes(lifetimeMinutes);

            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Issuer = issuer,
                Audience = audience,
                NotBefore = now.UtcDateTime,
                Expires = expires.UtcDateTime,
                SigningCredentials = credentials
            };

            var handler = new JwtSecurityTokenHandler();
            var token = handler.CreateToken(descriptor);

            return Results.Ok(new
            {
                token = handler.WriteToken(token),
                expiresAt = expires
            });
        })
        .AllowAnonymous();

        group.MapPost("/signing-key", async (
            IFormFile file,
            IOptions<StorageOptions> storageOptions,
            CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest("File required.");
            }

            if (file.Length > 10_000)
            {
                return Results.BadRequest("File too large.");
            }

            await using var input = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct);

            var bytes = buffer.ToArray();
            var pem = System.Text.Encoding.UTF8.GetString(bytes);
            if (!pem.Contains("BEGIN", StringComparison.Ordinal))
            {
                return Results.BadRequest("Invalid PEM content.");
            }

            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(pem);
            }
            catch (CryptographicException)
            {
                return Results.BadRequest("Invalid EC private key.");
            }

            var keyDir = Path.Combine(storageOptions.Value.RootPath, "keys");
            Directory.CreateDirectory(keyDir);

            var path = Path.Combine(keyDir, "netratel-agent-es256-private.pem");
            var tempPath = path + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, ct);
            File.Move(tempPath, path, overwrite: true);

            return Results.NoContent();
        })
        .RequireAuthorization("Operator")
        .DisableAntiforgery()
        .Accepts<IFormFile>("multipart/form-data")
        .Produces(StatusCodes.Status204NoContent)
        .Produces<string>(StatusCodes.Status400BadRequest);

        app.MapGet("/debug/jwks-test", async (
            OidcSigningService signingService,
            IAgentTokenService tokenService,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("JwksDebug");
            var key = await signingService.GetActiveSigningKeyAsync(ct);
            var parameters = key.ECDsa.ExportParameters(false);

            var derived = new
            {
                kid = key.KeyId ?? string.Empty,
                alg = "ES256",
                crv = "P-256",
                x = Base64UrlEncoder.Encode(parameters.Q.X),
                y = Base64UrlEncoder.Encode(parameters.Q.Y)
            };

            var served = await tokenService.GetJwksAsync(ct);
            var servedKey = served.Keys.FirstOrDefault();
            var match = servedKey is not null &&
                        string.Equals(servedKey.Kid, derived.kid, StringComparison.Ordinal) &&
                        string.Equals(servedKey.Alg, derived.alg, StringComparison.Ordinal) &&
                        string.Equals(servedKey.Crv, derived.crv, StringComparison.Ordinal) &&
                        string.Equals(servedKey.X, derived.x, StringComparison.Ordinal) &&
                        string.Equals(servedKey.Y, derived.y, StringComparison.Ordinal);

            logger.LogInformation(
                "JWKS debug compare: match={Match}, derived(kid={DerivedKid},alg={DerivedAlg},crv={DerivedCrv},x={DerivedX},y={DerivedY}), served(kid={ServedKid},alg={ServedAlg},crv={ServedCrv},x={ServedX},y={ServedY})",
                match,
                derived.kid,
                derived.alg,
                derived.crv,
                derived.x,
                derived.y,
                servedKey?.Kid,
                servedKey?.Alg,
                servedKey?.Crv,
                servedKey?.X,
                servedKey?.Y);

            return Results.Ok(new
            {
                match,
                derived,
                served = servedKey
            });
        })
        .RequireAuthorization("Operator")
        .WithTags("Debug");

        return app;
    }

    private static string FormatDisplayVersion(string value)
    {
        var trimmed = value.Trim();
        var metadataStart = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (metadataStart >= 0)
        {
            trimmed = trimmed[..metadataStart];
        }

        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"v{trimmed}";
    }
}
