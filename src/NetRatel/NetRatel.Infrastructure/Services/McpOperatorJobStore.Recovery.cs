using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed partial class McpOperatorJobStore
{
    public async Task<McpOperatorJobRunLease> RecordAdmittedRunAsync(Guid idempotencyId, ulong runId, CancellationToken ct)
    {
        var receipt = await _db.McpOperatorIdempotencyRecords.AsNoTracking().SingleAsync(row => row.Id == idempotencyId, ct).ConfigureAwait(false);
        var material = await _db.Set<McpReviewedJobAdmissionRecord>().AsNoTracking().SingleAsync(row => row.IdempotencyId == idempotencyId, ct).ConfigureAwait(false);
        var plan = await _db.McpOperatorConfirmationPlans.AsNoTracking().SingleAsync(row => row.ConsumedIdempotencyId == idempotencyId, ct).ConfigureAwait(false);
        if (runId == 0 || runId > long.MaxValue || receipt.Operation != "netratel_job_runs/start" ||
            receipt.Outcome is not (McpOperatorIdempotencyOutcome.Pending or McpOperatorIdempotencyOutcome.Succeeded) ||
            receipt.TenantId != material.TenantId || plan.PayloadHash != receipt.PayloadHash ||
            plan.Subject != receipt.Subject || plan.ClientId != receipt.ClientId)
            throw new InvalidOperationException("The original reviewed run admission is unavailable.");
        var key = McpAggregateAdmissionKey.For(receipt.TenantId, receipt.Subject, idempotencyId);
        await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({McpAggregateAdmissionKey.LockKey(key)})", ct).ConfigureAwait(false);
        var projection = await _db.JobRuns.AsNoTracking().SingleOrDefaultAsync(row => row.Id == checked((long)runId) &&
            row.JobId == material.JobId && row.TenantId == receipt.TenantId && row.IsAggregate, ct).ConfigureAwait(false);
        if (projection is null || !await _db.JobAggregateControls.AsNoTracking().AnyAsync(row => row.ParentRunId == checked((long)runId) &&
                row.AdmissionKey == key, ct).ConfigureAwait(false) ||
            !await _db.Set<McpJobTargetReservationRecord>().AsNoTracking().AnyAsync(row => row.IdempotencyId == idempotencyId &&
                row.TargetPlanDigest == projection.TargetPlanDigest, ct).ConfigureAwait(false))
            throw new InvalidOperationException("The original reviewed admission has no matching committed parent.");
        var existing = await _db.McpOperatorJobRuns.SingleOrDefaultAsync(row => row.IdempotencyId == idempotencyId ||
            row.JobRunId == checked((long)runId), ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.IdempotencyId != idempotencyId || existing.JobRunId != checked((long)runId) || existing.JobId != material.JobId)
                throw new InvalidOperationException("The reviewed receipt is already bound to another parent.");
            return ToRunLease(existing, projection);
        }
        var owner = await _db.McpOperatorJobs.SingleOrDefaultAsync(row => row.JobId == material.JobId && row.TenantId == receipt.TenantId &&
            row.AgentId == receipt.AgentId && row.Subject == receipt.Subject && row.ClientId == receipt.ClientId &&
            row.McpResource == plan.McpResource && row.McpInstance == plan.McpInstance, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The original reviewed owner binding is unavailable.");
        var audit = await _db.McpOperatorAcceptedAudits.AsNoTracking().SingleOrDefaultAsync(row => row.Id == material.AcceptedAuditId &&
            row.TenantId == receipt.TenantId && row.Subject == receipt.Subject && row.ClientId == receipt.ClientId &&
            row.Operation == receipt.Operation, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The original accepted operation audit is unavailable.");
        var now = DateTimeOffset.UtcNow;
        var record = new McpOperatorJobRunRecord
        {
            Id = Guid.NewGuid(), JobRunId = checked((long)runId), JobRecordId = owner.Id, JobId = material.JobId,
            TenantId = receipt.TenantId, AgentId = owner.AgentId, AcceptedAuditId = audit.Id, IdempotencyId = idempotencyId,
            CorrelationId = audit.CorrelationId, TargetSetDigest = receipt.TargetSetDigest,
            CreatedAtUtc = now, UpdatedAtUtc = now, Version = 1
        };
        _db.McpOperatorJobRuns.Add(record);
        _db.McpOperatorJobRunAudits.Add(RunAudit(record, "started", audit.Id, now));
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return ToRunLease(record, projection);
    }
}
