using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Gateway;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Observability;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Agent-ID keyed V2 file endpoints. They are additive and do not alter the
/// primary client filesystem routes or any terminal transport.
/// </summary>
public static class AgentFileGatewayEndpoints
{
    private const int DefaultPageSize = 128;
    private const string Authority = "akka";
    private const string Feature = "file-browser";

    public static IEndpointRouteBuilder MapAgentFileGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/agents/{tenantId:int}/{agentId:guid}/filesystem")
            .WithTags("Gateway Filesystem")
            .RequireAuthorization("Operator");

        group.MapGet("", ListAsync);
        group.MapGet("/file", ReadAsync);
        group.MapPut("/file", WriteAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        int tenantId,
        Guid agentId,
        [FromQuery] string path,
        [FromQuery] int? pageSize,
        NetRatelAkkaMigrationOptions options,
        IHostEnvironment environment,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsFileBrowseAuthorityActive)
        {
            return Results.NotFound();
        }

        if (!IsAllowedPath(path))
        {
            return FileProblem("invalid_path");
        }

        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
            Feature, Authority, "list", fallbackUsed: false, environment.EnvironmentName);
        try
        {
            var sessions = services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
            var entries = await sessions.ListAsync(new ClientKey(tenantId, agentId), path, pageSize ?? DefaultPageSize, cancellationToken).ConfigureAwait(false);
            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            return Results.Ok(new GatewayFileSystemListResponse(
                tenantId,
                agentId,
                path,
                Authority,
                entries.Select(entry => new GatewayFileSystemEntryDto(entry.Name, entry.FullPath, entry.IsDirectory, entry.SizeBytes)).ToArray()));
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            return FileProblem("session_unavailable");
        }
        catch (AgentFileGatewayOperationException exception)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            return FileProblem(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (OperationCanceledException)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            return FileProblem("session_unavailable");
        }
        catch
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            throw;
        }
    }

    private static async Task ReadAsync(
        int tenantId,
        Guid agentId,
        [FromQuery] string path,
        HttpContext context,
        NetRatelAkkaMigrationOptions options,
        IHostEnvironment environment,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var response = context.Response;
        if (!options.IsFileBrowseAuthorityActive)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!IsAllowedPath(path))
        {
            await FileProblem("invalid_path").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
            Feature, Authority, "read", fallbackUsed: false, environment.EnvironmentName);
        GatewayFileReadOperation? operation = null;
        var completed = false;
        try
        {
            var sessions = services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
            operation = await sessions.ReadAsync(new ClientKey(tenantId, agentId), path, cancellationToken).ConfigureAwait(false);
            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/octet-stream";
            response.Headers.ContentDisposition = "attachment";
            await foreach (var chunk in operation.Chunks.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await response.Body.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await operation.Completion.ConfigureAwait(false);
            completed = true;
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            await WriteFileProblemAsync(context, "session_unavailable").ConfigureAwait(false);
        }
        catch (AgentFileGatewayOperationException exception)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            await WriteFileProblemAsync(context, exception.Code).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            if (operation is not null)
            {
                await operation.CancelAsync("operator_download_cancelled", CancellationToken.None).ConfigureAwait(false);
            }
            response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            throw;
        }
        finally
        {
            if (operation is not null && !completed && !cancellationToken.IsCancellationRequested)
            {
                await operation.CancelAsync("operator_download_ended", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<IResult> WriteAsync(
        int tenantId,
        Guid agentId,
        [FromQuery] string path,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IHostEnvironment environment,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsFileBrowseAuthorityActive)
        {
            return Results.NotFound();
        }

        if (!IsAllowedPath(path))
        {
            return FileProblem("invalid_path");
        }

        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
            Feature, Authority, "write", fallbackUsed: false, environment.EnvironmentName);
        try
        {
            var sessions = services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
            await sessions.WriteAsync(new ClientKey(tenantId, agentId), path, http.Request.Body, cancellationToken).ConfigureAwait(false);
            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            return Results.Ok(new GatewayFileSystemWriteResponse(tenantId, agentId, path, Authority));
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            return FileProblem("session_unavailable");
        }
        catch (AgentFileGatewayOperationException exception)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            return FileProblem(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (OperationCanceledException)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            return FileProblem("session_unavailable");
        }
        catch
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            throw;
        }
    }

    private static bool IsAllowedPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 4_096 && !path.Contains('\0');

    private static async Task WriteFileProblemAsync(HttpContext context, string code)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        await FileProblem(code).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static IResult FileProblem(string code)
    {
        var (statusCode, detail) = code switch
        {
            "invalid_path" or "invalid_dispatch" => (StatusCodes.Status400BadRequest, "The remote path is invalid."),
            "file_not_found" or "directory_not_found" => (StatusCodes.Status404NotFound, "The requested remote path does not exist."),
            "access_denied" => (StatusCodes.Status403Forbidden, "The remote client denied access to the requested path."),
            "io_failure" => (StatusCodes.Status409Conflict, "The remote client could not complete the filesystem operation."),
            "cancelled" => (StatusCodes.Status499ClientClosedRequest, "The remote filesystem operation was cancelled."),
            "session_unavailable" => (StatusCodes.Status409Conflict, "No active file gateway session is available for this agent."),
            _ => (StatusCodes.Status502BadGateway, "The remote client could not complete the filesystem operation.")
        };

        return Results.Problem(
            statusCode: statusCode,
            title: "Remote filesystem operation failed",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
    }
}
