using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed partial class McpOperatorConfirmationService
{
    private async Task SaveReviewedSubmissionAsync(Guid id, McpReviewedJobSubmission submission,
        McpOperatorConfirmationRequest request, CancellationToken ct)
    {
        if (protection is null) throw new InvalidOperationException("Reviewed job protection is unavailable.");
        var record = await _db.McpOperatorIdempotencyRecords.SingleAsync(row => row.Id == id, ct).ConfigureAwait(false);
        var plan = await _db.McpOperatorConfirmationPlans.AsNoTracking().SingleAsync(row => row.IdempotencyKey == record.IdempotencyKey &&
            row.Subject == record.Subject && row.ClientId == record.ClientId && row.TenantId == record.TenantId && row.Operation == record.Operation, ct).ConfigureAwait(false);
        var admitted = submission with { Snapshot = submission.Snapshot with { ReviewIdempotencyId = id } };
        RequireSubmission(admitted, record, plan);
        if (request.JobTargetPlanDigest != admitted.Snapshot.TargetPlan!.ComputeDigest())
            throw new InvalidOperationException("Reviewed job reservation does not match its protected plan.");
        var audit = await (authorization ?? new McpOperatorAuthorization(_db)).RecordAcceptedAsync(request.CurrentDecision,
            admitted.Snapshot.ReviewedAuthority!.Route.ServicePrincipal, ct).ConfigureAwait(false);
        _db.Add(new McpReviewedJobAdmissionRecord
        {
            IdempotencyId = id, AcceptedAuditId = audit.AuditId, TenantId = record.TenantId, JobId = checked((long)admitted.Snapshot.Definition.Job.Id),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ProtectedSubmissionJson = Protector(record, plan).Protect(JsonSerializer.Serialize(admitted))
        });
    }

    public async Task<McpReviewedJobSubmission?> GetReviewedJobSubmissionAsync(Guid idempotencyId,
        McpOperatorDecision currentDecision, CancellationToken cancellationToken)
    {
        if (protection is null || !IsUsableDecision(currentDecision)) return null;
        var record = await _db.McpOperatorIdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(row => row.Id == idempotencyId,
            cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        var access = currentDecision.Request;
        if (record.Outcome != McpOperatorIdempotencyOutcome.Pending || record.Subject != access.Principal.Subject ||
            record.ClientId != ClientId(access) || record.TenantId != access.TenantId || record.AgentId != access.AgentId ||
            record.Operation != access.Operation || record.Operation != "netratel_job_runs/start") return null;
        var plan = await _db.McpOperatorConfirmationPlans.AsNoTracking().SingleOrDefaultAsync(row => row.IdempotencyKey == record.IdempotencyKey &&
            row.Subject == record.Subject && row.ClientId == record.ClientId && row.TenantId == record.TenantId &&
            row.Operation == record.Operation, cancellationToken).ConfigureAwait(false);
        if (plan is null || plan.McpResource != access.McpResource || plan.McpInstance != access.McpInstance ||
            AdmissionFor(record, plan).FailureCode is not null) return null;
        return await ReadMaterialAsync(record, plan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpReviewedJobSubmission?> ReadReviewedJobForRecoveryAsync(Guid idempotencyId, CancellationToken ct)
    {
        if (protection is null) return null;
        var record = await _db.McpOperatorIdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(row => row.Id == idempotencyId &&
            row.Outcome == McpOperatorIdempotencyOutcome.Pending && row.Operation == "netratel_job_runs/start", ct).ConfigureAwait(false);
        if (record is null) return null;
        var plan = await _db.McpOperatorConfirmationPlans.AsNoTracking().SingleOrDefaultAsync(row => row.ConsumedIdempotencyId == idempotencyId,
            ct).ConfigureAwait(false);
        return plan is null || AdmissionFor(record, plan).FailureCode is not null ? null : await ReadMaterialAsync(record, plan, ct).ConfigureAwait(false);
    }

    private async Task<McpReviewedJobSubmission?> ReadMaterialAsync(McpOperatorIdempotencyRecord record,
        McpOperatorConfirmationPlanRecord plan, CancellationToken ct)
    {
        var material = await _db.Set<McpReviewedJobAdmissionRecord>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.IdempotencyId == record.Id, ct).ConfigureAwait(false);
        if (material is null || material.TenantId != record.TenantId) return null;
        try
        {
            var submission = McpReviewedJobSerialization.Deserialize<McpReviewedJobSubmission>(Protector(record, plan).Unprotect(material.ProtectedSubmissionJson));
            if (submission is null) return null;
            RequireSubmission(submission, record, plan);
            if (material.JobId != checked((long)submission.Snapshot.Definition.Job.Id)) return null;
            return submission;
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public async Task<bool> FailReviewedJobBeforeParentAsync(Guid idempotencyId, string failureCode, CancellationToken ct)
    {
        var submission = await ReadReviewedJobForRecoveryAsync(idempotencyId, ct).ConfigureAwait(false);
        if (submission is null || failureCode is not ("job_current_authority_denied" or "job_source_unavailable")) return false;
        var key = McpAggregateAdmissionKey.For(submission.Snapshot.TargetPlan!.TenantId, submission.Snapshot.Subject, idempotencyId);
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({McpAggregateAdmissionKey.LockKey(key)})", ct).ConfigureAwait(false);
        if (await _db.JobAggregateControls.AsNoTracking().AnyAsync(row => row.AdmissionKey == key, ct).ConfigureAwait(false)) return false;
        var receipt = await _db.McpOperatorIdempotencyRecords.SingleOrDefaultAsync(row => row.Id == idempotencyId, ct).ConfigureAwait(false);
        if (receipt is null) return false;
        await _db.Entry(receipt).ReloadAsync(ct).ConfigureAwait(false);
        if (receipt.Outcome != McpOperatorIdempotencyOutcome.Pending) return false;
        await CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, failureCode, ct).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private IDataProtector Protector(McpOperatorIdempotencyRecord record, McpOperatorConfirmationPlanRecord plan) =>
        protection!.CreateProtector("NetRatel.Mcp.ReviewedJobAdmission.v1", record.TenantId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.Subject, record.ClientId, plan.McpResource ?? "", plan.McpInstance ?? "", record.Id.ToString("N"));

    private static void RequireSubmission(McpReviewedJobSubmission submission, McpOperatorIdempotencyRecord record,
        McpOperatorConfirmationPlanRecord plan)
    {
        var snapshot = submission.Snapshot;
        if (snapshot.TargetPlan is not { } targets || snapshot.ReviewedAuthority is null || snapshot.ReviewIdempotencyId != record.Id ||
            snapshot.Subject != record.Subject || snapshot.ClientId != record.ClientId || snapshot.McpResource != plan.McpResource ||
            snapshot.McpInstance != plan.McpInstance || targets.TenantId != record.TenantId || targets.JobId != snapshot.Definition.Job.Id ||
            targets.DefinitionDigest != snapshot.ComputeDefinitionDigest() || submission.ComputePayloadHash() != plan.PayloadHash ||
            submission.ComputeRequestHash() != plan.JobRequestHash || submission.Request.ExpectedTargetPlanDigest != targets.ComputeDigest())
            throw new InvalidOperationException("Reviewed job material does not match its durable admission.");
    }
}
