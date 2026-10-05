using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.WebUtilities;
using NetRatel.API.Security.M2M;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.Services;

namespace NetRatel.API.Endpoints;

public static class M2MTokenEndpoints
{
    public static IEndpointRouteBuilder MapM2MTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/token", async (HttpContext ctx, IM2MDeploymentProfileResolver deployment,
            IServicePrincipalRegistry registry, ServiceAccessTokenService issuer, OrchestratorDbContext db,
            OidcSigningService legacySigner, TimeProvider clock, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.Pragma = "no-cache";
            if (ctx.Request.ContentLength > 8192 || !string.Equals(ctx.Request.ContentType?.Split(';')[0].Trim(), "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "invalid_request" });
            Dictionary<string, Microsoft.Extensions.Primitives.StringValues> form;
            try
            {
                var buffer = new byte[8193]; var length = 0;
                while (length < buffer.Length)
                {
                    var count = await ctx.Request.Body.ReadAsync(buffer.AsMemory(length), ct);
                    if (count == 0) break; length += count;
                }
                if (length > 8192) return Results.BadRequest(new { error = "invalid_request" });
                form = QueryHelpers.ParseQuery(new UTF8Encoding(false, true).GetString(buffer, 0, length));
                if (form.Count > 4 || form.Any(x => x.Key.Length > 128 || x.Value.Any(v => v?.Length > 1024))) return Results.BadRequest(new { error = "invalid_request" });
            }
            catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException or ArgumentException)
            { return Results.BadRequest(new { error = "invalid_request" }); }
            if (form.Keys.Any(x => x is not ("grant_type" or "client_id" or "client_secret" or "scope")) ||
                new[] { "grant_type", "client_id", "client_secret", "scope" }.Any(x => !form.TryGetValue(x, out var field) || field.Count != 1)) return Results.BadRequest(new { error = "invalid_request" });
            if (form["grant_type"] != "client_credentials") return Results.BadRequest(new { error = "unsupported_grant_type" });
            var clientId = form["client_id"].ToString(); var secret = form["client_secret"].ToString();
            var scopes = form["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!ServicePrincipalRegistry.IsValidClientId(clientId) || secret.Length is < 1 or > 1024) return InvalidClient();
            if (scopes.Length == 0 || scopes.Length > 16 || scopes.Any(x => x.Length > 128) || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length) return Results.BadRequest(new { error = "invalid_scope" });
            try
            {
                var legacy = deployment.Resolve(clientId);
                var normalized = clientId.ToUpperInvariant(); var alias = ServicePrincipalRegistry.AliasKey(clientId);
                var managedExists = await db.Set<ServicePrincipalRegistration>().AsNoTracking().AnyAsync(x => x.NormalizedClientId == normalized || x.AliasKey == alias, ct);
                if (deployment.OwnsIdentity(clientId) && managedExists) return InvalidClient();
                if (legacy is not null)
                {
                    if (!SecureEquals(secret, legacy.Secret) || legacy.AllowedAudiences.Length > 0 && !legacy.AllowedAudiences.Contains(legacy.Audience, StringComparer.Ordinal)) return InvalidClient();
                    if (!scopes.Contains(legacy.Audience, StringComparer.Ordinal) || legacy.AllowedScopes.Length > 0 && scopes.Any(x => !legacy.AllowedScopes.Contains(x, StringComparer.Ordinal))) return Results.BadRequest(new { error = "invalid_scope" });
                    var now = clock.GetUtcNow(); var expires = now.AddMinutes(Math.Clamp(legacy.AccessTokenLifetimeMinutes, 5, 10));
                    var key = await legacySigner.GetActiveSigningKeyAsync(ct);
                    var jwt = new JwtSecurityToken(legacy.Authority.TrimEnd('/'), legacy.Audience,
                        [new("sub", legacy.ClientId), new("client_id", legacy.ClientId), new("scope", string.Join(' ', scopes)), new("jti", Guid.NewGuid().ToString("N")),
                         new("iat", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)],
                        now.UtcDateTime, expires.UtcDateTime, new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256));
                    return Results.Ok(new { access_token = new JwtSecurityTokenHandler().WriteToken(jwt), token_type = "Bearer", expires_in = (int)(expires - now).TotalSeconds });
                }
                var client = await registry.AuthenticateClientAsync(clientId, secret, ct);
                if (client is null) return InvalidClient();
                var token = await issuer.IssueAsync(client, scopes, ct);
                return Results.Ok(new { access_token = token.AccessToken, token_type = "Bearer", expires_in = token.ExpiresIn, scope = token.Scope });
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_scope" }); }
            catch (ServiceClientConflictException) { return InvalidClient(); }
            catch (ServiceSigningKeyUnavailableException) { return Results.Json(new { error = "temporarily_unavailable" }, statusCode: 503); }
        }).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(ServiceIdentityServiceCollectionExtensions.SensitiveRateLimiter).WithTags("OIDC");
        return app;
    }
    private static IResult InvalidClient() => Results.Json(new { error = "invalid_client" }, statusCode: 401);
    private static bool SecureEquals(string provided, string expected)
    {
        var left = Encoding.UTF8.GetBytes(provided); var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
