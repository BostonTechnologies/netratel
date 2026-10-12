using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Gateway;
using NetRatel.Akka.Observability;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Agent-ID keyed V2 file endpoints.
/// </summary>
public static class AgentFileGatewayEndpoints
{
    private const int DefaultPageSize = 128;
    private const string Authority = "akka";
    private const string Feature = "file-browser";

    public static IEndpointRouteBuilder MapAgentFileGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/agents/{tenantId:int}/{agentId:guid}/filesystem")
            .WithTags("Gateway Filesystem");

        group.MapGet("", ListAsync).RequireAuthorization("FileReader");
        group.MapGet("/file", ReadAsync).RequireAuthorization("FileReader");
        group.MapPut("/file", WriteAsync).RequireAuthorization("FileWriter");
        group.MapPost("/directory", CreateDirectoryAsync).RequireAuthorization("FileWriter");
        group.MapDelete("/directory", DeleteDirectoryAsync).RequireAuthorization("FileDeleter");
        return app;
    }

    private static async Task<IResult> ListAsync(
        int tenantId,
        Guid agentId,
        [FromQuery] string path,
        [FromQuery] int? pageSize,
        HttpContext http,
        IHostEnvironment environment,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeTargetAsync(tenantId, agentId, NetRatelPermissions.FileRead, http, services, cancellationToken).ConfigureAwait(false);
        if (authorization.Rejection is { } rejection) return rejection;
        if (!IsAllowedPath(path))
        {
            return FileProblem("invalid_path");
        }

        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, environment.EnvironmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
            Feature, Authority, "list", environment.EnvironmentName);
        try
        {
            var sessions = services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
            var entries = await sessions.ListAsync(new ClientKey(tenantId, agentId), path, pageSize ?? DefaultPageSize, cancellationToken).ConfigureAwait(false);
            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, environment.EnvironmentName);
            var capabilities = sessions.GetAvailability(new ClientKey(tenantId, agentId))?.NegotiatedCapabilities ?? [];
            var canWrite = HasPermission(authorization.Access!, NetRatelPermissions.FileWrite);
            var canDelete = HasPermission(authorization.Access!, NetRatelPermissions.FileDelete);
            return Results.Ok(new GatewayFileSystemListResponse(
                tenantId,
                agentId,
                path,
                Authority,
                entries.Select(entry => new GatewayFileSystemEntryDto(entry.Name, entry.FullPath, entry.IsDirectory, entry.SizeBytes)
                {
                    CreatedUtc = entry.CreatedUtc,
                    LastModifiedUtc = entry.LastModifiedUtc
                }).ToArray())
            {
                CanWriteFiles = canWrite,
                CanCreateDirectory = canWrite && capabilities.Contains("file-create-directory-v1", StringComparer.Ordinal),
                CanDeleteDirectory = canDelete && capabilities.Contains("file-delete-directory-v1", StringComparer.Ordinal)
            });
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return FileProblem("session_unavailable");
        }
        catch (AgentFileGatewayOperationException exception)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return FileProblem(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (OperationCanceledException)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return FileProblem("session_unavailable");
        }
        catch
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            throw;
        }
    }

    private static async Task ReadAsync(
        int tenantId,
        Guid agentId,
        [FromQuery] string path,
        HttpContext context,
        IHostEnvironment environment,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var response = context.Response;
        var authorization = await AuthorizeTargetAsync(tenantId, agentId, NetRatelPermissions.FileRead, context, services, cancellationToken).ConfigureAwait(false);
        if (authorization.Rejection is { } rejection)
        {
            await rejection.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        if (!IsAllowedPath(path))
        {
            await FileProblem("invalid_path").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, environment.EnvironmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
            Feature, Authority, "read", environment.EnvironmentName);
        GatewayFileReadOperation? operation = null;
        var completed = false;
        try
        {
            var sessions = services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
            operation = await sessions.ReadAsync(new ClientKey(tenantId, agentId), path, cancellationToken).ConfigureAwait(false);
            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, environment.EnvironmentName);
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
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            await WriteFileProblemAsync(context, "session_unavailable").ConfigureAwait(false);
        }
        catch (AgentFileGatewayOperationException exception)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            await WriteFileProblemAsync(context, exception.Code).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            if (operation is not null)
            {
                await operation.CancelAsync("operator_download_cancelled", CancellationToken.None).ConfigureAwait(false);
            }
            response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
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
        IHostEnvironment environment,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeTargetAsync(tenantId, agentId, NetRatelPermissions.FileWrite, http, services, cancellationToken).ConfigureAwait(false);
        if (authorization.Rejection is { } rejection) return rejection;
        if (!IsAllowedPath(path))
        {
            return FileProblem("invalid_path");
        }

        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, environment.EnvironmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
            Feature, Authority, "write", environment.EnvironmentName);
        try
        {
            var sessions = services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
            await sessions.WriteAsync(new ClientKey(tenantId, agentId), path, http.Request.Body, cancellationToken).ConfigureAwait(false);
            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, environment.EnvironmentName);
            return Results.Ok(new GatewayFileSystemWriteResponse(tenantId, agentId, path, Authority));
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return FileProblem("session_unavailable");
        }
        catch (AgentFileGatewayOperationException exception)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return FileProblem(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (OperationCanceledException)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return FileProblem("session_unavailable");
        }
        catch
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            throw;
        }
    }

    private static Task<IResult> CreateDirectoryAsync(int tenantId, Guid agentId,
        GatewayFileSystemCreateDirectoryRequest request, HttpContext http, IHostEnvironment environment,
        IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!RemoteFilePath.TryJoinDirectory(request.ParentPath, request.Name, out var path) ||
            !RemoteFilePath.TryGetDirectoryParent(path, out var parent))
            return Task.FromResult(FileProblem("invalid_folder_name"));

        return MutateDirectoryAsync(tenantId, agentId, path, parent, "create_directory", NetRatelPermissions.FileWrite,
            http, environment, services, cancellationToken);
    }

    private static Task<IResult> DeleteDirectoryAsync(int tenantId, Guid agentId, [FromQuery] string path,
        HttpContext http, IHostEnvironment environment, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!RemoteFilePath.TryNormalizeDirectory(path, out var normalizedPath))
            return Task.FromResult(FileProblem("invalid_path"));
        if (!RemoteFilePath.TryGetDirectoryParent(normalizedPath, out var parent))
            return Task.FromResult(FileProblem("protected_path"));

        return MutateDirectoryAsync(tenantId, agentId, normalizedPath, parent, "delete_directory", NetRatelPermissions.FileDelete,
            http, environment, services, cancellationToken);
    }

    private static async Task<IResult> MutateDirectoryAsync(int tenantId, Guid agentId, string path, string parent,
        string operation, string permission, HttpContext http, IHostEnvironment environment,
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeTargetAsync(tenantId, agentId, permission, http, services, cancellationToken).ConfigureAwait(false);
        if (authorization.Rejection is { } rejection) return rejection;
        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, environment.EnvironmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(Feature, Authority, operation, environment.EnvironmentName);
        try
        {
            var sessions = services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
            var target = new ClientKey(tenantId, agentId);
            // The selected existing parent is the physical boundary for a UI/CLI
            // folder operation. The native agent resolves links at that machine.
            var policy = new GatewayFileAccessPolicy([parent]);
            if (operation == "create_directory")
                await sessions.CreateDirectoryAsync(target, path, policy, cancellationToken).ConfigureAwait(false);
            else
                await sessions.DeleteDirectoryAsync(target, path, policy, cancellationToken).ConfigureAwait(false);
            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, environment.EnvironmentName);
            return Results.Ok(new GatewayFileSystemWriteResponse(tenantId, agentId, path, Authority));
        }
        catch (AgentFileGatewaySessionUnavailableException)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return FileProblem("session_unavailable");
        }
        catch (AgentFileGatewayOperationException exception)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return FileProblem(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (OperationCanceledException)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            return FileProblem("session_unavailable");
        }
    }

    private static async Task<(EffectiveAccessSnapshot? Access, IResult? Rejection)> AuthorizeTargetAsync(
        int tenantId, Guid agentId, string permission, HttpContext http, IServiceProvider services, CancellationToken cancellationToken)
    {
        var access = await services.GetRequiredService<IEffectiveAccessService>()
            .GetSnapshotAsync(http.User, tenantId, cancellationToken).ConfigureAwait(false);
        if (!HasPermission(access, permission)) return (null, Results.Forbid());
        var agent = await services.GetRequiredService<IAgentManagementService>()
            .GetAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        return agent is null || agent.TenantId != tenantId || agent.AgentId != agentId || agent.DeletedAtUtc is not null
            ? (null, Results.NotFound()) : (access, null);
    }

    private static bool HasPermission(EffectiveAccessSnapshot access, string permission) =>
        access.IsLegacyOperator || access.IsInstanceAdministrator || access.Permissions.Contains(permission);

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
            "invalid_folder_name" => (StatusCodes.Status400BadRequest, "Enter one valid folder name within an absolute remote parent path."),
            "file_not_found" or "directory_not_found" or "path_not_found" => (StatusCodes.Status404NotFound, "The requested remote path does not exist."),
            "access_denied" => (StatusCodes.Status403Forbidden, "The remote client denied access to the requested path."),
            "protected_path" => (StatusCodes.Status403Forbidden, "Filesystem roots, approved roots, and symbolic links cannot be deleted through folder operations."),
            "path_already_exists" => (StatusCodes.Status409Conflict, "A file or folder already exists at this path."),
            "directory_not_empty" => (StatusCodes.Status409Conflict, "This folder is not empty. Only empty folders can be deleted."),
            "not_a_directory" => (StatusCodes.Status409Conflict, "The selected path is no longer a folder. Refresh the directory."),
            "capability_unavailable" => (StatusCodes.Status409Conflict, "This client does not support the requested folder operation. Update the client and try again."),
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
