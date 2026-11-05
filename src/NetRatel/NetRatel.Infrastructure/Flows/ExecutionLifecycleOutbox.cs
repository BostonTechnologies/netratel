using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Clients;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Security;

namespace NetRatel.Infrastructure.Flows;

/// <summary>
/// Append-only source facts committed with their authoritative state transition. This is not a dispatcher;
/// a flow subscription must separately admit its immutable version and current execution authority.
/// </summary>
public static class ExecutionLifecycleOutbox
{
    public const string EventTypePrefix = "Flow.Execution";
    public const string RequestBindingOutboxType = "Flow.ExecutionRequestBinding.V1";
    public const string RequestSubmissionOutboxType = "Flow.ExecutionRequestSubmission.V1";
    public const string RequestReservationOutboxType = "Flow.ExecutionRequestReservation.V1";
    private sealed record AcceptedRequestJobBinding(int RequestId, int TenantId, Guid AgentId,
        string JobDefinitionId, string ExecutionId, string SourceSystem);

    /// <summary>Trusted submit endpoint hook after tenant-authorized definition/target resolution and before job admission.</summary>
    public static async Task RecordSubmittedRequestIntentAsync(OrchestratorDbContext db, RequestInfo request, CancellationToken ct)
    {
        if (request.TargetTenantId is not > 0 || request.TargetAgentId is not { } agent ||
            PositiveDecimal(request.JobDefinitionId) is not { } definitionId || request.Id <= 0)
            throw new InvalidOperationException("A trusted submitted request requires its resolved job and bound target.");
        var id = Identity($"request-submission|{request.TargetTenantId}|{request.Id}");
        var payload = JsonSerializer.Serialize(new AcceptedRequestJobBinding(request.Id, request.TargetTenantId.Value,
            agent, definitionId, string.Empty, request.SourceSystem));
        var existing = db.OutboxMessages.Local.SingleOrDefault(x => x.Id == id) ??
            await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (existing is not null)
        {
            if (existing.Type != RequestSubmissionOutboxType || existing.PayloadJson != payload)
                throw new InvalidOperationException("The immutable request submission differs from its original target.");
            return;
        }
        db.OutboxMessages.Add(new()
        {
            Id = id, Type = RequestSubmissionOutboxType, PayloadJson = payload, Source = "request-submitted",
            CorrelationId = "request:" + request.Id, TenantId = request.TargetTenantId.Value.ToString(CultureInfo.InvariantCulture),
            EntityId = request.Id.ToString(CultureInfo.InvariantCulture), OccurredUtc = DateTimeOffset.UtcNow,
            Status = OutboxStatuses.Published
        });
    }

    /// <summary>Reserves only an identity; accepted binding and physical dispatch remain inside the native owner transaction.</summary>
    public static async Task<ulong> ReserveCanonicalRequestRunAsync(OrchestratorDbContext db, int requestId,
        Func<ulong> allocate, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Canonical request reservation must precede the native owner transaction.");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var query = db.Database.IsNpgsql()
            ? db.Requests.FromSqlInterpolated($"SELECT * FROM \"Requests\" WHERE \"Id\" = {requestId} FOR UPDATE")
            : db.Requests.Where(x => x.Id == requestId);
        var row = await query.SingleOrDefaultAsync(ct) ?? throw new InvalidOperationException("The submitted request is unavailable.");
        var submissionId = Identity($"request-submission|{row.TargetTenantId}|{requestId}");
        var submission = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == submissionId, ct);
        if (!MatchesSubmission(row, submission))
            throw new InvalidOperationException("Canonical request admission requires a trusted immutable submission.");
        var id = Identity($"request-reservation|{row.TargetTenantId}|{requestId}");
        var existing = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        ulong runId;
        if (existing is not null)
        {
            if (!MatchesBinding(row, existing, RequestReservationOutboxType) ||
                !ulong.TryParse(row.ExecutionId, NumberStyles.None, CultureInfo.InvariantCulture, out runId) || runId == 0 || runId > long.MaxValue)
                throw new InvalidOperationException("The immutable request reservation differs from its recorded identity.");
            if (FlowExecutionOutcomes.FromAuthoritativeStatus(row.Status) is not null)
            {
                var acceptedId = RequestBindingId(row.TargetTenantId!.Value, row.Id);
                var accepted = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == acceptedId, ct);
                if (!MatchesBinding(row, accepted))
                    throw new InvalidOperationException("A rejected request cannot reserve a new execution without a deliberate new submission.");
            }
        }
        else
        {
            if (row.ExecutionId is not null || FlowExecutionOutcomes.FromAuthoritativeStatus(row.Status) is not null)
                throw new InvalidOperationException("The request already has a different disposition.");
            runId = allocate();
            if (runId is 0 or > long.MaxValue) throw new InvalidOperationException("The reserved job identity is invalid.");
            var execution = runId.ToString(CultureInfo.InvariantCulture);
            var binding = new AcceptedRequestJobBinding(row.Id, row.TargetTenantId!.Value, row.TargetAgentId!.Value,
                PositiveDecimal(row.JobDefinitionId)!, execution, row.SourceSystem);
            row.ExecutionId = execution;
            db.OutboxMessages.Add(new()
            {
                Id = id, Type = RequestReservationOutboxType, PayloadJson = JsonSerializer.Serialize(binding), Source = "request-reserved-job",
                CorrelationId = "request:" + requestId, TenantId = binding.TenantId.ToString(CultureInfo.InvariantCulture),
                EntityId = requestId.ToString(CultureInfo.InvariantCulture), OccurredUtc = DateTimeOffset.UtcNow,
                Status = OutboxStatuses.Published
            });
            await db.SaveChangesAsync(ct);
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return runId;
    }

    /// <summary>Called only by the canonical submit writer after the actual job accepts its bound target.</summary>
    public static async Task RecordAcceptedRequestJobBindingAsync(OrchestratorDbContext db,
        RequestInfo request, JobRunInfo run, CancellationToken ct)
    {
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Accepted request binding requires the request link transaction.");
        if (request.TargetTenantId is not > 0 || request.TargetAgentId is not { } agent ||
            PositiveDecimal(request.JobDefinitionId) != run.JobId.ToString(CultureInfo.InvariantCulture) ||
            request.TargetTenantId != run.TenantId || agent != run.AgentId || request.SourceSystem != run.StartedBy)
            throw new InvalidOperationException("The accepted request differs from its canonical job target.");
        var execution = run.Id.ToString(CultureInfo.InvariantCulture);
        var id = RequestBindingId(request.TargetTenantId.Value, request.Id);
        var binding = new AcceptedRequestJobBinding(request.Id, request.TargetTenantId.Value, agent,
            run.JobId.ToString(CultureInfo.InvariantCulture), execution, request.SourceSystem);
        var json = JsonSerializer.Serialize(binding);
        var existing = db.OutboxMessages.Local.SingleOrDefault(x => x.Id == id) ??
            await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (existing is not null)
        {
            if (existing.Type != RequestBindingOutboxType || existing.PayloadJson != json)
                throw new InvalidOperationException("A submitted request already has a different accepted execution binding.");
            return;
        }
        db.OutboxMessages.Add(new()
        {
            Id = id, Type = RequestBindingOutboxType, PayloadJson = json, Source = "request-accepted-job",
            CorrelationId = "job-run:" + execution, TenantId = request.TargetTenantId.Value.ToString(CultureInfo.InvariantCulture),
            EntityId = request.Id.ToString(CultureInfo.InvariantCulture), OccurredUtc = DateTimeOffset.UtcNow,
            Status = OutboxStatuses.Published
        });
    }

    /// <summary>Native owner hook before first transport; the caller commits this with its accepted job intent.</summary>
    public static async Task RecordCanonicalRequestBindingAsync(OrchestratorDbContext db, int requestId,
        JobRunInfo run, DateTimeOffset acceptedAtUtc, CancellationToken ct)
    {
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Canonical request binding requires the accepted job owner transaction.");
        var query = db.Database.IsNpgsql()
            ? db.Requests.FromSqlInterpolated($"SELECT * FROM \"Requests\" WHERE \"Id\" = {requestId} FOR UPDATE")
            : db.Requests.Where(x => x.Id == requestId);
        var row = await query.SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The submitted request is unavailable.");
        var executionId = run.Id.ToString(CultureInfo.InvariantCulture);
        var submissionId = Identity($"request-submission|{row.TargetTenantId}|{requestId}");
        var reservationId = Identity($"request-reservation|{row.TargetTenantId}|{requestId}");
        var submission = db.OutboxMessages.Local.SingleOrDefault(x => x.Id == submissionId) ??
            await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == submissionId, ct);
        var reservation = db.OutboxMessages.Local.SingleOrDefault(x => x.Id == reservationId) ??
            await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reservationId, ct);
        if (!MatchesSubmission(row, submission) || !MatchesBinding(row, reservation, RequestReservationOutboxType))
            throw new InvalidOperationException("Native request acceptance requires its exact immutable submission and reservation.");
        if (row.ExecutionId != executionId)
            throw new InvalidOperationException("The submitted request already has a different accepted disposition.");
        if (FlowExecutionOutcomes.FromAuthoritativeStatus(row.Status) is not null)
        {
            var acceptedId = RequestBindingId(row.TargetTenantId!.Value, row.Id);
            var accepted = db.OutboxMessages.Local.SingleOrDefault(x => x.Id == acceptedId) ??
                await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == acceptedId, ct);
            if (!MatchesBinding(row, accepted))
                throw new InvalidOperationException("A rejected request cannot acquire an accepted execution on retry.");
        }
        var request = new RequestInfo(row.Id, row.SourceSystem, row.TargetClientIdentity, row.JobDefinitionId,
            row.ExecutionId, row.Status, row.ResultMessage, row.ResultData, row.JobInputs, row.Logs.AsReadOnly(),
            row.CreatedAtUtc, row.UpdatedAtUtc, row.TargetTenantId, row.TargetAgentId);
        await RecordAcceptedRequestJobBindingAsync(db, request, run, ct);
        if (row.ExecutionId != executionId || FlowExecutionOutcomes.FromAuthoritativeStatus(row.Status) is null &&
            row.Status != "Processing")
        {
            row.ExecutionId = executionId;
            if (FlowExecutionOutcomes.FromAuthoritativeStatus(row.Status) is null) row.Status = "Processing";
            row.UpdatedAtUtc = acceptedAtUtc;
        }
    }

    /// <summary>
    /// Projects terminal status only for normal requests with a server-owned accepted job binding.
    /// The job owner calls this at terminal-ready under the same transaction; UI claim IDs are insufficient.
    /// </summary>
    public static async Task ProjectAcceptedRequestTerminalsAsync(OrchestratorDbContext db, JobRunDetails details,
        string resultHash, IRequestService requests, CancellationToken ct)
    {
        if (details.Run.TenantId is not > 0 || FlowExecutionOutcomes.FromAuthoritativeStatus(details.Run.Status.ToString()) is null)
            return;
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is null ||
            resultHash != JobRunProjectionHash.Compute(details))
            throw new InvalidOperationException("Request terminal projection requires the accepted job owner result.");
        var runId = details.Run.Id.ToString(CultureInfo.InvariantCulture);
        var tenant = details.Run.TenantId.Value.ToString(CultureInfo.InvariantCulture);
        var correlation = "job-run:" + runId;
        var bindings = await db.OutboxMessages.AsNoTracking().Where(x => x.Type == RequestBindingOutboxType &&
            x.TenantId == tenant && x.CorrelationId == correlation).Select(x => x.PayloadJson).ToArrayAsync(ct);
        var status = details.Run.Status switch
        {
            JobRunState.Succeeded => "Completed", JobRunState.TimedOut => "TimedOut", JobRunState.Cancelled => "Cancelled",
            JobRunState.Unknown => "Unknown", _ => "Failed"
        };
        foreach (var json in bindings)
        {
            var binding = JsonSerializer.Deserialize<AcceptedRequestJobBinding>(json);
            if (binding is null || binding.ExecutionId != runId || binding.TenantId != details.Run.TenantId) continue;
            var query = db.Database.IsNpgsql()
                ? db.Requests.FromSqlInterpolated($"SELECT * FROM \"Requests\" WHERE \"Id\" = {binding.RequestId} FOR UPDATE")
                : db.Requests.Where(x => x.Id == binding.RequestId);
            var row = await query.AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is null || row.ExecutionId != binding.ExecutionId || PositiveDecimal(row.JobDefinitionId) != PositiveDecimal(binding.JobDefinitionId) ||
                row.TargetTenantId != binding.TenantId || row.TargetAgentId != binding.AgentId ||
                row.SourceSystem != binding.SourceSystem) continue;
            if (FlowExecutionOutcomes.FromAuthoritativeStatus(row.Status) is not null)
            {
                if (details.Run.Status == JobRunState.TimedOut &&
                    FlowExecutionOutcomes.FromAuthoritativeStatus(row.Status) == FlowExecutionOutcome.TimedOut)
                    await RecordRequestAsync(db, row, string.Empty, details.Run.CompletedAtUtc ?? DateTimeOffset.UtcNow, ct);
                continue;
            }
            // Only a bounded reference is copied; the actual immutable terminal body remains in its canonical result.
            var result = JsonSerializer.Serialize(new { jobRunId = runId, status, resultHash });
            await requests.UpdateAsync(new(binding.RequestId, null, null, null, null, status,
                "Job execution " + status + ".", result, null, null), ct);
        }
    }

    internal static void CaptureTracked(OrchestratorDbContext db)
    {
        PreserveFacts(db);
        foreach (var candidate in Candidates(db))
        {
            var value = candidate.Event;
            if (candidate.Request is { } request)
            {
                value = RequestTarget(value, RequestTargets(db, value).SingleOrDefault());
                if (HasSubmission(db, request)) value = value with { DefinitionSourceKind = FlowExecutionSourceKind.Job };
                if (LinkedJob(db, request) is { } run)
                {
                    value = CorrelateJob(value, run);
                    if (FlowExecutionOutcomes.FromAuthoritativeStatus(value.RawStatus) == FlowExecutionOutcome.TimedOut)
                        value = RequestTimeoutOutcome(value, run, Control(db, run.Id));
                }
            }
            value = ApplyAdmission(value, Admission(db, value));
            value = ApplyNativeScriptReplay(db, value);
            if (!db.OutboxMessages.Local.Any(x => x.Id == value.EventId) &&
                db.OutboxMessages.AsNoTracking().Any(x => x.Id == value.EventId)) continue;
            Append(db, value);
        }
    }

    internal static async Task CaptureTrackedAsync(OrchestratorDbContext db, CancellationToken ct)
    {
        PreserveFacts(db);
        foreach (var candidate in Candidates(db))
        {
            var value = candidate.Event;
            if (candidate.Request is { } request)
            {
                value = RequestTarget(value, await RequestTargets(db, value).SingleOrDefaultAsync(ct));
                if (await HasTrustedSubmissionAsync(db, request, ct)) value = value with { DefinitionSourceKind = FlowExecutionSourceKind.Job };
                if (await LinkedJobAsync(db, request, ct) is { } run)
                {
                    value = CorrelateJob(value, run);
                    if (FlowExecutionOutcomes.FromAuthoritativeStatus(value.RawStatus) == FlowExecutionOutcome.TimedOut)
                        value = RequestTimeoutOutcome(value, run, await ControlAsync(db, run.Id, ct));
                }
            }
            value = ApplyAdmission(value, await AdmissionAsync(db, value, ct));
            value = await ApplyNativeScriptReplayAsync(db, value, ct);
            if (!db.OutboxMessages.Local.Any(x => x.Id == value.EventId) &&
                await db.OutboxMessages.AsNoTracking().AnyAsync(x => x.Id == value.EventId, ct)) continue;
            Append(db, value);
        }
    }

    /// <summary>For relational ExecuteUpdate writers, call under the same request lock/transaction before commit.</summary>
    public static async Task RecordRequestAsync(OrchestratorDbContext db, RequestRecord row, string previousStatus,
        DateTimeOffset observedAtUtc, CancellationToken ct)
    {
        var value = RequestEvent(row, previousStatus, observedAtUtc);
        if (value is null) return;
        value = RequestTarget(value, await RequestTargets(db, value).SingleOrDefaultAsync(ct));
        if (await HasTrustedSubmissionAsync(db, row, ct)) value = value with { DefinitionSourceKind = FlowExecutionSourceKind.Job };
        if (await LinkedJobAsync(db, row, ct) is { } run)
        {
            value = CorrelateJob(value, run);
            if (FlowExecutionOutcomes.FromAuthoritativeStatus(value.RawStatus) == FlowExecutionOutcome.TimedOut)
                value = RequestTimeoutOutcome(value, run, await ControlAsync(db, run.Id, ct));
        }
        value = ApplyAdmission(value, await AdmissionAsync(db, value, ct));
        if (db.OutboxMessages.Local.Any(x => x.Id == value.EventId) ||
            await db.OutboxMessages.AsNoTracking().AnyAsync(x => x.Id == value.EventId, ct)) return;
        Append(db, value);
    }

    /// <summary>
    /// Call only after the final accepted terminal body/hash is complete, before the control record save.
    /// The caller's existing job owner transaction commits the terminal-ready marker and event together.
    /// </summary>
    public static async Task RecordJobAsync(OrchestratorDbContext db, JobRunDetails details, string resultHash,
        DateTimeOffset terminalReadyAtUtc, CancellationToken ct, PinnedJobExecutionSnapshot? pinnedSnapshot = null)
    {
        if (details.Run.TenantId is not > 0) return;
        var outcome = FlowExecutionOutcomes.FromAuthoritativeStatus(details.Run.Status.ToString());
        if (outcome is null || resultHash != JobRunProjectionHash.Compute(details))
            throw new InvalidOperationException("A lifecycle event requires the accepted complete terminal result.");
        var row = await db.JobRuns.AsNoTracking().SingleAsync(x => x.Id == checked((long)details.Run.Id), ct);
        var metadata = JobRunInputProtection.Metadata(row);
        var id = Decimal(row.Id);
        var occurred = terminalReadyAtUtc;
        var replayHash = TrustedReplayHash(row, metadata);
        if (pinnedSnapshot is not null)
        {
            pinnedSnapshot.Validate(row.TenantId!.Value);
            if (pinnedSnapshot.Definition.Job.Id != details.Run.JobId ||
                pinnedSnapshot.Definition.Job.AgentId != row.AgentId ||
                !string.Equals(pinnedSnapshot.ComputeDigest(), metadata.DefinitionRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("The lifecycle replay descriptor differs from the accepted original job.");
            replayHash = pinnedSnapshot.ComputeDigest();
        }
        var definition = new FlowExecutionDefinitionDto(Decimal(row.JobId), Bound(metadata.DefinitionName, 512),
            metadata.DefinitionRevision, replayHash);
        var target = new FlowExecutionTargetDto(row.AgentId, Bound(row.TargetDisplayName ?? row.TargetName, 512),
            Bound(row.TargetHostName, 512));
        var value = Create(row.TenantId!.Value, FlowExecutionSourceKind.Job, definition, id, id, target,
            outcome.Value, details.Run.Status.ToString(), occurred, terminalReadyAtUtc, details.Run.CompletedAtUtc ?? terminalReadyAtUtc) with
        {
            StartedAtUtc = details.Run.StartedAtUtc,
            CompletedAtUtc = details.Run.CompletedAtUtc,
            CorrelationId = "job-run:" + id,
            RootExecutionId = id,
            ReasonCode = "job_" + outcome.Value.ToString().ToLowerInvariant(),
            Reason = Bound(details.Run.Error, 512),
            Results = [new("job-result", id, resultHash, Available: true)],
            Replay = JobReplay(row, definition, target),
            Provenance = "job-terminal-ready"
        };
        value = ApplyAdmission(value, await AdmissionAsync(db, value, ct));
        if (db.OutboxMessages.Local.Any(x => x.Id == value.EventId) ||
            await db.OutboxMessages.AsNoTracking().AnyAsync(x => x.Id == value.EventId, ct)) return;
        Append(db, value);
    }

    public static FlowExecutionEventDto? RequestEvent(RequestRecord row, string? previousStatus, DateTimeOffset observedAtUtc)
    {
        if (row.TargetTenantId is not > 0 || row.Id <= 0 ||
            string.Equals(row.Status, previousStatus, StringComparison.OrdinalIgnoreCase) ||
            FlowExecutionOutcomes.FromAuthoritativeStatus(row.Status) is not { } outcome) return null;
        var unprovedTimeout = outcome == FlowExecutionOutcome.TimedOut;
        if (unprovedTimeout) outcome = FlowExecutionOutcome.Unknown;
        var id = row.Id.ToString(CultureInfo.InvariantCulture);
        // Browser claim/edit identifiers are descriptive until an immutable server-owned binding proves them.
        var attempt = id;
        var metadata = JobRunInputProtection.Metadata(new() { InputSensitivityJson = row.InputSensitivityJson });
        var definition = new FlowExecutionDefinitionDto(PositiveDecimal(row.JobDefinitionId), Bound(metadata.DefinitionName, 512),
            metadata.DefinitionRevision);
        var target = new FlowExecutionTargetDto(row.TargetAgentId);
        return Create(row.TargetTenantId.Value, FlowExecutionSourceKind.Request, definition, id, attempt, target,
            outcome, row.Status.Trim(), observedAtUtc, observedAtUtc, row.UpdatedAtUtc) with
        {
            StartedAtUtc = row.CreatedAtUtc <= row.UpdatedAtUtc ? row.CreatedAtUtc : null,
            CompletedAtUtc = row.UpdatedAtUtc,
            RequestKind = OptionalIdentifier(row.SourceSystem),
            CorrelationId = "request:" + id,
            ReasonCode = unprovedTimeout ? "request_timeout_evidence_unavailable" : "request_" + outcome.ToString().ToLowerInvariant(),
            Reason = Bound(row.ResultMessage, 512),
            Results = [new("request-result", id, Digest(row.ResultData), row.ResultData is not null)],
            Replay = new(FlowExecutionSourceKind.Request, definition, target, "request:" + id, id, attempt,
                false, "request_replay_binding_unavailable"),
            Provenance = "request-state-commit"
        };
    }

    public static FlowExecutionEventDto? ScriptEvent(JobTaskActivityRecord row, string? previousStatus, DateTimeOffset observedAtUtc,
        bool terminalEvidenceChanged = false)
    {
        if (row.TenantId is not > 0 || string.IsNullOrWhiteSpace(row.RequestId) ||
            row.TaskType is not (TaskKinds.ExecLibraryScript or TaskKinds.Legacy_RunLibraryScript) ||
            string.Equals(row.Status, previousStatus, StringComparison.OrdinalIgnoreCase) && !terminalEvidenceChanged ||
            FlowScriptExecutionOutcomes.ClassifyCommittedResult(row.Status, row.ExitCode, row.ResultJson) is not { } terminal) return null;
        var outcome = terminal.Outcome;
        var policy = TaskOutputProtection.Metadata(row);
        var definition = new FlowExecutionDefinitionDto(policy?.ScriptId?.ToString(CultureInfo.InvariantCulture),
            Bound(row.ScriptName, 512), policy?.ScriptRevision?.ToString(CultureInfo.InvariantCulture), policy?.ScriptContentHash);
        var target = new FlowExecutionTargetDto(row.AgentId, Bound(row.TargetDisplayName ?? row.TargetName, 512),
            Bound(row.TargetHostName, 512));
        var result = TaskOutputProtection.Project(row, row.ResultJson);
        var id = row.RequestId;
        var parent = row.JobRunId?.ToString(CultureInfo.InvariantCulture);
        var accepted = Create(row.TenantId.Value, FlowExecutionSourceKind.Script, definition, id, id, target,
            outcome, row.Status.Trim(), observedAtUtc, observedAtUtc, row.CompletedAtUtc ?? row.CreatedAtUtc);
        return accepted with
        {
            EventId = Identity($"script-evidence|{accepted.EventId:N}|{terminal.ExitCode?.ToString(CultureInfo.InvariantCulture)}|{Digest(result)}"),
            StartedAtUtc = row.StartedAtUtc,
            CompletedAtUtc = row.CompletedAtUtc,
            ParentExecutionId = parent,
            RootExecutionId = parent ?? id,
            CorrelationId = parent is null ? BoundedCorrelation("script-execution:", id) : "job-run:" + parent,
            ExitCode = terminal.ExitCode,
            ReasonCode = "script_" + outcome.ToString().ToLowerInvariant(),
            Reason = Bound(TaskOutputProtection.Project(row, row.Error, TaskOutputRedactor.ReadBinding(row.ResultJson), envelope: false), 512),
            Results = [new("script-result", id, Digest(result), result is not null)],
            Replay = new(FlowExecutionSourceKind.Script, definition, target, "script-execution:" + id, id, id,
                false, "canonical_replay_admission_required"),
            Provenance = "script-terminal-commit"
        };
    }

    // Candidate capture and transaction ownership use the same native transition predicate.
    internal static bool HasScriptTransition(EntityEntry<JobTaskActivityRecord> entry) =>
        entry.State == EntityState.Added || entry.State == EntityState.Modified &&
        (!string.Equals(entry.Property(row => row.Status).OriginalValue, entry.Entity.Status, StringComparison.OrdinalIgnoreCase) ||
         ScriptTerminalEvidenceChanged(entry));

    private static bool ScriptTerminalEvidenceChanged(EntityEntry<JobTaskActivityRecord> entry) =>
        entry.State == EntityState.Modified &&
        (entry.Property(row => row.ExitCode).OriginalValue != entry.Entity.ExitCode ||
         !string.Equals(entry.Property(row => row.ResultJson).OriginalValue, entry.Entity.ResultJson, StringComparison.Ordinal) ||
         entry.Property(row => row.CompletedAtUtc).OriginalValue != entry.Entity.CompletedAtUtc);

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
        foreach (var entry in db.ChangeTracker.Entries<JobTaskActivityRecord>().Where(HasScriptTransition))
        {
            var previous = entry.State == EntityState.Added ? null : entry.Property(x => x.Status).OriginalValue;
            if (ScriptEvent(entry.Entity, previous, observed, ScriptTerminalEvidenceChanged(entry)) is { } value)
                candidates.Add((value, null));
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

    private static JobRunRecord? LinkedJob(OrchestratorDbContext db, RequestRecord row) =>
        HasTrustedBinding(db, row) ? LinkedJobs(db, row).SingleOrDefault() : null;

    private static async Task<JobRunRecord?> LinkedJobAsync(OrchestratorDbContext db, RequestRecord row, CancellationToken ct) =>
        await HasTrustedBindingAsync(db, row, ct) ? await LinkedJobs(db, row).SingleOrDefaultAsync(ct) : null;

    private static Guid RequestBindingId(int tenantId, int requestId) => Identity($"request-binding|{tenantId}|{requestId}");

    private static bool HasTrustedBinding(OrchestratorDbContext db, RequestRecord row)
    {
        var managed = db.Set<ManagedOrchestrationRequestBinding>().Local.SingleOrDefault(x => x.RequestId == row.Id) ??
            db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleOrDefault(x => x.RequestId == row.Id);
        if (MatchesManaged(row, managed)) return true;
        var id = RequestBindingId(row.TargetTenantId ?? 0, row.Id);
        var binding = db.OutboxMessages.Local.SingleOrDefault(x => x.Id == id) ??
            db.OutboxMessages.AsNoTracking().SingleOrDefault(x => x.Id == id);
        return MatchesBinding(row, binding);
    }

    private static async Task<bool> HasTrustedBindingAsync(OrchestratorDbContext db, RequestRecord row, CancellationToken ct)
    {
        var managed = db.Set<ManagedOrchestrationRequestBinding>().Local.SingleOrDefault(x => x.RequestId == row.Id) ??
            await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == row.Id, ct);
        if (MatchesManaged(row, managed)) return true;
        var id = RequestBindingId(row.TargetTenantId ?? 0, row.Id);
        var binding = db.OutboxMessages.Local.SingleOrDefault(x => x.Id == id) ??
            await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return MatchesBinding(row, binding);
    }

    private static bool MatchesManaged(RequestRecord row, ManagedOrchestrationRequestBinding? binding) =>
        binding is not null && binding.TenantId == row.TargetTenantId && binding.AgentId == row.TargetAgentId &&
        PositiveDecimal(binding.JobDefinitionId) == PositiveDecimal(row.JobDefinitionId) && binding.ExecutionId == row.ExecutionId &&
        binding.ExecutionId is not null && binding.SourceSystem == row.SourceSystem;

    private static bool IsManagedSubmission(RequestRecord row, ManagedOrchestrationRequestBinding? binding) =>
        binding is not null && binding.TenantId == row.TargetTenantId && binding.AgentId == row.TargetAgentId &&
        PositiveDecimal(binding.JobDefinitionId) == PositiveDecimal(row.JobDefinitionId) && binding.SourceSystem == row.SourceSystem;

    private static bool HasSubmission(OrchestratorDbContext db, RequestRecord row)
    {
        var managed = db.Set<ManagedOrchestrationRequestBinding>().Local.SingleOrDefault(x => x.RequestId == row.Id) ??
            db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleOrDefault(x => x.RequestId == row.Id);
        if (IsManagedSubmission(row, managed)) return true;
        var id = Identity($"request-submission|{row.TargetTenantId}|{row.Id}");
        return MatchesSubmission(row, db.OutboxMessages.Local.SingleOrDefault(x => x.Id == id) ??
            db.OutboxMessages.AsNoTracking().SingleOrDefault(x => x.Id == id));
    }

    internal static async Task<bool> HasTrustedSubmissionAsync(OrchestratorDbContext db, RequestRecord row, CancellationToken ct)
    {
        var managed = db.Set<ManagedOrchestrationRequestBinding>().Local.SingleOrDefault(x => x.RequestId == row.Id) ??
            await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == row.Id, ct);
        if (IsManagedSubmission(row, managed)) return true;
        return await HasImmutableSubmittedRequestAsync(db, row, ct);
    }

    internal static async Task<bool> HasImmutableSubmittedRequestAsync(OrchestratorDbContext db, RequestRecord row, CancellationToken ct)
    {
        var id = Identity($"request-submission|{row.TargetTenantId}|{row.Id}");
        return MatchesSubmission(row, db.OutboxMessages.Local.SingleOrDefault(x => x.Id == id) ??
            await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct));
    }

    private static bool MatchesSubmission(RequestRecord row, OutboxMessage? ledger)
    {
        if (ledger?.Type != RequestSubmissionOutboxType || Encoding.UTF8.GetByteCount(ledger.PayloadJson) > 2048) return false;
        try
        {
            var binding = JsonSerializer.Deserialize<AcceptedRequestJobBinding>(ledger.PayloadJson);
            return binding is not null && binding.RequestId == row.Id && binding.TenantId == row.TargetTenantId &&
                binding.AgentId == row.TargetAgentId && PositiveDecimal(binding.JobDefinitionId) == PositiveDecimal(row.JobDefinitionId) &&
                binding.ExecutionId == string.Empty && binding.SourceSystem == row.SourceSystem &&
                ledger.TenantId == binding.TenantId.ToString(CultureInfo.InvariantCulture);
        }
        catch (JsonException) { return false; }
    }

    private static bool MatchesBinding(RequestRecord row, OutboxMessage? ledger, string expectedType = RequestBindingOutboxType)
    {
        if (ledger?.Type != expectedType || Encoding.UTF8.GetByteCount(ledger.PayloadJson) > 2048) return false;
        try
        {
            var binding = JsonSerializer.Deserialize<AcceptedRequestJobBinding>(ledger.PayloadJson);
            return binding is not null && binding.RequestId == row.Id && binding.TenantId == row.TargetTenantId &&
                binding.AgentId == row.TargetAgentId && PositiveDecimal(binding.JobDefinitionId) == PositiveDecimal(row.JobDefinitionId) &&
                binding.ExecutionId == row.ExecutionId && binding.SourceSystem == row.SourceSystem &&
                ledger.TenantId == binding.TenantId.ToString(CultureInfo.InvariantCulture);
        }
        catch (JsonException) { return false; }
    }

    private static JobRunControlRecord? Control(OrchestratorDbContext db, long runId) =>
        db.Set<JobRunControlRecord>().Local.SingleOrDefault(x => x.RunId == runId) ??
        db.Set<JobRunControlRecord>().AsNoTracking().SingleOrDefault(x => x.RunId == runId);

    private static Task<JobRunControlRecord?> ControlAsync(OrchestratorDbContext db, long runId, CancellationToken ct) =>
        db.Set<JobRunControlRecord>().Local.SingleOrDefault(x => x.RunId == runId) is { } local
            ? Task.FromResult<JobRunControlRecord?>(local)
            : db.Set<JobRunControlRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.RunId == runId, ct);

    private static FlowExecutionEventDto RequestTimeoutOutcome(FlowExecutionEventDto value, JobRunRecord run,
        JobRunControlRecord? control)
    {
        if (FlowExecutionOutcomes.FromAuthoritativeStatus(value.RawStatus) != FlowExecutionOutcome.TimedOut ||
            run.Status != (int)JobRunState.TimedOut || control?.RunId != run.Id ||
            control.TerminalReadyAtUtc is null || control.TerminalResultHash is not { Length: 64 } hash ||
            !hash.All(char.IsAsciiHexDigit)) return value;
        // Mutable request labels do not establish deadlines. The accepted native result does.
        // Its immutable hash gives duplicate/backfill delivery a stable identity even if the raw
        // request had already claimed timeout before its real parent result became ready.
        return value with
        {
            EventId = Identity($"event|{value.OccurrenceId:N}|{(int)FlowExecutionOutcome.TimedOut}|{value.RawStatus.ToUpperInvariant()}|job-result|{hash}"),
            Outcome = FlowExecutionOutcome.TimedOut,
            ReasonCode = "request_timedout",
            Reason = Bound(run.Error, 512),
            Results = [.. (value.Results ?? []), new("job-result", Decimal(run.Id), hash, Available: true)]
        };
    }

    private sealed record RequestDirectoryTarget(string? Name, string? FriendlyName, string? DeviceInfoJson);

    private static IQueryable<RequestDirectoryTarget> RequestTargets(OrchestratorDbContext db, FlowExecutionEventDto value) =>
        db.Agents.AsNoTracking().Where(agent => agent.TenantId == value.TenantId && agent.Id == value.Target.AgentId)
            .Select(agent => new RequestDirectoryTarget(agent.Name, agent.FriendlyName, agent.DeviceInfoJson));

    private static FlowExecutionEventDto RequestTarget(FlowExecutionEventDto value, RequestDirectoryTarget? directory)
    {
        if (directory is null) return value;
        var device = ClientDirectoryIdentity.Parse(directory.DeviceInfoJson);
        var name = new[] { directory.FriendlyName, directory.Name, device.HostName }
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))?.Trim();
        var target = value.Target with { DisplayName = Bound(name, 512), HostName = Bound(device.HostName, 512) };
        return value with { Target = target, Replay = value.Replay is not { } replay ? null : replay with { Target = target } };
    }

    private static FlowExecutionEventDto CorrelateJob(FlowExecutionEventDto value, JobRunRecord run)
    {
        var id = Decimal(run.Id);
        var metadata = JobRunInputProtection.Metadata(run);
        var definition = new FlowExecutionDefinitionDto(Decimal(run.JobId), Bound(metadata.DefinitionName, 512),
            metadata.DefinitionRevision, TrustedReplayHash(run, metadata));
        var target = new FlowExecutionTargetDto(value.Target.AgentId,
            Bound(run.TargetDisplayName ?? run.TargetName ?? value.Target.DisplayName, 512),
            Bound(run.TargetHostName ?? value.Target.HostName, 512));
        return value with
        {
            CorrelationId = "job-run:" + id, ParentExecutionId = id, RootExecutionId = id, AttemptId = id,
            Definition = definition, DefinitionSourceKind = FlowExecutionSourceKind.Job, Target = target, Replay = JobReplay(run, definition, target)
        };
    }

    private static string? TrustedReplayHash(JobRunRecord row, JobRunInputProtection.Sensitivity metadata) =>
        row.ProtectedExecutionJson is not null && metadata.PinnedReplayDefinitionHash is { Length: 64 } hash &&
            hash.All(char.IsAsciiHexDigit) && string.Equals(hash, metadata.DefinitionRevision, StringComparison.Ordinal)
                ? hash : null;

    private static FlowExecutionReplayDescriptorDto JobReplay(JobRunRecord row,
        FlowExecutionDefinitionDto definition, FlowExecutionTargetDto target)
    {
        var id = Decimal(row.Id);
        var supported = definition.ContentHash is { Length: 64 } && target.AgentId is not null && row.ProtectedExecutionJson is not null;
        return new(FlowExecutionSourceKind.Job, definition, target, "job-run:" + id, id, id, supported,
            supported ? null : "canonical_replay_material_unavailable");
    }

    private static FlowExecutionEventDto ApplyNativeScriptReplay(OrchestratorDbContext db, FlowExecutionEventDto value)
    {
        if (value.SourceKind != FlowExecutionSourceKind.Script) return value;
        var pinned = db.Set<ScriptExecutionReplayRecord>().Local.SingleOrDefault(row =>
            row.RequestId == value.ExecutionId && row.TenantId == value.TenantId) ??
            db.Set<ScriptExecutionReplayRecord>().AsNoTracking().SingleOrDefault(row =>
                row.RequestId == value.ExecutionId && row.TenantId == value.TenantId);
        return ApplyNativeScriptReplay(value, pinned);
    }

    private static async Task<FlowExecutionEventDto> ApplyNativeScriptReplayAsync(OrchestratorDbContext db,
        FlowExecutionEventDto value, CancellationToken ct)
    {
        if (value.SourceKind != FlowExecutionSourceKind.Script) return value;
        var pinned = db.Set<ScriptExecutionReplayRecord>().Local.SingleOrDefault(row =>
            row.RequestId == value.ExecutionId && row.TenantId == value.TenantId) ??
            await db.Set<ScriptExecutionReplayRecord>().AsNoTracking().SingleOrDefaultAsync(row =>
                row.RequestId == value.ExecutionId && row.TenantId == value.TenantId, ct);
        return ApplyNativeScriptReplay(value, pinned);
    }

    private static FlowExecutionEventDto ApplyNativeScriptReplay(FlowExecutionEventDto value,
        ScriptExecutionReplayRecord? pinned)
    {
        // Only the canonical activity's exact protected script payload can advertise script replay.
        // A child task never substitutes its parent job. Admission independently decrypts and
        // verifies this material and current authority before creating another native execution.
        if (pinned is null || value.SourceKind != FlowExecutionSourceKind.Script ||
            pinned.RequestId != value.ExecutionId || pinned.TenantId != value.TenantId ||
            pinned.AgentId == Guid.Empty || pinned.AgentId != value.Target.AgentId ||
            pinned.ScriptId <= 0 || pinned.ScriptRevision <= 0 ||
            value.Definition.Id != pinned.ScriptId.ToString(CultureInfo.InvariantCulture) ||
            value.Definition.Revision != pinned.ScriptRevision.ToString(CultureInfo.InvariantCulture) ||
            !IsReplayDigest(pinned.ScriptContentHash) || !IsReplayDigest(pinned.PayloadHash) ||
            !string.Equals(value.Definition.ContentHash, pinned.ScriptContentHash, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(pinned.ProtectedSnapshotJson)) return value;
        // Retain the validated native digest spelling so replay and terminal lineage stay exact.
        var definition = new FlowExecutionDefinitionDto(pinned.ScriptId.ToString(CultureInfo.InvariantCulture),
            Bound(pinned.ScriptName, 512), pinned.ScriptRevision.ToString(CultureInfo.InvariantCulture), value.Definition.ContentHash);
        return value with
        {
            Definition = definition,
            Replay = new(FlowExecutionSourceKind.Script, definition, value.Target, "script-execution:" + value.ExecutionId,
                value.ExecutionId, value.AttemptId, true)
        };
    }

    private static bool IsReplayDigest(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);

    private static IQueryable<ExecutionAdmissionRecord> Admissions(OrchestratorDbContext db, FlowExecutionEventDto value)
    {
        var kind = value.SourceKind == FlowExecutionSourceKind.Script && value.ParentExecutionId is null
            ? (int)ExecutionAdmissionKind.LibraryScript : (int)ExecutionAdmissionKind.Job;
        var id = value.SourceKind == FlowExecutionSourceKind.Job || kind == (int)ExecutionAdmissionKind.LibraryScript
            ? value.ExecutionId : value.ParentExecutionId;
        return db.Set<ExecutionAdmissionRecord>().AsNoTracking().Where(x =>
            x.TenantId == value.TenantId && x.Kind == kind && x.ExecutionId == id);
    }

    private static ExecutionAdmissionRecord? Admission(OrchestratorDbContext db, FlowExecutionEventDto value) =>
        Admissions(db, value).SingleOrDefault();
    private static Task<ExecutionAdmissionRecord?> AdmissionAsync(OrchestratorDbContext db, FlowExecutionEventDto value, CancellationToken ct) =>
        Admissions(db, value).SingleOrDefaultAsync(ct);

    private static FlowExecutionEventDto ApplyAdmission(FlowExecutionEventDto value, ExecutionAdmissionRecord? admission)
    {
        if (admission is null) return value;
        ExecutionAdmissionCausation? cause = null;
        if (admission.CausationJson is { } json)
        {
            if (Encoding.UTF8.GetByteCount(json) > 8192)
                throw new InvalidOperationException("Accepted execution causation exceeds its metadata budget.");
            cause = JsonSerializer.Deserialize<ExecutionAdmissionCausation>(json)
                ?? throw new InvalidOperationException("Accepted execution causation is unavailable.");
        }
        var denied = value.Outcome == FlowExecutionOutcome.Failed && admission.AuthorityDeniedAtUtc is not null;
        var linkedRerun = !string.IsNullOrWhiteSpace(cause?.SourceExecutionId) &&
            (value.SourceKind == FlowExecutionSourceKind.Job ||
             value.SourceKind == FlowExecutionSourceKind.Script && value.ParentExecutionId is null);
        var replay = value.Replay;
        if (admission.Kind == (int)ExecutionAdmissionKind.LibraryScript && value.SourceKind == FlowExecutionSourceKind.Script &&
            value.Definition.Id is not null && value.Definition.Revision is not null &&
            value.Definition.ContentHash is { Length: 64 } && value.Target.AgentId is not null &&
            !string.IsNullOrEmpty(admission.ProtectedPayloadJson))
            replay = new(FlowExecutionSourceKind.Script, value.Definition, value.Target, "script-execution:" + value.ExecutionId,
                value.ExecutionId, value.AttemptId, true);
        return value with
        {
            Outcome = denied ? FlowExecutionOutcome.Denied : value.Outcome,
            ReasonCode = denied ? "execution_authority_denied" : value.ReasonCode,
            ParentExecutionId = linkedRerun ? cause!.SourceExecutionId : value.ParentExecutionId,
            RootExecutionId = linkedRerun ? cause!.RootExecutionId ?? cause.SourceExecutionId : value.RootExecutionId,
            Causation = new(cause?.RootOccurrenceId ?? value.OccurrenceId.ToString("N"), cause?.ParentEventId,
                admission.FlowRunId, admission.NodeId, cause?.HopCount ?? 0, cause?.RecoveryAttempt ?? 0),
            Replay = replay
        };
    }

    private static FlowExecutionEventDto Create(int tenantId, FlowExecutionSourceKind source,
        FlowExecutionDefinitionDto definition, string executionId, string attemptId, FlowExecutionTargetDto target,
        FlowExecutionOutcome outcome, string rawStatus, DateTimeOffset occurred, DateTimeOffset observed, DateTimeOffset? transitionIdentityAtUtc = null)
    {
        var occurrence = Identity($"occurrence|{tenantId}|{(int)source}|{executionId}|{attemptId}");
        var eventId = Identity($"event|{occurrence:N}|{(int)outcome}|{rawStatus.ToUpperInvariant()}|{(transitionIdentityAtUtc ?? occurred).UtcTicks}");
        return new(eventId, occurrence, tenantId, source, definition, executionId, attemptId, target, outcome,
            rawStatus, occurred, observed) { Causation = new(occurrence.ToString("N")) };
    }

    private static void Append(OrchestratorDbContext db, FlowExecutionEventDto value)
    {
        value = FitSourcePresentation(value);
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

    // Source identity and replay references are lossless. Presentation has a smaller combined
    // budget because default JSON escaping can expand individually valid Unicode fields.
    // Reserve room for the transaction's final timestamp stamp before its payload hash is frozen.
    private static FlowExecutionEventDto FitSourcePresentation(FlowExecutionEventDto value)
    {
        const int byteBudget = FlowExecutionEventLimits.MaximumEnvelopeBytes - 256;
        if (Bytes(value) <= byteBudget) return value;
        FlowExecutionEventDto fitted = value;
        foreach (var limit in new[] { 256, 128, 64, 32, 16, 0 })
        {
            fitted = value with
            {
                Definition = DefinitionPresentation(value.Definition, limit),
                Target = TargetPresentation(value.Target, limit),
                Reason = (Shorten(value.Reason, limit) ?? string.Empty) +
                    " [Presentation truncated; see the canonical execution.]",
                Replay = value.Replay is not { } replay ? null : replay with
                {
                    Definition = DefinitionPresentation(replay.Definition, limit),
                    Target = TargetPresentation(replay.Target, limit)
                }
            };
            if (Bytes(fitted) <= byteBudget) return fitted;
        }
        return fitted;

        static int Bytes(FlowExecutionEventDto source) =>
            JsonSerializer.SerializeToUtf8Bytes(source, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto).Length;
        static FlowExecutionDefinitionDto DefinitionPresentation(FlowExecutionDefinitionDto definition, int limit) =>
            definition with { Name = Shorten(definition.Name, limit) };
        static FlowExecutionTargetDto TargetPresentation(FlowExecutionTargetDto target, int limit) =>
            target with { DisplayName = Shorten(target.DisplayName, limit), HostName = Shorten(target.HostName, limit) };
    }

    private static string? Shorten(string? value, int limit) => value is null || value.Length <= limit
        ? value : limit == 0 ? null : Bound(value, limit - 1) + "…";

    private static void PreserveFacts(OrchestratorDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<OutboxMessage>().Where(x => x.State is EntityState.Modified or EntityState.Deleted))
        {
            if (!entry.Property(x => x.Type).OriginalValue.StartsWith(EventTypePrefix, StringComparison.Ordinal)) continue;
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
    // Historical source-system labels can exceed the optional filter contract. Keep the native
    // record intact and omit an unavailable filter value rather than truncate it into another kind.
    private static string? OptionalIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) &&
        FlowGraphValidator.IsBoundedText(value, FlowExecutionEventLimits.MaximumIdentifierLength) &&
        !value.Any(char.IsControl) ? value : null;

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
