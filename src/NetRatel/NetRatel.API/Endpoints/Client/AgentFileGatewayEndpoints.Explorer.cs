using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Gateway;
using NetRatel.Akka.Observability;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.API.Endpoints.Client;

public static partial class AgentFileGatewayEndpoints
{
    private static async Task<IResult> StatEntryAsync(int tenantId, Guid agentId, [FromQuery] string path,
        HttpContext http, IServiceProvider services, CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeTargetAsync(tenantId, agentId, NetRatelPermissions.FileRead, http, services, cancellationToken);
        if (authorization.Rejection is { } rejection) return rejection;
        if (!RemoteFilePath.TryNormalizeDirectory(path, out path)) return FileProblem("invalid_path");
        try
        {
            var sessions = services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
            var client = new ClientKey(tenantId, agentId);
            var stat = await sessions.StatAsync(client, path, cancellationToken);
            return Results.Ok(new GatewayFileSystemStatResponse(tenantId, agentId, stat.FullPath, stat.IsDirectory,
                stat.SizeBytes, stat.LastModifiedUtc, stat.CreatedUtc, stat.ObservedVersion,
                sessions.GetAvailability(client)?.NegotiatedCapabilities.Contains("file-read-range-v1") == true));
        }
        catch (AgentFileGatewayOperationException exception) { return FileProblem(exception.Code); }
        catch (AgentFileGatewaySessionUnavailableException) { return FileProblem("session_unavailable"); }
    }

    private static async Task<IResult> MutateEntriesAsync(int tenantId, Guid agentId, GatewayFileSystemMutationRequest request,
        HttpContext http, IHostEnvironment environment, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (request.Operation is not ("create_file" or "move" or "delete") || request.Items is null || request.Items.Count is 0 or > 128)
            return FileProblem("invalid_dispatch");
        var permission = request.Operation == "delete" ? NetRatelPermissions.FileDelete : NetRatelPermissions.FileWrite;
        var authorization = await AuthorizeTargetAsync(tenantId, agentId, permission, http, services, cancellationToken);
        if (authorization.Rejection is { } rejection) return rejection;
        if (request.Operation == "move" && !HasPermission(authorization.Access!, NetRatelPermissions.FileDelete)) return Results.Forbid();

        // Preflight the complete selection before dispatching any item. Native
        // validation remains authoritative; the batch is explicitly not atomic.
        var items = new List<GatewayFileSystemMutationItem>();
        foreach (var item in request.Items)
        {
            if (!RemoteFilePath.TryNormalizeDirectory(item.Path, out var path) || !RemoteFilePath.TryGetDirectoryParent(path, out _))
                return FileProblem("protected_path");
            string? destination = null;
            if (request.Operation == "move" &&
                (!RemoteFilePath.TryNormalizeDirectory(item.DestinationPath, out destination) ||
                 !RemoteFilePath.TryGetDirectoryParent(destination, out _) || IsInvalidMoveDestination(path, destination)))
                return FileProblem("invalid_destination");
            if (items.Any(other => RemoteFilePath.IsWithin(path, other.Path) || RemoteFilePath.IsWithin(other.Path, path)))
                return FileProblem("overlapping_selection");
            items.Add(new(path, destination));
        }
        if (request.Operation == "move" && items.Any(item => items.Any(other =>
                !ReferenceEquals(item, other) && RemoteFilePath.IsWithin(item.DestinationPath!, other.Path)))) return FileProblem("invalid_destination");

        var sessions = services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
        var client = new ClientKey(tenantId, agentId);
        var outcomes = new List<GatewayFileSystemMutationResult>();
        var stop = false;
        foreach (var item in items)
        {
            if (stop || cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(new(item.Path, item.DestinationPath, "cancelled", "not_dispatched"));
                continue;
            }
            RemoteFilePath.TryGetDirectoryParent(item.Path, out var parent);
            using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(Feature, Authority, request.Operation, environment.EnvironmentName);
            activity?.SetTag("file.batch.items", items.Count);
            activity?.SetTag("enduser.id", http.User.Identity?.Name);
            try
            {
                long affected;
                if (request.Operation == "delete" && !request.Recursive)
                {
                    await sessions.DeleteAsync(client, item.Path, new([parent]), cancellationToken);
                    affected = 1;
                }
                else
                {
                    GatewayFileMoveCopyAccessPolicy? movePolicy = null;
                    if (item.DestinationPath is { } destination)
                    {
                        RemoteFilePath.TryGetDirectoryParent(destination, out var destinationParent);
                        movePolicy = new([parent], [destinationParent]);
                    }
                    affected = await sessions.MutateEntryAsync(client, request.Operation switch
                    { "move" => "move_entry", "delete" => "delete_recursive", _ => "create_file" },
                        item.Path, item.DestinationPath, new([parent]), movePolicy, cancellationToken);
                }
                outcomes.Add(new(item.Path, item.DestinationPath, "completed", AffectedEntries: affected));
                NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, environment.EnvironmentName);
            }
            catch (AgentFileGatewayOperationException exception)
            {
                outcomes.Add(new(item.Path, item.DestinationPath, exception.Code == "outcome_unknown" ? "unknown" :
                    exception.Code == "cancelled" ? "cancelled" : "failed", exception.Code, exception.AffectedEntries));
                stop = exception.Code == "outcome_unknown";
                NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            }
            catch (Exception exception) when (exception is AgentFileGatewaySessionUnavailableException or OperationCanceledException)
            {
                outcomes.Add(new(item.Path, item.DestinationPath, "unknown", "connection_lost_or_cancelled"));
                stop = true;
                NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            }
        }
        return Results.Ok(new GatewayFileSystemMutationResponse(outcomes));
    }

    private static bool IsInvalidMoveDestination(string source, string destination) =>
        RemoteFilePath.IsWithin(destination, source) && !(RemoteFilePath.IsWindows(source) &&
            !string.Equals(source, destination, StringComparison.Ordinal) && string.Equals(source, destination, StringComparison.OrdinalIgnoreCase));
}
