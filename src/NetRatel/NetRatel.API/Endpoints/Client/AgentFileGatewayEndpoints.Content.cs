using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.API.Endpoints.Client;

public static partial class AgentFileGatewayEndpoints
{
    private static async Task ReadContentAsync(int tenantId, Guid agentId, [FromQuery] string path,
        [FromQuery] string expectedVersion, HttpContext http, IServiceProvider services, CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeTargetAsync(tenantId, agentId, NetRatelPermissions.FileRead, http, services, cancellationToken);
        if (authorization.Rejection is { } rejection) { await rejection.ExecuteAsync(http); return; }
        if (!RemoteFilePath.TryNormalizeDirectory(path, out path) || string.IsNullOrEmpty(expectedVersion) || expectedVersion.Length > 256)
        { await FileProblem("invalid_path").ExecuteAsync(http); return; }
        GatewayFileReadOperation? operation = null;
        var completed = false;
        try
        {
            var sessions = services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
            var client = new ClientKey(tenantId, agentId);
            var stat = await sessions.StatAsync(client, path, cancellationToken);
            if (stat.IsDirectory) { await FileProblem("not_a_file").ExecuteAsync(http); return; }
            if (stat.ObservedVersion != expectedVersion) { await FileProblem("file_changed").ExecuteAsync(http); return; }
            var supportsRange = sessions.GetAvailability(client)?.NegotiatedCapabilities.Contains("file-read-range-v1") == true;
            if (!supportsRange) { await FileProblem("capability_unavailable").ExecuteAsync(http); return; }
            // Only a weak metadata validator is available. If-Range cannot match
            // it; ignore Range and return the complete current representation.
            if (!RemoteByteRange.TryResolve(http.Request.Headers.Range, stat.SizeBytes,
                    HttpMethods.IsHead(http.Request.Method) || http.Request.Headers.ContainsKey("If-Range"), out var range))
            {
                http.Response.Headers.ContentRange = $"bytes */{stat.SizeBytes}";
                http.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                return;
            }
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.AcceptRanges = "bytes";
            http.Response.Headers.ETag = $"W/\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expectedVersion)))}\"";
            http.Response.Headers.LastModified = stat.LastModifiedUtc.ToString("R");
            http.Response.ContentType = "application/octet-stream";
            if (HttpMethods.IsHead(http.Request.Method))
            {
                // Range is defined for GET. HEAD describes the complete representation.
                http.Response.ContentLength = stat.SizeBytes;
                return;
            }
            operation = await sessions.ReadRangeAsync(client, path, range.Offset, range.Length, expectedVersion, cancellationToken);
            await using var chunks = operation.Chunks.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            // Await the first native result before committing HTTP headers, so
            // denied/missing/changed reads cannot masquerade as empty files.
            var hasChunk = await chunks.MoveNextAsync();
            if (!hasChunk) await operation.Completion;
            http.Response.StatusCode = range.Partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
            http.Response.ContentLength = range.Length;
            if (range.Partial) http.Response.Headers.ContentRange = $"bytes {range.Offset}-{range.Offset + range.Length - 1}/{stat.SizeBytes}";
            while (hasChunk)
            {
                var current = chunks.Current;
                hasChunk = await chunks.MoveNextAsync();
                // Withhold the final bounded chunk until native completion is
                // validated, so a late failure cannot look like a complete body.
                if (!hasChunk) await operation.Completion;
                await http.Response.Body.WriteAsync(current, cancellationToken);
            }
            await operation.Completion;
            completed = true;
        }
        catch (AgentFileGatewayOperationException exception)
        {
            if (http.Response.HasStarted) http.Abort();
            else { http.Response.ContentLength = null; await FileProblem(exception.Code).ExecuteAsync(http); }
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            if (http.Response.HasStarted) http.Abort();
            else { http.Response.ContentLength = null; await FileProblem("session_unavailable").ExecuteAsync(http); }
        }
        finally
        {
            if (operation is not null && !completed) await operation.CancelAsync("content_request_ended", CancellationToken.None);
        }
    }

    private static async Task<IResult> WriteContentAsync(int tenantId, Guid agentId, [FromQuery] string path,
        [FromQuery] bool replace, [FromQuery] string? expectedVersion, HttpContext http, IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeTargetAsync(tenantId, agentId, NetRatelPermissions.FileWrite, http, services, cancellationToken);
        if (authorization.Rejection is { } rejection) return rejection;
        if (replace && !HasPermission(authorization.Access!, NetRatelPermissions.FileDelete)) return Results.Forbid();
        if (!RemoteFilePath.TryNormalizeDirectory(path, out path) || RemoteFilePath.IsRoot(path) ||
            (replace && string.IsNullOrEmpty(expectedVersion))) return FileProblem("invalid_dispatch");
        try
        {
            await services.GetRequiredService<IAgentFileGatewaySessionRegistry>().WriteConditionalAsync(new(tenantId, agentId),
                path, http.Request.Body, !replace, expectedVersion ?? string.Empty, cancellationToken);
            return Results.Ok(new GatewayFileSystemWriteResponse(tenantId, agentId, path, Authority));
        }
        catch (AgentFileGatewayOperationException exception) { return FileProblem(exception.Code); }
        catch (Exception exception) when (exception is AgentFileGatewaySessionUnavailableException or OperationCanceledException)
        { return Results.Problem(statusCode: 409, detail: "Upload outcome unknown. Refresh before trying again.", extensions: new Dictionary<string, object?> { ["code"] = "outcome_unknown" }); }
    }
}
