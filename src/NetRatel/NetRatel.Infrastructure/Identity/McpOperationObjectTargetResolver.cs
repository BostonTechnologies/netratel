using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;

namespace NetRatel.Infrastructure.Identity;

/// <summary>
/// Reads the durable owner projection for object-addressed MCP operations.
/// The local gateway can only carry a closed-schema identifier; this service
/// makes the API the authority for the tenant and agent pair.
/// </summary>
public sealed class McpOperationObjectTargetResolver(OrchestratorDbContext db) : IMcpOperationObjectTargetResolver
{
    public async Task<bool> MatchesDelegationAsync(
        McpOperatorDelegation delegation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delegation);

        var descriptor = McpOperationTargetCatalog.Find(delegation.Tool, delegation.Operation);
        if (descriptor?.Model is not McpOperationTargetModel.ObjectDerived)
            return delegation.ObjectReference is null;
        if (descriptor.ObjectReferenceKind is null)
            return delegation.ObjectReference is null;
        if (string.IsNullOrWhiteSpace(delegation.ObjectReference))
            return !descriptor.RequiresObjectReference;
        if (delegation.TenantId is not > 0 || delegation.AgentId is null ||
            !long.TryParse(delegation.ObjectReference, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var identifier) || identifier <= 0)
        {
            return false;
        }

        var owner = descriptor.ObjectReferenceKind switch
        {
            McpOperationObjectReferenceKind.Job => await JobOwnerAsync(identifier, cancellationToken).ConfigureAwait(false),
            McpOperationObjectReferenceKind.JobRun => await JobRunOwnerAsync(identifier, cancellationToken).ConfigureAwait(false),
            McpOperationObjectReferenceKind.Task => await TaskOwnerAsync(identifier, cancellationToken).ConfigureAwait(false),
            McpOperationObjectReferenceKind.Request => await RequestOwnerAsync(identifier, cancellationToken).ConfigureAwait(false),
            _ => null
        };

        return owner is { } resolved &&
               resolved.TenantId == delegation.TenantId &&
               resolved.AgentId == delegation.AgentId;
    }

    private async Task<McpObjectOwner?> JobOwnerAsync(long jobId, CancellationToken cancellationToken)
        => await SingleOwnerAsync(
            db.McpOperatorJobs.AsNoTracking()
                .Where(record => record.JobId == jobId && record.DeletedAtUtc == null)
                .Select(record => new McpObjectOwner(record.TenantId, record.AgentId)),
            cancellationToken).ConfigureAwait(false);

    private async Task<McpObjectOwner?> JobRunOwnerAsync(long jobRunId, CancellationToken cancellationToken)
        => await SingleOwnerAsync(
            db.McpOperatorJobRuns.AsNoTracking()
                .Where(record => record.JobRunId == jobRunId && record.DeletedAtUtc == null)
                .Select(record => new McpObjectOwner(record.TenantId, record.AgentId)),
            cancellationToken).ConfigureAwait(false);

    private async Task<McpObjectOwner?> TaskOwnerAsync(long taskId, CancellationToken cancellationToken)
        => await SingleOwnerAsync(
            db.McpOperatorTasks.AsNoTracking()
                .Where(record => record.TaskActivityId == taskId)
                .Select(record => new McpObjectOwner(record.TenantId, record.AgentId)),
            cancellationToken).ConfigureAwait(false);

    private async Task<McpObjectOwner?> RequestOwnerAsync(long requestId, CancellationToken cancellationToken)
    {
        if (requestId > int.MaxValue)
            return null;

        return await SingleOwnerAsync(
            db.McpOperatorRequests.AsNoTracking()
                .Where(record => record.RequestId == (int)requestId)
                .Select(record => new McpObjectOwner(record.TenantId, record.AgentId)),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<McpObjectOwner?> SingleOwnerAsync(
        IQueryable<McpObjectOwner> query,
        CancellationToken cancellationToken)
    {
        var owners = await query.Take(2).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return owners.Length == 1 ? owners[0] : null;
    }

    private sealed record McpObjectOwner(int TenantId, Guid AgentId);
}
