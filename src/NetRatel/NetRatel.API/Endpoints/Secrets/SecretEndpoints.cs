using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Models.SecretModels;
using NetRatel.API.Security;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Endpoints;

public static class SecretEndpoints
{
    public static IEndpointRouteBuilder MapSecretEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/secrets")
            .WithTags("Secrets")
            .RequireAuthorization()
            .AddEndpointFilter(LocalCookieMutationFilter.InvokeAsync);

        group.MapGet("/", async (HttpContext http, IEffectiveAccessService access, ISecretService secrets, CancellationToken ct) =>
        {
            var scope = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.SecretReveal, ct);
            if (scope is { Length: 0 })
                return Results.Forbid();

            var payload = (await secrets.ListAsync(scope, ct)).Select(MapResponse);
            return Results.Ok(payload);
        });

        group.MapGet("/{id:int}", async (int id, HttpContext http, IEffectiveAccessService access, ISecretService secrets, CancellationToken ct) =>
        {
            var scope = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.SecretReveal, ct);
            if (scope is { Length: 0 })
                return Results.Forbid();

            var secret = await secrets.GetAsync(id, scope, ct);
            return secret is null ? Results.NotFound() : Results.Ok(MapResponse(secret));
        });

        group.MapPost("/", async ([FromBody] CreateSecretRequest request, HttpContext http, IEffectiveAccessService access,
            ISecretService secrets, CancellationToken ct) =>
        {
            if (!await access.AuthorizeAsync(http.User, NetRatelPermissions.SecretManage, request.TenantId, ct))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(request.Value))
                return Results.BadRequest("Value is required.");

            var created = await secrets.CreateAsync(new CreateSecretCommand(
                request.TenantId, request.ClientIdentity, request.Value, request.Description), ct);
            return Results.Created($"/api/v1/secrets/{created.Id}", created);
        });

        group.MapPut("/{id:int}", async (int id, [FromBody] UpdateSecretRequest request, HttpContext http,
            IEffectiveAccessService access, ISecretService secrets, CancellationToken ct) =>
        {
            var scope = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.SecretManage, ct);
            if (scope is { Length: 0 })
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(request.Value))
                return Results.BadRequest("Value is required.");

            var updated = await secrets.UpdateAsync(new UpdateSecretCommand(id, request.Value, request.Description), scope, ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        group.MapDelete("/{id:int}", async (int id, HttpContext http, IEffectiveAccessService access, ISecretService secrets,
            CancellationToken ct) =>
        {
            var scope = await access.GetAuthorizedTenantIdsAsync(http.User, NetRatelPermissions.SecretManage, ct);
            if (scope is { Length: 0 })
                return Results.Forbid();

            var deleted = await secrets.DeleteAsync(id, scope, ct);
            return deleted is null ? Results.NotFound() : Results.Accepted($"/api/v1/secrets/{id}", deleted);
        });

        return app;
    }

    private static SecretDto MapResponse(SecretInfo secret) =>
        new(secret.Id, secret.TenantId, secret.ClientIdentity, secret.Value, secret.Description, secret.CreatedAtUtc, secret.UpdatedAtUtc);
}
