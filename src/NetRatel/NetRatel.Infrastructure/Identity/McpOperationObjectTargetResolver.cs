using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;
using System.Globalization;

namespace NetRatel.Infrastructure.Identity;

/// <summary>
/// Reads the durable owner projection for object-addressed MCP operations.
/// The local gateway can only carry a closed-schema identifier; this service
/// makes the API the authority for the tenant and agent pair.
/// </summary>
public sealed class McpOperationObjectTargetResolver(OrchestratorDbContext db) : IMcpOperationObjectTargetResolver
{
    public async Task<McpOperationResolvedPolicyTarget?> ResolvePolicyTargetAsync(string reference, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(reference, out var policyId) || policyId == Guid.Empty) return null;
        var policy = await db.McpOperatorPolicies.AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == policyId, cancellationToken).ConfigureAwait(false);
        return policy is null ? null : new(policy.TenantId > 0 ? policy.TenantId : null, policy.AgentId);
    }

    public async Task<bool> MatchesDelegationAsync(
        McpOperatorDelegation delegation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delegation);

        var descriptor = McpOperationTargetCatalog.Find(delegation.Tool, delegation.Operation);
        if (descriptor?.Model is McpOperationTargetModel.PolicyReference)
        {
            var policy = delegation.ObjectReference is { } reference
                ? await ResolvePolicyTargetAsync(reference, cancellationToken).ConfigureAwait(false) : null;
            return policy is not null && policy.TenantId == delegation.TenantId && policy.AgentId == delegation.AgentId;
        }
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

        if (owner is { } resolved)
            return resolved.TenantId == delegation.TenantId && resolved.AgentId == delegation.AgentId;

        return await MatchesDeletedReplayAsync(delegation, identifier, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> MatchesDeletedReplayAsync(McpOperatorDelegation delegation, long identifier, CancellationToken cancellationToken)
    {
        if (!delegation.ObjectTargetResolutionEnabled || delegation.ReplayBinding is not { IsValid: true } binding ||
            string.IsNullOrWhiteSpace(delegation.IngressCredentialId) ||
            delegation.IngressCredentialId != delegation.Identity.ClientId ||
            string.IsNullOrWhiteSpace(delegation.Resource) || string.IsNullOrWhiteSpace(delegation.Instance)) return false;

        var jobMutation = delegation.Tool == "netratel_jobs" && McpOperatorJobConfirmationPayload.IsMutation(delegation.Operation);
        var runAction = delegation.Tool == "netratel_job_runs" && delegation.Operation is "cancel" or "delete";
        if (!jobMutation && !runAction) return false;

        // A tombstone is an owner-bound receipt source only. Joining a run's
        // durable job record prevents another job with the same public id from
        // supplying identity after the original definition has been removed.
        var owners = jobMutation
            ? db.McpOperatorJobs.AsNoTracking().Where(job => job.JobId == identifier && job.DeletedAtUtc != null)
                .Select(job => new ReplayOwner(job.TenantId, job.AgentId, job.Subject, job.ClientId, job.McpResource, job.McpInstance, job.TargetSetDigest))
            : from run in db.McpOperatorJobRuns.AsNoTracking()
              join job in db.McpOperatorJobs.AsNoTracking() on run.JobRecordId equals job.Id
              where run.JobRunId == identifier && run.DeletedAtUtc != null && run.JobId == job.JobId &&
                    run.TenantId == job.TenantId && run.AgentId == job.AgentId
              select new ReplayOwner(run.TenantId, run.AgentId, job.Subject, job.ClientId, job.McpResource, job.McpInstance, run.TargetSetDigest);
        var matches = await owners.Take(2).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (matches is not [var owner] || owner.TenantId != delegation.TenantId || owner.AgentId != delegation.AgentId ||
            owner.Subject != delegation.Identity.Subject || owner.ClientId != delegation.Identity.ClientId ||
            owner.Resource != delegation.Resource || owner.Instance != delegation.Instance || string.IsNullOrWhiteSpace(owner.TargetSetDigest)) return false;

        var operation = McpOperatorOperationCatalog.Find(delegation.Tool, delegation.Operation);
        var plan = await db.McpOperatorConfirmationPlans.AsNoTracking()
            .SingleOrDefaultAsync(record => record.TokenHash == binding.PlanTokenHash, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (operation is null || plan?.ConsumedIdempotencyId is not { } receiptId || plan.ConsumedAtUtc is null ||
            plan.ExpiresAtUtc <= now || plan.Subject != owner.Subject || plan.ClientId != owner.ClientId ||
            plan.McpResource != owner.Resource || plan.McpInstance != owner.Instance ||
            plan.TenantId != owner.TenantId || plan.AgentId != owner.AgentId || plan.TargetSetDigest != owner.TargetSetDigest ||
            plan.Operation != $"{delegation.Tool}/{delegation.Operation}" || plan.OperationFamily != operation.OperationFamily ||
            plan.ConfirmationClass != operation.ConfirmationClass || plan.PayloadHash != binding.PayloadHash ||
            McpOperatorReplayBinding.Hash(plan.IdempotencyKey) != binding.IdempotencyKeyHash) return false;

        var receipt = await db.McpOperatorIdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == receiptId, cancellationToken).ConfigureAwait(false);
        if (receipt is null || receipt.Outcome != McpOperatorIdempotencyOutcome.Succeeded || receipt.CompletedAtUtc is null ||
            receipt.Environment != plan.Environment || receipt.Subject != plan.Subject || receipt.ClientId != plan.ClientId ||
            receipt.TenantId != plan.TenantId || receipt.AgentId != plan.AgentId || receipt.TargetSetDigest != plan.TargetSetDigest ||
            receipt.PolicyId != plan.PolicyId || receipt.PolicyVersion != plan.PolicyVersion ||
            receipt.OperationFamily != plan.OperationFamily || receipt.Operation != plan.Operation ||
            receipt.IdempotencyKey != plan.IdempotencyKey || receipt.PayloadHash != plan.PayloadHash ||
            !ResultMatchesObject(receipt.ResultReference, identifier, jobMutation)) return false;

        // This inexpensive withdrawal check is repeated at execution ingress.
        // Route admission and ReplayOnly still evaluate the complete current
        // policy, target profile, permission and request before returning data.
        return await db.McpOperatorPolicies.AsNoTracking().AnyAsync(policy => policy.Id == plan.PolicyId &&
            policy.Version == plan.PolicyVersion && policy.Environment == plan.Environment &&
            policy.Effect == McpOperatorPolicyEffect.Allow && policy.DisabledAtUtc == null &&
            policy.LifecycleState == McpOperatorPolicyLifecycleState.Active &&
            (policy.ExpiresAtUtc == null || policy.ExpiresAtUtc > now), cancellationToken).ConfigureAwait(false);
    }

    private static bool ResultMatchesObject(string? reference, long identifier, bool jobMutation)
    {
        if (PositiveDecimal(reference, out var legacyId)) return legacyId == identifier;
        var parts = reference?.Split(':');
        if (jobMutation)
            return parts is ["job-mutation", var jobIdText, var versionText, var itemIdText] &&
                   PositiveDecimal(jobIdText, out var jobId) && jobId == identifier && PositiveDecimal(versionText, out _) &&
                   (itemIdText.Length == 0 || PositiveDecimal(itemIdText, out _));
        return parts is ["job-run-action", var runIdText, var stateText] &&
               PositiveDecimal(runIdText, out var runId) && runId == identifier &&
               Enum.TryParse<JobRunState>(stateText, out var state) && Enum.IsDefined(state) && state.ToString() == stateText;
    }

    private static bool PositiveDecimal(string? value, out long parsed) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed > 0;

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
    private sealed record ReplayOwner(int TenantId, Guid AgentId, string Subject, string ClientId, string Resource, string Instance, string TargetSetDigest);
}
