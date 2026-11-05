using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

/// <summary>
/// Append-only source facts committed with their authoritative state transition. This is not a dispatcher;
/// a flow subscription must separately admit its immutable version and current execution authority.
/// </summary>
public static class ExecutionLifecycleOutbox
{
    internal static void CaptureTracked(OrchestratorDbContext db)
    {
        PreserveFacts(db);
        foreach (var candidate in Candidates(db))
        {
            var value = candidate.Event;
            if (candidate.Request is { } request && LinkedJob(db, request) is { } runId)
                value = CorrelateJob(value, runId);
            Append(db, value);
        }
    }

    internal static async Task CaptureTrackedAsync(OrchestratorDbContext db, CancellationToken ct)
    {
        PreserveFacts(db);
        foreach (var candidate in Candidates(db))
        {
            var value = candidate.Event;
            if (candidate.Request is { } request && await LinkedJobAsync(db, request, ct) is { } runId)
                value = CorrelateJob(value, runId);
            Append(db, value);
        }
    }

    /// <summary>For relational ExecuteUpdate writers, call under the same request lock/transaction before commit.</summary>
    public static async Task RecordRequestAsync(OrchestratorDbContext db, RequestRecord row, string previousStatus,
        DateTimeOffset observedAtUtc, CancellationToken ct)
    {
        var value = RequestEvent(row, previousStatus, observedAtUtc);
        if (value is null) return;
        if (await LinkedJobAsync(db, row, ct) is { } runId) value = CorrelateJob(value, runId);
        Append(db, value);
    }

    /// <summary>
    /// Call only after the final accepted terminal body/hash is complete, before the control record save.
    /// The caller's existing job owner transaction commits the terminal-ready marker and event together.
    /// </summary>
    public static async Task RecordJobAsync(OrchestratorDbContext db, JobRunDetails details, string resultHash,
        DateTimeOffset terminalReadyAtUtc, CancellationToken ct)
    {
        if (details.Run.TenantId is not > 0) return;
        var outcome = FlowExecutionOutcomes.FromAuthoritativeStatus(details.Run.Status.ToString());
        if (outcome is null || resultHash != JobRunProjectionHash.Compute(details))
            throw new InvalidOperationException("A lifecycle event requires the accepted complete terminal result.");
        var row = await db.JobRuns.AsNoTracking().SingleAsync(x => x.Id == checked((long)details.Run.Id), ct);
        var metadata = JobRunInputProtection.Metadata(row);
        var id = Decimal(row.Id);
        var occurred = details.Run.CompletedAtUtc ?? terminalReadyAtUtc;
        var definition = new FlowExecutionDefinitionDto(Decimal(row.JobId), Bound(metadata.DefinitionName, 512),
            metadata.DefinitionRevision);
        var target = new FlowExecutionTargetDto(row.AgentId, Bound(row.TargetDisplayName ?? row.TargetName, 512),
            Bound(row.TargetHostName, 512));
        var value = Create(row.TenantId!.Value, FlowExecutionSourceKind.Job, definition, id, id, target,
            outcome.Value, details.Run.Status.ToString(), occurred, terminalReadyAtUtc) with
        {
            StartedAtUtc = details.Run.StartedAtUtc,
            CompletedAtUtc = details.Run.CompletedAtUtc,
            CorrelationId = "job-run:" + id,
            RootExecutionId = id,
            ReasonCode = "job_" + outcome.Value.ToString().ToLowerInvariant(),
            Results = [new("job-result", id, resultHash, Available: true)],
            // A revision/hash alone does not prove that a full original definition is replayable.
            Replay = new(FlowExecutionSourceKind.Job, definition, target, "job-run:" + id, id, id, false,
                "canonical_replay_admission_required"),
            Provenance = "job-terminal-ready"
        };
        if (db.OutboxMessages.Local.Any(x => x.Id == value.EventId) ||
            await db.OutboxMessages.AsNoTracking().AnyAsync(x => x.Id == value.EventId, ct)) return;
        Append(db, value);
    }

    public static FlowExecutionEventDto? RequestEvent(RequestRecord row, string? previousStatus, DateTimeOffset observedAtUtc)
    {
        if (row.TargetTenantId is not > 0 || row.Id <= 0 ||
            string.Equals(row.Status, previousStatus, StringComparison.OrdinalIgnoreCase) ||
            FlowExecutionOutcomes.FromAuthoritativeStatus(row.Status) is not { } outcome) return null;
        var id = row.Id.ToString(CultureInfo.InvariantCulture);
        var execution = PositiveDecimal(row.ExecutionId);
        var attempt = execution ?? id;
        var metadata = JobRunInputProtection.Metadata(new() { InputSensitivityJson = row.InputSensitivityJson });
        var definition = new FlowExecutionDefinitionDto(PositiveDecimal(row.JobDefinitionId), Bound(metadata.DefinitionName, 512),
            metadata.DefinitionRevision);
        var target = new FlowExecutionTargetDto(row.TargetAgentId);
        return Create(row.TargetTenantId.Value, FlowExecutionSourceKind.Request, definition, id, attempt, target,
            outcome, row.Status.Trim(), row.UpdatedAtUtc, observedAtUtc) with
        {
            StartedAtUtc = row.CreatedAtUtc <= row.UpdatedAtUtc ? row.CreatedAtUtc : null,
            CompletedAtUtc = row.UpdatedAtUtc,
            RequestKind = Bound(row.SourceSystem, FlowExecutionEventLimits.MaximumIdentifierLength),
            CorrelationId = "request:" + id,
            ReasonCode = "request_" + outcome.ToString().ToLowerInvariant(),
            Results = [new("request-result", id, Digest(row.ResultData), row.ResultData is not null)],
            Replay = new(FlowExecutionSourceKind.Request, definition, target, "request:" + id, id, attempt,
                false, "request_replay_binding_unavailable"),
            Provenance = "request-state-commit"
        };
    }

    public static FlowExecutionEventDto? ScriptEvent(JobTaskActivityRecord row, string? previousStatus, DateTimeOffset observedAtUtc)
    {
        if (row.TenantId is not > 0 || string.IsNullOrWhiteSpace(row.RequestId) ||
            row.TaskType is not (TaskKinds.ExecLibraryScript or TaskKinds.Legacy_RunLibraryScript) ||
            string.Equals(row.Status, previousStatus, StringComparison.OrdinalIgnoreCase) ||
            FlowExecutionOutcomes.FromAuthoritativeStatus(row.Status) is not { } outcome) return null;
        var policy = TaskOutputProtection.Metadata(row);
        var definition = new FlowExecutionDefinitionDto(policy?.ScriptId?.ToString(CultureInfo.InvariantCulture),
            Bound(row.ScriptName, 512), policy?.ScriptRevision?.ToString(CultureInfo.InvariantCulture), policy?.ScriptContentHash);
        var target = new FlowExecutionTargetDto(row.AgentId, Bound(row.TargetDisplayName ?? row.TargetName, 512),
            Bound(row.TargetHostName, 512));
        var result = TaskOutputProtection.Project(row, row.ResultJson);
        var id = row.RequestId;
        var parent = row.JobRunId?.ToString(CultureInfo.InvariantCulture);
        return Create(row.TenantId.Value, FlowExecutionSourceKind.Script, definition, id, id, target,
            outcome, row.Status.Trim(), row.CompletedAtUtc ?? observedAtUtc, observedAtUtc) with
        {
            StartedAtUtc = row.StartedAtUtc,
            CompletedAtUtc = row.CompletedAtUtc,
            ParentExecutionId = parent,
            RootExecutionId = parent ?? id,
            CorrelationId = parent is null ? BoundedCorrelation("script-execution:", id) : "job-run:" + parent,
            ExitCode = row.ExitCode,
            ReasonCode = "script_" + outcome.ToString().ToLowerInvariant(),
            Results = [new("script-result", id, Digest(result), result is not null)],
            Replay = new(FlowExecutionSourceKind.Script, definition, target, "script-execution:" + id, id, id,
                false, "canonical_replay_admission_required"),
            Provenance = "script-terminal-commit"
        };
    }

    private static IReadOnlyList<(FlowExecutionEventDto Event, RequestRecord? Request)> Candidates(OrchestratorDbContext db)
    {
        db.ChangeTracker.DetectChanges();
        var candidates = new List<(FlowExecutionEventDto, RequestRecord?)>();
        var observed = DateTimeOffset.UtcNow;
        foreach (var entry in db.ChangeTracker.Entries<RequestRecord>().Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.State == EntityState.Modified && !entry.Property(x => x.Status).IsModified) continue;
            var previous = entry.State == EntityState.Added ? null : entry.Property(x => x.Status).OriginalValue;
            if (RequestEvent(entry.Entity, previous, observed) is { } value) candidates.Add((value, entry.Entity));
        }
        foreach (var entry in db.ChangeTracker.Entries<JobTaskActivityRecord>().Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.State == EntityState.Modified && !entry.Property(x => x.Status).IsModified) continue;
            var previous = entry.State == EntityState.Added ? null : entry.Property(x => x.Status).OriginalValue;
            if (ScriptEvent(entry.Entity, previous, observed) is { } value) candidates.Add((value, null));
        }
        return candidates;
    }

    private static IQueryable<JobRunRecord> LinkedJobs(OrchestratorDbContext db, RequestRecord row)
    {
        var executionId = PositiveDecimal(row.ExecutionId);
        var definitionId = PositiveDecimal(row.JobDefinitionId);
        if (!long.TryParse(executionId, NumberStyles.None, CultureInfo.InvariantCulture, out var runId) ||
            !long.TryParse(definitionId, NumberStyles.None, CultureInfo.InvariantCulture, out var jobId))
            return db.JobRuns.Where(_ => false);
        // Correlation identifies the persisted accepted submission, not a fabricated terminal outcome.
        // Combined subscriptions wait for this known parent's terminal-ready result separately.
        return db.JobRuns.AsNoTracking().Where(run => run.Id == runId && run.JobId == jobId &&
            run.TenantId == row.TargetTenantId && run.AgentId == row.TargetAgentId && run.StartedBy == row.SourceSystem);
    }

    private static string? LinkedJob(OrchestratorDbContext db, RequestRecord row) =>
        LinkedJobs(db, row).Select(run => run.Id).SingleOrDefault() is > 0 and var id ? Decimal(id) : null;

    private static async Task<string?> LinkedJobAsync(OrchestratorDbContext db, RequestRecord row, CancellationToken ct) =>
        await LinkedJobs(db, row).Select(run => run.Id).SingleOrDefaultAsync(ct) is > 0 and var id ? Decimal(id) : null;

    private static FlowExecutionEventDto CorrelateJob(FlowExecutionEventDto value, string runId) => value with
    {
        CorrelationId = "job-run:" + runId,
        ParentExecutionId = runId,
        RootExecutionId = runId,
        AttemptId = runId
    };

    private static FlowExecutionEventDto Create(int tenantId, FlowExecutionSourceKind source,
        FlowExecutionDefinitionDto definition, string executionId, string attemptId, FlowExecutionTargetDto target,
        FlowExecutionOutcome outcome, string rawStatus, DateTimeOffset occurred, DateTimeOffset observed)
    {
        var occurrence = Identity($"occurrence|{tenantId}|{(int)source}|{executionId}|{attemptId}");
        var eventId = Identity($"event|{occurrence:N}|{(int)outcome}|{rawStatus.ToUpperInvariant()}|{occurred.UtcTicks}");
        return new(eventId, occurrence, tenantId, source, definition, executionId, attemptId, target, outcome,
            rawStatus, occurred, observed) { Causation = new(occurrence.ToString("N")) };
    }

    private static void Append(OrchestratorDbContext db, FlowExecutionEventDto value)
    {
        if (db.OutboxMessages.Local.Any(x => x.Id == value.EventId)) return;
        var validation = FlowExecutionEventValidator.Validate(value);
        if (!validation.Valid) throw new InvalidOperationException("Invalid execution lifecycle event: " + string.Join(", ", validation.Issues));
        db.OutboxMessages.Add(new()
        {
            Id = value.EventId, OccurredUtc = value.OccurredAtUtc, Type = FlowExecutionEventLimits.OutboxType,
            PayloadJson = JsonSerializer.Serialize(value, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto),
            Source = value.Provenance, CorrelationId = value.CorrelationId ?? value.OccurrenceId.ToString("N"),
            TenantId = value.TenantId.ToString(CultureInfo.InvariantCulture), EntityId = value.ExecutionId,
            Severity = value.Outcome == FlowExecutionOutcome.Succeeded ? "Info" : "Warning",
            Status = OutboxStatuses.Pending, NextAttemptUtc = value.ObservedAtUtc
        });
    }

    private static void PreserveFacts(OrchestratorDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<OutboxMessage>().Where(x => x.State is EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Property(x => x.Type).OriginalValue != FlowExecutionEventLimits.OutboxType) continue;
            if (entry.State == EntityState.Deleted || entry.Property(x => x.PayloadJson).IsModified ||
                entry.Property(x => x.Type).IsModified || entry.Property(x => x.OccurredUtc).IsModified ||
                entry.Property(x => x.TenantId).IsModified || entry.Property(x => x.EntityId).IsModified ||
                entry.Property(x => x.Source).IsModified || entry.Property(x => x.CorrelationId).IsModified)
                throw new InvalidOperationException("Execution lifecycle facts are append-only.");
        }
    }

    private static string? PositiveDecimal(string? value) => ulong.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var id) && id > 0 ? id.ToString(CultureInfo.InvariantCulture) : null;
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string? Bound(string? value, int limit)
    {
        if (value is null) return null;
        var length = Math.Min(value.Length, limit);
        if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1]) &&
            char.IsLowSurrogate(value[length])) length--;
        return value[..length];
    }
    private static string BoundedCorrelation(string prefix, string id) =>
        prefix.Length + id.Length <= FlowExecutionEventLimits.MaximumIdentifierLength ? prefix + id : prefix + "sha256:" + Digest(id);

    private static string? Digest(string? value) => value is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static Guid Identity(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes("NetRatel.Flow.Execution.V1|" + value)).AsSpan(0, 16));
}
