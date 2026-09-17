using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Gateway;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Events;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Development-only file reads used by the HTTP MCP service. Unlike the
/// general operator file browser, every request is constrained to the
/// persisted fixture root on an explicitly eligible target.
/// </summary>
public static class DevelopmentMcpFileGatewayEndpoints
{
    private const int DefaultPageSize = 64;
    private const int MaximumPageSize = 100;
    private const int MaximumInlineBytes = 64 * 1024;
    private const int MaximumArtifactBytes = 512 * 1024;
    private static readonly TimeSpan ArtifactLifetime = TimeSpan.FromMinutes(15);

    public static IEndpointRouteBuilder MapDevelopmentMcpFileGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        // The file gateway is optional in a Dev deployment. Do not register
        // endpoint delegates that depend on its service when the authority is
        // off: minimal-API parameter inference runs while constructing routes,
        // before an endpoint can return its intended fail-closed response.
        var options = app.ServiceProvider.GetRequiredService<NetRatelAkkaMigrationOptions>();
        if (!options.IsFileBrowseAuthorityActive ||
            app.ServiceProvider.GetService<IAgentFileGatewaySessionRegistry>() is null)
        {
            return app;
        }

        var group = app.MapGroup("/api/v2/development/mcp/agents/{tenantId:int}/{agentId:guid}/files")
            .WithTags("Development MCP Files")
            .RequireAuthorization("Operator");

        group.MapGet("", BrowseAsync);
        group.MapGet("/inline", ReadInlineAsync);
        group.MapPost("/collect", CollectAsync);
        group.MapGet("/artifacts/{artifactId:guid}", GetArtifactAsync);
        group.MapGet("/artifacts/{artifactId:guid}/download", DownloadArtifactAsync);
        group.MapDelete("/artifacts/{artifactId:guid}", CleanupArtifactAsync);
        return app;
    }

    private static async Task<IResult> BrowseAsync(
        int tenantId,
        Guid agentId,
        [FromQuery] string path,
        [FromQuery] int? pageSize,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var sessions = http.RequestServices.GetService<IAgentFileGatewaySessionRegistry>();
        if (!options.IsFileBrowseAuthorityActive || sessions is null)
        {
            return Results.NotFound();
        }

        if (pageSize is <= 0 or > MaximumPageSize)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_page_size", "pageSize must be between 1 and 100 when supplied.");
        }

        if (await RequireFixtureAccessAsync(http, tenantId, agentId, path, DevelopmentOperatorOperation.FileBrowse, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        try
        {
            var entries = await sessions.ListAsync(new ClientKey(tenantId, agentId), path, pageSize ?? DefaultPageSize, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                tenantId,
                agentId,
                path,
                entries = entries.Select(entry => new { entry.Name, entry.FullPath, entry.IsDirectory, entry.SizeBytes }).ToArray(),
                authority = "development-mcp-fixture"
            });
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            return Problem(StatusCodes.Status409Conflict, "session_unavailable", "No active file gateway session is available for this agent.");
        }
        catch (AgentFileGatewayOperationException exception)
        {
            return FileOperationProblem(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
    }

    private static async Task<IResult> ReadInlineAsync(
        int tenantId,
        Guid agentId,
        [FromQuery] string path,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var sessions = http.RequestServices.GetService<IAgentFileGatewaySessionRegistry>();
        if (!options.IsFileBrowseAuthorityActive || sessions is null)
        {
            return Results.NotFound();
        }

        if (await RequireFixtureAccessAsync(http, tenantId, agentId, path, DevelopmentOperatorOperation.FileRead, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        GatewayFileReadOperation? operation = null;
        var completed = false;
        var operationEnded = false;
        try
        {
            operation = await sessions.ReadAsync(new ClientKey(tenantId, agentId), path, cancellationToken).ConfigureAwait(false);
            await using var buffer = new MemoryStream();
            await foreach (var chunk in operation.Chunks.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (chunk.Length > MaximumInlineBytes - buffer.Length)
                {
                    await operation.CancelAsync("development_inline_read_too_large", CancellationToken.None).ConfigureAwait(false);
                    operationEnded = true;
                    return Problem(StatusCodes.Status413PayloadTooLarge, "inline_read_too_large", "The fixture file exceeds the 64 KiB inline read limit; collect it as an artifact instead.");
                }

                await buffer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }

            await operation.Completion.ConfigureAwait(false);
            completed = true;
            var bytes = buffer.ToArray();
            string content;
            try
            {
                content = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Problem(StatusCodes.Status415UnsupportedMediaType, "inline_text_required", "The fixture file is not valid UTF-8 text; collect it as an artifact instead.");
            }

            return Results.Ok(new
            {
                tenantId,
                agentId,
                path,
                sizeBytes = bytes.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                content,
                authority = "development-mcp-fixture"
            });
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            return Problem(StatusCodes.Status409Conflict, "session_unavailable", "No active file gateway session is available for this agent.");
        }
        catch (AgentFileGatewayOperationException exception)
        {
            return FileOperationProblem(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (operation is not null)
            {
                await operation.CancelAsync("development_inline_read_cancelled", CancellationToken.None).ConfigureAwait(false);
                operationEnded = true;
            }

            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        finally
        {
            if (operation is not null && !completed && !operationEnded && !cancellationToken.IsCancellationRequested)
            {
                await operation.CancelAsync("development_inline_read_ended", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<IResult> CollectAsync(
        int tenantId,
        Guid agentId,
        DevelopmentMcpFileCollectRequest request,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        OrchestratorDbContext db,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var sessions = http.RequestServices.GetService<IAgentFileGatewaySessionRegistry>();
        if (!options.IsFileBrowseAuthorityActive || sessions is null)
        {
            return Results.NotFound();
        }

        if (!TryGetFileName(request.Path, out var fileName))
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_path", "The fixture collection path must name a bounded file.");
        }

        if (!IsCampaignMarker(request.Marker) || !IsMarkerOwnedFileName(fileName, request.Marker))
        {
            return Problem(StatusCodes.Status400BadRequest, "marker_artifact_required", "The fixture file name must be derived from its MCP-QA campaign marker.");
        }

        if (await RequireFixtureAccessAsync(http, tenantId, agentId, request.Path, DevelopmentOperatorOperation.FileCollect, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        var grant = await targets.GetActiveGrantAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (grant?.FileFixtureRoot is null || !DevelopmentFileFixture.Contains(grant.FileFixtureRoot, request.Path))
        {
            return Problem(StatusCodes.Status403Forbidden, "target_authorization_changed", "The Development file-fixture authorization changed before collection.");
        }

        GatewayFileReadOperation? operation = null;
        var completed = false;
        var operationEnded = false;
        try
        {
            operation = await sessions.ReadAsync(new ClientKey(tenantId, agentId), request.Path, cancellationToken).ConfigureAwait(false);
            await using var buffer = new MemoryStream();
            await foreach (var chunk in operation.Chunks.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (chunk.Length > MaximumArtifactBytes - buffer.Length)
                {
                    await operation.CancelAsync("development_artifact_too_large", CancellationToken.None).ConfigureAwait(false);
                    operationEnded = true;
                    return Problem(StatusCodes.Status413PayloadTooLarge, "artifact_too_large", "The fixture file exceeds the 512 KiB Development artifact limit.");
                }

                await buffer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }

            await operation.Completion.ConfigureAwait(false);
            completed = true;
            var bytes = buffer.ToArray();
            var now = DateTimeOffset.UtcNow;
            var artifact = new DevelopmentMcpFileArtifactRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                AgentId = agentId,
                TargetGrantId = grant.GrantId,
                FileName = fileName,
                SizeBytes = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                MarkerOwned = true,
                Content = bytes,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.Add(ArtifactLifetime)
            };
            db.DevelopmentMcpFileArtifacts.Add(artifact);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Results.Created($"/api/v2/development/mcp/agents/{tenantId}/{agentId:D}/files/artifacts/{artifact.Id:D}", ToArtifactMetadata(artifact, now));
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            return Problem(StatusCodes.Status409Conflict, "session_unavailable", "No active file gateway session is available for this agent.");
        }
        catch (AgentFileGatewayOperationException exception)
        {
            return FileOperationProblem(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (operation is not null)
            {
                await operation.CancelAsync("development_artifact_collect_cancelled", CancellationToken.None).ConfigureAwait(false);
                operationEnded = true;
            }

            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        finally
        {
            if (operation is not null && !completed && !operationEnded && !cancellationToken.IsCancellationRequested)
            {
                await operation.CancelAsync("development_artifact_collect_ended", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<IResult> GetArtifactAsync(
        int tenantId,
        Guid agentId,
        Guid artifactId,
        HttpContext http,
        OrchestratorDbContext db,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var artifact = await FindArtifactAsync(db, tenantId, agentId, artifactId, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        if (artifact.DeletedAtUtc is not null || artifact.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return Problem(StatusCodes.Status410Gone, "artifact_expired", "The Development artifact is no longer available.");
        }

        if (await DevelopmentOperatorTargetGate.RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.FileArtifactStatus, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        return Results.Ok(ToArtifactMetadata(artifact, DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> DownloadArtifactAsync(
        int tenantId,
        Guid agentId,
        Guid artifactId,
        HttpContext http,
        OrchestratorDbContext db,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var artifact = await FindArtifactAsync(db, tenantId, agentId, artifactId, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        if (artifact.DeletedAtUtc is not null || artifact.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return Problem(StatusCodes.Status410Gone, "artifact_expired", "The Development artifact is no longer available.");
        }

        if (await DevelopmentOperatorTargetGate.RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.FileArtifactDownload, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        // This is deliberately a bounded JSON transfer: the MCP transport is
        // structured JSON, and direct operator APIs are not given a bypass URL.
        return Results.Ok(new
        {
            artifactId = artifact.Id,
            artifact.FileName,
            artifact.SizeBytes,
            artifact.Sha256,
            contentBase64 = Convert.ToBase64String(artifact.Content),
            expiresAtUtc = artifact.ExpiresAtUtc
        });
    }

    private static async Task<IResult> CleanupArtifactAsync(
        int tenantId,
        Guid agentId,
        Guid artifactId,
        HttpContext http,
        OrchestratorDbContext db,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var artifact = await FindArtifactAsync(db, tenantId, agentId, artifactId, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            return Results.NotFound();
        }

        if (!artifact.MarkerOwned)
        {
            return Problem(StatusCodes.Status403Forbidden, "marker_artifact_required", "Only marker-owned Development artifacts may be removed.");
        }

        if (artifact.DeletedAtUtc is not null)
        {
            return Problem(StatusCodes.Status409Conflict, "artifact_already_cleaned", "The Development artifact was already removed.");
        }

        if (await DevelopmentOperatorTargetGate.RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.FileArtifactCleanup, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        artifact.Content = [];
        artifact.DeletedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToArtifactMetadata(artifact, artifact.DeletedAtUtc.Value));
    }

    private static async Task<IResult?> RequireFixtureAccessAsync(
        HttpContext http,
        int tenantId,
        Guid agentId,
        string? path,
        DevelopmentOperatorOperation operation,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var grant = await targets.GetActiveGrantAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (grant?.FileFixtureRoot is null)
        {
            return Problem(StatusCodes.Status403Forbidden, "file_fixture_not_authorized", "This Development target has no active file-fixture grant.");
        }

        if (!DevelopmentFileFixture.Contains(grant.FileFixtureRoot, path))
        {
            return Problem(StatusCodes.Status403Forbidden, "fixture_path_not_authorized", "The requested path is outside the approved Development file fixture.");
        }

        if (await DevelopmentOperatorTargetGate.RequireAcceptedAsync(http, tenantId, agentId, operation, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        // The gate audit is intentionally before dispatch. Recheck the exact
        // persisted grant/root so a concurrent grant replacement cannot widen
        // the path accepted for the remote operation.
        var current = await targets.GetActiveGrantAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.GrantId != grant.GrantId ||
            !string.Equals(current.FileFixtureRoot, grant.FileFixtureRoot, StringComparison.Ordinal) ||
            !DevelopmentFileFixture.Contains(current.FileFixtureRoot, path))
        {
            return Problem(StatusCodes.Status403Forbidden, "target_authorization_changed", "The Development file-fixture authorization changed before dispatch.");
        }

        return null;
    }

    private static IResult FileOperationProblem(string code) => code switch
    {
        "invalid_path" or "invalid_dispatch" => Problem(StatusCodes.Status400BadRequest, code, "The remote path is invalid."),
        "file_not_found" or "directory_not_found" => Problem(StatusCodes.Status404NotFound, code, "The requested remote fixture path does not exist."),
        "access_denied" => Problem(StatusCodes.Status403Forbidden, code, "The remote client denied access to the fixture path."),
        "io_failure" => Problem(StatusCodes.Status409Conflict, code, "The remote client could not complete the fixture operation."),
        "cancelled" => Problem(StatusCodes.Status499ClientClosedRequest, code, "The remote fixture operation was cancelled."),
        _ => Problem(StatusCodes.Status502BadGateway, code, "The remote client could not complete the fixture operation.")
    };

    private static IResult Problem(int statusCode, string code, string detail) => Results.Problem(
        statusCode: statusCode,
        title: "Development MCP fixture file operation failed",
        detail: detail,
        extensions: new Dictionary<string, object?> { ["code"] = code });

    private static Task<DevelopmentMcpFileArtifactRecord?> FindArtifactAsync(
        OrchestratorDbContext db,
        int tenantId,
        Guid agentId,
        Guid artifactId,
        CancellationToken cancellationToken) =>
        db.DevelopmentMcpFileArtifacts.SingleOrDefaultAsync(artifact =>
            artifact.Id == artifactId && artifact.TenantId == tenantId && artifact.AgentId == agentId,
            cancellationToken);

    private static object ToArtifactMetadata(DevelopmentMcpFileArtifactRecord artifact, DateTimeOffset now) => new
    {
        artifactId = artifact.Id,
        artifact.TenantId,
        artifact.AgentId,
        artifact.FileName,
        artifact.SizeBytes,
        artifact.Sha256,
        artifact.MarkerOwned,
        artifact.CreatedAtUtc,
        artifact.ExpiresAtUtc,
        deletedAtUtc = artifact.DeletedAtUtc,
        isAvailable = artifact.DeletedAtUtc is null && artifact.ExpiresAtUtc > now
    };

    private static bool TryGetFileName(string? path, out string fileName)
    {
        fileName = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var index = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        var candidate = index >= 0 ? path[(index + 1)..] : path;
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 512)
        {
            return false;
        }

        fileName = candidate;
        return true;
    }

    private static bool IsCampaignMarker(string? marker)
        => marker is { Length: > 7 and <= 128 } &&
           marker.StartsWith("MCP-QA-", StringComparison.Ordinal) &&
           marker.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsMarkerOwnedFileName(string fileName, string marker)
        => fileName.Length > marker.Length &&
           fileName.StartsWith(marker, StringComparison.Ordinal) &&
           fileName[marker.Length] == '.';
}

public sealed record DevelopmentMcpFileCollectRequest(string Path, string Marker);
