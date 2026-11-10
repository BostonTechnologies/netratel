using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services.Jobs;

/// <summary>Resumes accepted protected material, including the interval before a parent exists.</summary>
public sealed class McpReviewedJobAdmissionRecovery(OrchestratorDbContext db, IMcpOperatorConfirmationService confirmations,
    IMcpAggregateExecutionAuthority verifier, IAkkaJobAuthorityService authority, IMcpOperatorJobStore jobs,
    ILogger<McpReviewedJobAdmissionRecovery> logger)
{
    public async Task<Guid?> SweepAsync(Guid? after = null, CancellationToken ct = default)
    {
        var query = from material in db.Set<McpReviewedJobAdmissionRecord>().AsNoTracking()
                    join receipt in db.McpOperatorIdempotencyRecords.AsNoTracking() on material.IdempotencyId equals receipt.Id
                    where receipt.Outcome == McpOperatorIdempotencyOutcome.Pending && receipt.Operation == "netratel_job_runs/start"
                    select material.IdempotencyId;
        if (after is { } cursor) query = query.Where(id => id.CompareTo(cursor) > 0);
        var ids = await query.Order().Take(16).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var id in ids)
        {
            try
            {
                var original = await confirmations.ReadReviewedJobForRecoveryAsync(id, ct).ConfigureAwait(false);
                if (original is null) continue; // Missing protection remains conservative and never releases capacity.
                var snapshot = original.Snapshot;
                var key = McpAggregateAdmissionKey.For(snapshot.TargetPlan!.TenantId, snapshot.Subject, id);
                var parent = await db.JobAggregateControls.AsNoTracking().Where(row => row.AdmissionKey == key)
                    .Select(row => (long?)row.ParentRunId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
                ulong runId;
                if (parent is { } committed) runId = checked((ulong)committed);
                else
                {
                    foreach (var node in snapshot.TargetPlan.Nodes)
                        await verifier.RequireAllowedAsync(snapshot, node.AgentId, ct).ConfigureAwait(false);
                    runId = (await authority.StartReviewedMcpAsync(snapshot, original.Request, ct).ConfigureAwait(false)).Id;
                }
                await jobs.RecordAdmittedRunAsync(id, runId, ct).ConfigureAwait(false);
                await confirmations.CompleteAsync(id, McpOperatorIdempotencyOutcome.Succeeded,
                    runId.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            }
            catch (Exception denial) when (denial is ExecutionAdmissionDeniedException or McpOperatorAdmissionRejectedException)
            {
                // Runtime and this finalizer share the admission fence. If a
                // parent won the race, retain it for the next repair sweep.
                await confirmations.FailReviewedJobBeforeParentAsync(id, "job_current_authority_denied", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception failure)
            {
                logger.LogWarning("Reviewed job admission recovery failed: {FailureType}.", failure.GetType().Name);
            }
        }
        return ids.Length == 0 ? null : ids[^1];
    }
}

public sealed class McpReviewedJobAdmissionRecoveryWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<McpReviewedJobAdmissionRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Guid? after = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                after = await scope.ServiceProvider.GetRequiredService<McpReviewedJobAdmissionRecovery>()
                    .SweepAsync(after, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception failure)
            {
                logger.LogWarning("Reviewed job admission sweep failed: {FailureType}.", failure.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
