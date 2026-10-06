using NetRatel.Infrastructure.ServiceIdentity;

namespace NetRatel.API.Endpoints.Auth;

public static class ServiceIdentityMetadataEndpoints
{
    public static IEndpointRouteBuilder MapServiceIdentityMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/oauth-authorization-server", async (IServiceIdentityRuntimeOptions runtime, CancellationToken ct) =>
        {
            var options = await runtime.GetAsync(ct);
            if (!options.Enabled) return Results.StatusCode(503);
            return Results.Ok(new
            {
                issuer = options.Issuer,
                token_endpoint = options.ApiBaseUrl.TrimEnd('/') + "/connect/token",
                jwks_uri = options.ApiBaseUrl.TrimEnd('/') + "/.well-known/service-jwks.json",
                grant_types_supported = new[] { "client_credentials" }, token_endpoint_auth_methods_supported = new[] { "client_secret_post" },
                scopes_supported = ServiceIdentityScopes.All, token_endpoint_auth_signing_alg_values_supported = new[] { "RS256" }
            });
        }).AllowAnonymous().WithTags("Service identity");
        app.MapGet("/.well-known/service-jwks.json", async (ServiceSigningKeyStore keys, CancellationToken ct) =>
        {
            try { return Results.Ok(await keys.GetJwksAsync(ct)); }
            catch (ServiceSigningKeyUnavailableException) { return Results.StatusCode(503); }
        }).AllowAnonymous().WithTags("Service identity");
        return app;
    }
}
