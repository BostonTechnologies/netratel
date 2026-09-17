using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Models.SecretModels;
using NetRatel.Application.Secrets;

namespace NetRatel.API.Endpoints;

public static class SecretEndpoints
{
    public static IEndpointRouteBuilder MapSecretEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/secrets")
            .WithTags("Secrets")
            .RequireAuthorization("Operator");

        group.MapGet("/", async (ISecretService secrets, CancellationToken ct) =>
        {
            var payload = (await secrets.ListAsync(ct)).Select(MapResponse);
            return Results.Ok(payload);
        });

        group.MapGet("/{id:int}", async (int id, ISecretService secrets, CancellationToken ct) =>
        {
            var secret = await secrets.GetAsync(id, ct);
            return secret is null ? Results.NotFound() : Results.Ok(MapResponse(secret));
        });

        group.MapPost("/", async ([FromBody] CreateSecretRequest request, ISecretService secrets, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Value))
            {
                return Results.BadRequest("Value is required.");
            }

            try
            {
                var created = await secrets.CreateAsync(new CreateSecretCommand(
                    request.TenantId,
                    request.ClientIdentity?.Trim(),
                    request.Value,
                    request.Description?.Trim()), ct);
                return Results.Created($"/api/v1/secrets/{created.Id}", MapResponse(created));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPut("/{id:int}", async (int id, [FromBody] UpdateSecretRequest request, ISecretService secrets, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Value))
            {
                return Results.BadRequest("Value is required.");
            }

            try
            {
                var updated = await secrets.UpdateAsync(new UpdateSecretCommand(
                    id,
                    request.Value,
                    request.Description?.Trim()), ct);
                return updated is null ? Results.NotFound() : Results.Ok(MapResponse(updated));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapDelete("/{id:int}", async (int id, ISecretService secrets, CancellationToken ct) =>
        {
            var deleted = await secrets.DeleteAsync(id, ct);
            return deleted is null ? Results.NotFound() : Results.Accepted($"/api/v1/secrets/{id}", MapResponse(deleted));
        });

        return app;
    }

    private static SecretDto MapResponse(SecretInfo secret)
        => new(
            secret.Id,
            secret.TenantId,
            secret.ClientIdentity,
            secret.Value,
            secret.Description,
            secret.CreatedAtUtc,
            secret.UpdatedAtUtc);
}
