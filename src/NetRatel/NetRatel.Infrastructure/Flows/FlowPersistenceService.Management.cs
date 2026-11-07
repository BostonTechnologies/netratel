using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

public sealed partial class FlowPersistenceService
{
    public Task<FlowSummaryDto?> GetMetadataAsync(int tenantId, Guid flowId, CancellationToken ct = default) =>
        WithDb<FlowSummaryDto?>(async (db, _) => await db.FlowDefinitions.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.Id == flowId)
            .Select(row => new FlowSummaryDto(row.Id, row.TenantId, row.Name, row.Revision, row.Enabled,
                row.PublishedVersionId, row.PublishedVersionNumber, row.UpdatedAtUtc, null, row.DeletedAtUtc))
            .SingleOrDefaultAsync(ct).ConfigureAwait(false));

    public Task<FlowDeleteResultDto> DeleteAsync(int tenantId, Guid flowId, FlowDeleteRequest request,
        string actorId, CancellationToken ct = default, string? correlationId = null) => WithDb(async (db, _) =>
    {
        FlowDeleteResultDto Result(FlowDeleteDisposition disposition, string code, FlowDefinitionRecord? row = null) =>
            new(tenantId, flowId, row?.Revision, disposition, code, row?.DeletedAtUtc);
        if (tenantId <= 0 || flowId == Guid.Empty || request.ExpectedRevision <= 0 || !ValidActor(actorId) || correlationId is { Length: > 128 })
            return Result(FlowDeleteDisposition.Invalid, "flow-delete-invalid");
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false);
        await TenantLockAsync(db, tenantId, ct).ConfigureAwait(false);
        var row = db.Database.IsNpgsql()
            ? await db.FlowDefinitions.FromSqlInterpolated($"SELECT * FROM \"FlowDefinitions\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {flowId} FOR UPDATE")
                .SingleOrDefaultAsync(ct).ConfigureAwait(false)
            : await db.FlowDefinitions.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == flowId, ct).ConfigureAwait(false);
        if (row is null) return Result(FlowDeleteDisposition.NotFound, "flow-not-found");
        if (row.DeletedAtUtc is not null)
            return row.DeletedFromRevision == request.ExpectedRevision
                ? Result(FlowDeleteDisposition.AlreadyDeleted, "flow-already-deleted", row)
                : Result(FlowDeleteDisposition.Conflict, "flow-revision-conflict", row);
        if (row.Revision != request.ExpectedRevision)
            return Result(FlowDeleteDisposition.Conflict, "flow-revision-conflict", row);
        // Worker claims/resumes only these actual states. Terminal checkpoint JSON, accepted IDs,
        // MayHaveCommitted and unknown outcomes are retained evidence, not a scheduling predicate.
        var unfinished = new[] { FlowRunStatus.Queued, FlowRunStatus.Running, FlowRunStatus.RetryWaiting,
            FlowRunStatus.WaitingForAi, FlowRunStatus.AwaitingApproval, FlowRunStatus.WaitingForExecution };
        var now = await ActivationNowAsync(db, ct).ConfigureAwait(false);
        if (await db.FlowRuns.AnyAsync(run => run.TenantId == tenantId && run.FlowId == flowId &&
            (unfinished.Contains(run.Status) || run.LeaseExpiresAtUtc > now), ct).ConfigureAwait(false))
            return Result(FlowDeleteDisposition.ActiveWork, "flow-active-work", row);
        // A terminal Flow may still own a live native execution or an admitted automated AI turn.
        // Inspect their current scheduling state, never retained dispatch timestamps or payload JSON.
        var runIds = db.FlowRuns.Where(run => run.TenantId == tenantId && run.FlowId == flowId).Select(run => run.Id);
        var native = db.Set<ExecutionAdmissionRecord>().Where(admission => admission.TenantId == tenantId &&
            db.FlowVersions.Any(version => version.TenantId == tenantId && version.FlowId == flowId && version.Id == admission.FlowVersionId));
        var liveNative = await native.AnyAsync(admission =>
            admission.Kind == (int)ExecutionAdmissionKind.Job && db.JobRuns.Any(run => run.TenantId == tenantId && run.Id.ToString() == admission.ExecutionId &&
                (run.Status == (int)JobRunState.Pending || run.Status == (int)JobRunState.Running ||
                    db.Set<JobRunControlRecord>().Any(control => control.RunId == run.Id && control.TerminalReadyAtUtc == null))), ct).ConfigureAwait(false);
        // Native jobs have their own resumable owner. A script has no independent resend/recovery
        // worker after its Flow becomes terminal: a retained Pending activity/admission from an
        // uncertain send is historical evidence, not a live local dispatch obligation. A physical
        // Flow send still in progress is covered by the Flow state/lease check above.
        var liveAi = await db.Set<AiInvestigationRecord>().AnyAsync(investigation => investigation.TenantId == tenantId &&
            investigation.Automated && investigation.FlowRunId != null && runIds.Contains(investigation.FlowRunId.Value) &&
            (investigation.State == InvestigationState.Queued || investigation.State == InvestigationState.Processing ||
                investigation.State == InvestigationState.AwaitingApproval), ct).ConfigureAwait(false);
        if (liveNative || liveAi) return Result(FlowDeleteDisposition.ActiveWork, "flow-active-work", row);
        row.Enabled = false;
        row.DeletedFromRevision = row.Revision;
        row.Revision = checked(row.Revision + 1);
        row.DeletedAtUtc = now; row.DeletedBy = actorId; row.UpdatedAtUtc = now;
        db.FlowAudits.Add(new() { Id = Guid.NewGuid(), TenantId = tenantId, FlowId = flowId, Revision = row.Revision,
            ActorId = actorId, Operation = "deleted", AtUtc = now, CorrelationId = correlationId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return Result(FlowDeleteDisposition.Deleted, "flow-deleted", row);
    });

    public Task<FlowPageDto> QueryAsync(int tenantId, FlowQueryDto query, CancellationToken ct = default) => WithDb(async (db, _) =>
    {
        if (tenantId <= 0 || !query.IsValid) throw new ArgumentException("flow-query-invalid");
        var definitions = db.FlowDefinitions.AsNoTracking().Where(row => row.TenantId == tenantId && row.DeletedAtUtc == null);
        if (query.Search?.Trim() is { Length: > 0 } search)
        {
            // Contains translates to a parameterized literal substring, including %, _ and backslash.
            var literal = search.ToLowerInvariant();
            definitions = definitions.Where(row => row.Name.ToLower().Contains(literal));
        }
        if (query.Enabled != FlowEnabledFilter.All)
            definitions = definitions.Where(row => row.Enabled == (query.Enabled == FlowEnabledFilter.Enabled));
        if (query.Publication != FlowPublicationFilter.All)
            definitions = definitions.Where(row => (row.PublishedVersionId != null) == (query.Publication == FlowPublicationFilter.Published));
        var total = await definitions.CountAsync(ct).ConfigureAwait(false);
        // The correlated top-one is ordered before paging; no graphs or historical run batches are read.
        var candidates = definitions.Select(row => new
        {
            row.Id, row.TenantId, row.Name, row.Revision, row.Enabled, row.PublishedVersionId,
            row.PublishedVersionNumber, row.UpdatedAtUtc,
            LatestAt = db.FlowRuns.Where(run => run.TenantId == tenantId && run.FlowId == row.Id)
                .OrderByDescending(run => run.CreatedAtUtc).ThenByDescending(run => run.Id)
                .Select(run => (DateTimeOffset?)run.CreatedAtUtc).FirstOrDefault(),
            Latest = db.FlowRuns.Where(run => run.TenantId == tenantId && run.FlowId == row.Id)
                .OrderByDescending(run => run.CreatedAtUtc).ThenByDescending(run => run.Id)
                .Select(run => new { run.Id, run.FlowId, run.FlowVersionId, run.EventId,
                    run.OccurrenceId, run.Status, run.CreatedAtUtc, run.CompletedAtUtc, run.Code }).FirstOrDefault()
        });
        var ordered = query.Sort switch
        {
            FlowSort.UpdatedAt => query.Descending ? candidates.OrderByDescending(row => row.UpdatedAtUtc).ThenBy(row => row.Id)
                : candidates.OrderBy(row => row.UpdatedAtUtc).ThenBy(row => row.Id),
            FlowSort.LatestRunAt => query.Descending
                ? candidates.OrderBy(row => row.LatestAt == null).ThenByDescending(row => row.LatestAt).ThenBy(row => row.Id)
                : candidates.OrderBy(row => row.LatestAt == null).ThenBy(row => row.LatestAt).ThenBy(row => row.Id),
            _ => query.Descending ? candidates.OrderByDescending(row => row.Name).ThenBy(row => row.Id)
                : candidates.OrderBy(row => row.Name).ThenBy(row => row.Id)
        };
        var items = await ordered.Skip(checked(query.PageIndex * query.PageSize)).Take(query.PageSize)
            .Select(row => new FlowSummaryDto(row.Id, row.TenantId, row.Name, row.Revision, row.Enabled,
                row.PublishedVersionId, row.PublishedVersionNumber, row.UpdatedAtUtc, row.Latest == null ? null : new FlowRunSummaryDto(row.Latest.Id, row.Latest.FlowId, row.Latest.FlowVersionId, row.Latest.EventId, row.Latest.OccurrenceId, row.Latest.Status, row.Latest.CreatedAtUtc, row.Latest.CompletedAtUtc, row.Latest.Code), null)).ToArrayAsync(ct).ConfigureAwait(false);
        return new FlowPageDto(items, total, query.PageIndex, query.PageSize);
    });
}
