using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Endpoints.Client;

public static class ClientReleaseDeletionEndpoints
{
    private static string? Actor(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? http.User.FindFirstValue("sub") ?? http.User.Identity?.Name;
    public static IEndpointRouteBuilder MapClientReleaseDeletionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/client-updates/local-releases")
            .RequireAuthorization("ArtifactPublisher").WithTags("Client Updates");
        app.MapGet("/api/v1/client-updates/local-releases/catalogue", async (int? page, int? pageSize, string? search, string? channel, string? runtime,
            string? localState, bool? refresh, HttpContext http, Microsoft.AspNetCore.Authorization.IAuthorizationService authorization,
            ClientReleaseWorkspaceService workspace, CancellationToken ct) =>
        {
            var canManage = (await authorization.AuthorizeAsync(http.User, null, "ArtifactPublisher")).Succeeded;
            try { return Results.Ok(await workspace.ReadAsync(page ?? 0, pageSize ?? 20, search, channel ?? "all", runtime, localState, refresh ?? false, ct, canManage)); }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
        }).RequireAuthorization("ClientArtifactsWrite").WithTags("Client Updates");
        group.MapPost("/batch-preview", async (ClientReleaseBatchRequest request, ClientReleaseDeletionService deletion, CancellationToken ct) =>
        {
            try { return Results.Ok(await deletion.PreviewBatchAsync(request.Versions, ct)); }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
        });
        group.MapPost("/batch-delete", async (ClientReleaseBatchRequest request, HttpContext http, ClientReleaseDeletionService deletion, CancellationToken ct) =>
        {
            if (request.Versions is null || request.Versions.Length is < 1 or > 100)
                return Results.BadRequest(new { message = "Select between 1 and 100 versions per reviewed cleanup batch." });
            if (request.InventoryTokens is null || request.Versions.Any(v => !request.InventoryTokens.TryGetValue(v, out var token) || string.IsNullOrWhiteSpace(token)))
                return Results.BadRequest(new { message = "Refresh and review an authoritative inventory preview for every selected version." });
            if (!request.Confirmed) return Results.BadRequest(new { message = "Review and confirm the selected exact versions before purging files." });
            var actor = Actor(http);
            if (actor is null) return Results.Forbid();
            try { return Results.Ok(await deletion.DeleteBatchAsync(request.Versions, actor, request.DisableAllRuntimes, ct, request.InventoryTokens)); }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
        });
        group.MapPost("/{version}/attempts/{attemptId:guid}/retire", async (string version, Guid attemptId,
            ClientReleaseRetireRequest request, HttpContext http, ClientReleaseDeletionService deletion, CancellationToken ct) =>
        {
            var actor = Actor(http);
            if (actor is null) return Results.Forbid();
            if (!request.AcknowledgeUnknownOutcome) return Results.BadRequest(new { message = "Acknowledge the unknown physical outcome before retiring authority." });
            try { await deletion.RetireAttemptAsync(version, attemptId, actor, request.Reason, ct); return Results.NoContent(); }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (KeyNotFoundException e) { return Results.NotFound(new { message = e.Message }); }
            catch (InvalidOperationException e) { return Results.Conflict(new { message = e.Message }); }
        });
        group.MapGet("/deletions", async ([FromServices] ClientReleaseDeletionService deletion, CancellationToken ct) =>
            Results.Ok(await deletion.ListAsync(ct)));
        group.MapGet("/{version}/delete-preview", async (string version, [FromServices] ClientReleaseDeletionService deletion, CancellationToken ct) =>
        {
            try { return Results.Ok(await deletion.PreviewAsync(version, ct)); }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
        });
        group.MapPost("/{version}/delete", async (string version, ClientReleaseDeleteRequest request,
            HttpContext http, [FromServices] ClientReleaseDeletionService deletion, CancellationToken ct) =>
        {
            if (request.ConfirmedVersion != version) return Results.BadRequest(new { message = "Confirm the exact version to delete local data." });
            var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub") ?? http.User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(actor)) return Results.Forbid();
            try
            {
                var result = await deletion.DeleteAsync(version, actor, ct, request.DisableAllRuntimes);
                return result.State == "Deleted" ? Results.Ok(result) : Results.Accepted(value: result);
            }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (KeyNotFoundException e) { return Results.NotFound(new { message = e.Message }); }
            catch (InvalidOperationException e) { return Results.Conflict(new { message = e.Message }); }
        });
        group.MapGet("/{version}/source", async (string version, OrchestratorDbContext db,
            [FromServices] IGitHubClientReleaseCatalog catalog, CancellationToken ct) =>
        {
            var imports = await db.ClientReleaseImportOperations.AsNoTracking().Include(x => x.Assets)
                .Where(x => x.Version == version).ToListAsync(ct);
            var checkedAt = DateTimeOffset.UtcNow;
            if (imports.Count == 0) return Results.Ok(new { state = "Unknown", checkedAtUtc = checkedAt, message = "This local package has no GitHub import provenance." });
            try
            {
                foreach (var import in imports)
                {
                    var upstream = await catalog.FindAsync(import.GitHubReleaseId, ct);
                    if (upstream is null) return Results.Ok(new { state = "Missing", checkedAtUtc = checkedAt, message = "GitHub confirmed this release is missing. Verified local packages are retained." });
                    if (upstream.PublicationAsset is null || upstream.ChecksumsAsset is null || import.Assets.Any(asset =>
                        !upstream.ClientAssets.Any(remote => remote.Id == asset.GitHubAssetId && remote.Name == asset.SourceName)))
                        return Results.Ok(new { state = "Changed", checkedAtUtc = checkedAt, message = "The source release has missing or changed assets. Verified local packages are retained." });
                    var commit = await catalog.ResolveTagCommitAsync(import.Tag, ct);
                    if (import.BuildCommit is not null && commit != import.BuildCommit)
                        return Results.Ok(new { state = "Changed", checkedAtUtc = checkedAt, message = "The upstream tag changed. Local verified provenance is retained." });
                }
                return Results.Ok(new { state = "Available", checkedAtUtc = checkedAt, message = "Source release, tag and imported asset identities are available. Local integrity remains independent." });
            }
            catch (GitHubReleaseMissingException e) { return Results.Ok(new { state = "Missing", checkedAtUtc = checkedAt, message = e.Message }); }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return Results.Ok(new { state = "Unknown", checkedAtUtc = checkedAt, message = "Source availability could not be checked. Retry after resolving GitHub connectivity, authentication or rate limits; local data is retained." });
            }
        });
        return app;
    }
}
public sealed record ClientReleaseDeleteRequest(string ConfirmedVersion, bool DisableAllRuntimes = false);
public sealed record ClientReleaseBatchRequest(string[] Versions, bool Confirmed = false, bool DisableAllRuntimes = false, Dictionary<string, string>? InventoryTokens = null);
public sealed record ClientReleaseRetireRequest(string Reason, bool AcknowledgeUnknownOutcome);
