using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Endpoints.Client;

public static class ClientReleaseDeletionEndpoints
{
    public static IEndpointRouteBuilder MapClientReleaseDeletionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/client-updates/local-releases")
            .RequireAuthorization("ArtifactPublisher").WithTags("Client Updates");
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
                var result = await deletion.DeleteAsync(version, actor, ct);
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
            if (imports.Count == 0) return Results.Ok(new { state = "Untracked", checkedAtUtc = checkedAt, message = "This local package has no GitHub import provenance." });
            try
            {
                foreach (var import in imports)
                {
                    var upstream = await catalog.FindAsync(import.GitHubReleaseId, ct);
                    if (upstream is null) return Results.Ok(new { state = "Missing", checkedAtUtc = checkedAt, message = "GitHub confirmed this release is missing. Verified local packages are retained." });
                    if (upstream.PublicationAsset is null || upstream.ChecksumsAsset is null || import.Assets.Any(asset =>
                        !upstream.ClientAssets.Any(remote => remote.Id == asset.GitHubAssetId && remote.Name == asset.SourceName)))
                        return Results.Ok(new { state = "MissingAssets", checkedAtUtc = checkedAt, message = "The source release has missing or changed assets. Verified local packages are retained." });
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
public sealed record ClientReleaseDeleteRequest(string ConfirmedVersion);
