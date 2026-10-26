using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Security;
using System.Text.Json;

namespace NetRatel.Infrastructure.Services;

public sealed class JobRunService(OrchestratorDbContext db, IDataProtectionProvider protection) : IJobRunService
{
    private const int TaskActivityInsertAttempts = 4;
    private readonly OrchestratorDbContext _db = db;
    private readonly JobRunInputProtection _protection = new(protection);
    private readonly Dictionary<ulong, (ulong JobId, int? TenantId, JobRunInputProtection.Sensitivity Metadata)> _submissions = [];
    private readonly Dictionary<ulong, McpOperatorJobExecutionSnapshot> _mcpSnapshots = [];

    public void BindMcpExecutionSnapshot(ulong runId, McpOperatorJobExecutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_mcpSnapshots.TryAdd(runId, snapshot))
            throw new InvalidOperationException("A reviewed MCP run snapshot is immutable.");
        BindSubmittedDefinition(runId, snapshot.Definition);
    }

    public async Task<McpOperatorJobExecutionSnapshot?> GetMcpExecutionSnapshotAsync(ulong runId, CancellationToken ct = default)
    {
        var run = await _db.JobRuns.AsNoTracking().SingleOrDefaultAsync(row => row.Id == (long)runId, ct);
        return run?.ProtectedExecutionJson is null ? null : _protection.ForExecution(run).McpSnapshot;
    }

    public async Task<TaskOutputPolicy> GetRunOutputPolicyAsync(ulong runId, CancellationToken ct = default)
    {
        await ProtectLegacyAsync((long)runId, ct);
        var run = await _db.JobRuns.AsNoTracking().SingleAsync(x => x.Id == (long)runId, ct);
        var metadata = JobRunInputProtection.Metadata(run);
        return TaskOutputPolicy.Create(metadata.Parameters.Select(p => (p.Name, p.Type)), withholdOutput: metadata.Legacy);
    }

    public async Task<string?> ProtectTaskOutputAsync(string requestId, string? output, CancellationToken ct = default)
    {
        var task = await _db.JobTaskActivities.AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (task is null)
        {
            var command = await _db.McpOperatorCommands.AsNoTracking().SingleOrDefaultAsync(x => x.CommandId == requestId, ct);
            return command is null ? TaskOutputRedactor.WithholdEnvelope(output)
                : TaskOutputProtection.Project(command.OutputProtectionJson, output,
                    TaskOutputProtection.HasEnvironmentReferences(command.EnvironmentReferencesJson));
        }
        await ProtectLegacyTaskAsync(task, ct);
        var safe = TaskOutputProtection.Project(task, output);
        var run = await FindActivityRunAsync(requestId, ct);
        return run is null ? safe : _protection.Diagnostic(run, safe);
    }

    public void BindSubmittedDefinition(ulong runId, JobDefinitionDetails definition)
    {
        var job = new JobDefinition { Id = checked((long)definition.Job.Id), UpdatedAtUtc = definition.Job.UpdatedAtUtc };
        var parameters = definition.Params.Select(p => new JobParameterDefinition
            { Id = checked((long)p.Id), Name = p.Name, Type = p.Type, Required = p.Required });
        // Bind exactly once; later lifecycle calls cannot replace submission sensitivity.
        _submissions.TryAdd(runId, (definition.Job.Id, definition.Job.TenantId, JobRunInputProtection.Capture(job, parameters)));
    }

    public async Task<IReadOnlyList<JobRunInfo>> ListAsync(CancellationToken ct = default)
    {
        await ProtectLegacyAsync(null, ct);
        return (await _db.JobRuns.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct))
            .Select(Map).ToList();
    }

    public async Task<JobRunInfo?> GetAsync(ulong runId, CancellationToken ct = default)
    {
        await ProtectLegacyAsync((long)runId, ct);
        var row = await _db.JobRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == (long)runId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<string?> GetExecutionInputsAsync(ulong runId, CancellationToken ct = default)
    {
        await ProtectLegacyAsync((long)runId, ct);
        var row = await _db.JobRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == (long)runId, ct);
        return row is null ? null : _protection.ForExecution(row).InputsJson;
    }

    public async Task<string?> GetExecutionOptionsAsync(ulong runId, CancellationToken ct = default)
    {
        await ProtectLegacyAsync((long)runId, ct);
        var row = await _db.JobRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == (long)runId, ct);
        return row is null ? null : _protection.ForExecution(row).OptionsJson;
    }

    public async Task<JobRunDetails?> GetDetailsAsync(ulong runId, CancellationToken ct = default)
    {
        await ProtectLegacyAsync((long)runId, ct);
        var run = await _db.JobRuns.AsNoTracking().Include(x => x.Steps).Include(x => x.Activities)
            .FirstOrDefaultAsync(x => x.Id == (long)runId, ct);
        if (run is null) return null;
        return new JobRunDetails(Map(run),
            run.Steps.OrderBy(x => x.Ordinal).ThenBy(x => x.Id)
                .Select(x => Map(x) with { Error = _protection.Diagnostic(run, x.Error) }).ToList(),
            run.Activities.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id)
                .Select(x => SanitizeActivity(run, ProtectActivity(x))).ToList());
    }

    public async Task DeleteAsync(ulong runId, CancellationToken ct = default)
    {
        var run = await _db.JobRuns
            .Include(x => x.Steps)
            .Include(x => x.Activities)
                .ThenInclude(x => x.Logs)
            .FirstOrDefaultAsync(x => x.Id == (long)runId, ct);

        if (run is null)
        {
            return;
        }

        if (run.Activities.Count > 0)
        {
            var logs = run.Activities.SelectMany(x => x.Logs).ToList();
            if (logs.Count > 0)
            {
                _db.JobTaskLogs.RemoveRange(logs);
            }

            _db.JobTaskActivities.RemoveRange(run.Activities);
        }

        if (run.Steps.Count > 0)
        {
            _db.JobStepRuns.RemoveRange(run.Steps);
        }

        _db.JobRuns.Remove(run);
        await SaveAndNotifyAsync(ct);
    }

    public async Task<JobTaskActivityInfo?> GetActivityByRequestIdAsync(string requestId, CancellationToken ct = default)
    {
        var row = await _db.JobTaskActivities.AsNoTracking().Where(x => x.RequestId == requestId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        return row is null ? null : await SanitizeActivityAsync(row, ct);
    }

    public async Task<IReadOnlyList<JobTaskActivityInfo>> ListTaskActivitiesAsync(CancellationToken ct = default)
    {
        var result = new List<JobTaskActivityInfo>();
        foreach (var row in await _db.JobTaskActivities.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct))
            result.Add(await SanitizeActivityAsync(row, ct));
        return result;
    }

    public async Task<JobTaskActivityInfo?> GetActivityByIdAsync(ulong activityId, CancellationToken ct = default)
    {
        var row = await _db.JobTaskActivities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == (long)activityId, ct);
        return row is null ? null : await SanitizeActivityAsync(row, ct);
    }

    public async Task<IReadOnlyList<JobTaskLogInfo>> GetLogsByRequestIdAsync(string requestId, CancellationToken ct = default)
    {
        var activity = await _db.JobTaskActivities.FirstOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (activity is not null) await ProtectLegacyTaskAsync(activity, ct);
        var run = await FindActivityRunAsync(requestId, ct);
        if (run is not null && run.ProtectedExecutionJson is null)
        {
            await ProtectLegacyAsync(run.Id, ct);
            run = await FindActivityRunAsync(requestId, ct);
        }
        var logs = await _db.JobTaskLogs.AsNoTracking().Where(x => x.RequestId == requestId)
            .OrderBy(x => x.Sequence).ThenBy(x => x.Id).ToListAsync(ct);
        return logs.Select(x => Map(x) with
        {
            Message = ProjectLog(activity, run, x)
        }).ToList();
    }

    public async Task<JobRunInfo> UpsertRunAsync(UpsertJobRunCommand command, CancellationToken ct = default)
    {
        var row = await _db.JobRuns.FirstOrDefaultAsync(x => x.Id == (long)command.RunId, ct);
        var isNew = row is null;
        await using var transaction = isNew ? await JobDefinitionMutationFence.BeginAsync(_db, ct) : null;
        if (row is null)
        {
            // A captured definition does not permit starting a retired job.
            // Share retirement's row fence until the Pending intent is committed;
            // the actor's existing transaction ends before any gateway dispatch.
            if (!await JobDefinitionMutationFence.LockActiveAsync(_db, checked((long)command.JobId), ct))
                throw new JobDefinitionUnavailableException();
            row = new JobRunRecord { Id = (long)command.RunId };
            _db.JobRuns.Add(row);
        }

        if (!isNew && (row.JobId != (long)command.JobId || row.TenantId != command.TenantId))
            throw new InvalidOperationException("The submitted run identity cannot change.");
        var capture = row.ProtectedExecutionJson is null;
        var existingHistory = capture && !isNew;
        row.JobId = (long)command.JobId;
        row.TenantId = command.TenantId;
        row.AgentId = command.AgentId;
        row.Environment = command.Environment ?? row.Environment;
        row.TargetDisplayName = command.TargetDisplayName ?? row.TargetDisplayName;
        row.TargetHostName = command.TargetHostName ?? row.TargetHostName;
        row.TargetName = command.TargetName ?? row.TargetName;
        row.ClientIdentity = command.ClientIdentity.Trim();
        row.StartedBy = command.StartedBy.Trim();
        row.Status = (int)command.Status;
        row.CurrentStepOrdinal = command.CurrentStepOrdinal;
        row.CreatedAtUtc = command.CreatedAtUtc;
        if (command.StartedAtUtc.HasValue)
        {
            row.StartedAtUtc = command.StartedAtUtc;
        }
        if (command.CompletedAtUtc.HasValue)
        {
            row.CompletedAtUtc = command.CompletedAtUtc;
        }
        if (capture)
        {
            JobRunInputProtection.Sensitivity metadata;
            if (existingHistory) metadata = new("legacy-unknown-v1", null, []);
            else if (_submissions.TryGetValue(command.RunId, out var submitted))
            {
                if (submitted.JobId != command.JobId || submitted.TenantId != command.TenantId)
                    throw new InvalidOperationException("The run differs from its submitted definition binding.");
                metadata = submitted.Metadata;
            }
            else
            {
                // Non-actor ingress creates its durable intent here. One SQL snapshot owns
                // both the definition and parameter sensitivity at that submission boundary.
                var definition = await _db.Jobs.AsNoTracking().Include(x => x.Parameters).AsSingleQuery()
                    .SingleOrDefaultAsync(x => x.Id == row.JobId && x.TenantId == row.TenantId, ct);
                metadata = JobRunInputProtection.Capture(definition, definition?.Parameters ?? []);
            }
            _mcpSnapshots.TryGetValue(command.RunId, out var mcpSnapshot);
            if (mcpSnapshot is not null)
                metadata = metadata with { DefinitionRevision = mcpSnapshot.ComputeDigest() };
            _protection.Store(row, metadata,
                existingHistory ? row.InputsJson : command.InputsJson,
                existingHistory ? row.OptionsJson : command.OptionsJson, mcpSnapshot);
        }
        row.Error = _protection.Diagnostic(row, NormalizeNullable(command.Error));

        await SaveAndNotifyAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(row);
    }

    public async Task<JobStepRunInfo> UpsertStepRunAsync(UpsertJobStepRunCommand command, CancellationToken ct = default)
    {
        var row = await _db.JobStepRuns.FirstOrDefaultAsync(x => x.Id == (long)command.StepRunId, ct);
        if (row is null)
        {
            row = new JobStepRunRecord { Id = (long)command.StepRunId };
            _db.JobStepRuns.Add(row);
        }

        row.JobRunId = (long)command.JobRunId;
        row.JobStepId = (long?)command.JobStepId;
        row.Status = (int)command.Status;
        row.Ordinal = command.Ordinal;
        row.TaskRequestId = NormalizeNullable(command.TaskRequestId);
        row.Error = NormalizeNullable(command.Error);
        if (command.StartedAtUtc.HasValue)
        {
            row.StartedAtUtc = command.StartedAtUtc;
        }
        if (command.CompletedAtUtc.HasValue)
        {
            row.CompletedAtUtc = command.CompletedAtUtc;
        }

        await SaveAndNotifyAsync(ct);
        return Map(row);
    }

    public async Task<JobTaskActivityInfo> CreateTaskActivityAsync(CreateJobTaskActivityCommand command, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < TaskActivityInsertAttempts; attempt++)
        {
            var existing = await _db.JobTaskActivities.FirstOrDefaultAsync(x => x.RequestId == command.RequestId, ct);
            if (existing is not null)
            {
                return await SanitizeActivityAsync(existing, ct);
            }

            var row = BuildTaskActivity(command, await AllocateNextTaskActivityIdAsync(ct));
            _db.JobTaskActivities.Add(row);

            try
            {
                await SaveAndNotifyAsync(ct);
                return await SanitizeActivityAsync(row, ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _db.Entry(row).State = EntityState.Detached;

                existing = await _db.JobTaskActivities
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.RequestId == command.RequestId, ct);
                if (existing is not null)
                {
                    return await SanitizeActivityAsync(existing, ct);
                }
            }
        }

        var finalExisting = await _db.JobTaskActivities.FirstOrDefaultAsync(x => x.RequestId == command.RequestId, ct);
        if (finalExisting is not null)
        {
            return await SanitizeActivityAsync(finalExisting, ct);
        }

        var finalRow = BuildTaskActivity(command, await AllocateNextTaskActivityIdAsync(ct));
        _db.JobTaskActivities.Add(finalRow);
        await SaveAndNotifyAsync(ct);
        return await SanitizeActivityAsync(finalRow, ct);
    }

    public async Task<JobTaskActivityInfo> UpsertTaskActivityAsync(UpsertJobTaskActivityCommand command, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < TaskActivityInsertAttempts; attempt++)
        {
            var row = await _db.JobTaskActivities.FirstOrDefaultAsync(
                x => x.Id == (long)command.ActivityId || x.RequestId == command.RequestId,
                ct);
            if (row is null)
            {
                row = BuildTaskActivity(command, ResolveRequestedOrAllocatedActivityId(command, await AllocateNextTaskActivityIdAsync(ct)));
                _db.JobTaskActivities.Add(row);
            }
            else
            {
                await ProtectLegacyTaskAsync(row, ct);
                ApplyTaskActivity(row, command);
            }

            try
            {
                await SaveAndNotifyAsync(ct);
                return await SanitizeActivityAsync(row, ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _db.Entry(row).State = EntityState.Detached;

                var existing = await _db.JobTaskActivities
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.RequestId == command.RequestId, ct);
                if (existing is not null)
                {
                    return await SanitizeActivityAsync(existing, ct);
                }
            }
        }

        var finalRow = await _db.JobTaskActivities.FirstOrDefaultAsync(x => x.RequestId == command.RequestId, ct);
        if (finalRow is null)
        {
            finalRow = BuildTaskActivity(command, await AllocateNextTaskActivityIdAsync(ct));
            _db.JobTaskActivities.Add(finalRow);
        }
        else
        {
            await ProtectLegacyTaskAsync(finalRow, ct);
            ApplyTaskActivity(finalRow, command);
        }

        await SaveAndNotifyAsync(ct);
        return await SanitizeActivityAsync(finalRow, ct);
    }

    public async Task<JobTaskLogInfo> AppendTaskLogAsync(AppendJobTaskLogCommand command, CancellationToken ct = default)
    {
        var legacyActivity = await _db.JobTaskActivities.FirstOrDefaultAsync(x => x.RequestId == command.RequestId && x.TenantId == command.TenantId, ct);
        if (legacyActivity is not null) await ProtectLegacyTaskAsync(legacyActivity, ct);
        var existing = await _db.JobTaskLogs.FirstOrDefaultAsync(
            x => x.RequestId == command.RequestId &&
                 x.Sequence == command.Sequence &&
                 x.Stream == command.Stream,
            ct);

        if (existing is not null)
        {
            var run = await FindActivityRunAsync(existing.RequestId, ct);
            var activity = await _db.JobTaskActivities.FirstOrDefaultAsync(x => x.RequestId == existing.RequestId, ct);
            return Map(existing) with { Message = ProjectLog(activity, run, existing) };
        }

        var row = new JobTaskLogRecord
        {
            RequestId = command.RequestId,
            JobTaskActivityId = command.JobTaskActivityId.HasValue ? (long)command.JobTaskActivityId.Value : null,
            ClientIdentity = command.ClientIdentity.Trim(),
            TenantId = command.TenantId,
            Stream = command.Stream.Trim(),
            Message = command.Message,
            Sequence = command.Sequence,
            TimestampUtc = command.TimestampUtc,
            OutputProtectionBinding = command.OutputProtectionBinding
        };

        _db.JobTaskLogs.Add(row);
        await SaveAndNotifyAsync(ct);
        return Map(row);
    }

    public async Task<JobTaskActivityInfo?> UpdateTaskActivityStatusAsync(UpdateJobTaskActivityStatusCommand command, CancellationToken ct = default)
    {
        var row = await _db.JobTaskActivities.FirstOrDefaultAsync(x => x.RequestId == command.RequestId, ct);
        if (row is null)
        {
            return null;
        }

        await ProtectLegacyTaskAsync(row, ct);

        if (IsTerminal(row.Status) && !IsTerminal(command.Status))
        {
            return await SanitizeActivityAsync(row, ct);
        }

        row.Status = command.Status.Trim();
        if (command.Error is not null)
        {
            row.Error = NormalizeNullable(command.Error);
        }
        if (command.ResultJson is not null)
        {
            row.ResultJson = NormalizeNullable(command.ResultJson);
        }
        if (command.CompletedAtUtc.HasValue)
        {
            row.CompletedAtUtc = command.CompletedAtUtc;
        }

        if (command.StartedAtUtc.HasValue)
        {
            row.StartedAtUtc ??= command.StartedAtUtc;
        }
        if (command.ExitCode.HasValue)
        {
            row.ExitCode = command.ExitCode;
        }

        await SaveAndNotifyAsync(ct);
        return await SanitizeActivityAsync(row, ct);
    }

    private async Task<JobRunRecord?> FindActivityRunAsync(string requestId, CancellationToken ct)
    {
        var activity = await _db.JobTaskActivities.AsNoTracking().FirstOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (activity?.JobRunId is not { } runId) return null;
        return await _db.JobRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId && x.TenantId == activity.TenantId, ct);
    }

    private async Task<JobTaskActivityInfo> SanitizeActivityAsync(JobTaskActivityRecord row, CancellationToken ct)
    {
        await ProtectLegacyTaskAsync(row, ct);
        if (row.JobRunId is { } parentId) await ProtectLegacyAsync(parentId, ct);
        var run = row.JobRunId is { } id
            ? await _db.JobRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == row.TenantId, ct) : null;
        return run is null ? ProtectActivity(row) : SanitizeActivity(run, ProtectActivity(row));
    }

    private static JobTaskActivityInfo ProtectActivity(JobTaskActivityRecord row) => Map(row) with
    {
        Error = TaskOutputProtection.Project(row, row.Error, TaskOutputRedactor.ReadBinding(row.ResultJson)),
        ResultJson = TaskOutputProtection.Project(row, row.ResultJson),
        CommandLabel = TaskOutputProtection.Project(row, row.CommandLabel)
    };

    private string ProjectLog(JobTaskActivityRecord? activity, JobRunRecord? run, JobTaskLogRecord log)
    {
        var message = activity is null ? TaskOutputRedactor.Withhold(log.Message)!
            : TaskOutputProtection.Project(activity, log.Message, log.OutputProtectionBinding, envelope: false)!;
        return run is null ? message : _protection.Diagnostic(run, message)!;
    }

    private JobTaskActivityInfo SanitizeActivity(JobRunRecord run, JobTaskActivityInfo activity) => activity with
    {
        Error = _protection.Diagnostic(run, activity.Error),
        ResultJson = _protection.Diagnostic(run, activity.ResultJson),
        CommandLabel = _protection.Diagnostic(run, activity.CommandLabel)
    };

    private async Task SanitizePendingDiagnosticsAsync(CancellationToken ct)
    {
        var changed = _db.ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified)
            .Select(x => x.Entity).ToArray();
        foreach (var item in changed)
        {
            if (item is JobStepRunRecord step)
            {
                var run = await _db.JobRuns.FindAsync([step.JobRunId], ct);
                if (run is not null) step.Error = _protection.Diagnostic(run, step.Error);
            }
            else if (item is JobTaskActivityRecord activity)
            {
                var safe = ProtectActivity(activity);
                activity.Error = safe.Error;
                activity.ResultJson = safe.ResultJson;
                activity.CommandLabel = safe.CommandLabel;
                if (activity.JobRunId is { } id)
                {
                    var run = await _db.JobRuns.FindAsync([id], ct);
                    if (run is null || run.TenantId != activity.TenantId) continue;
                    activity.Error = _protection.Diagnostic(run, activity.Error);
                    activity.ResultJson = _protection.Diagnostic(run, activity.ResultJson);
                    activity.CommandLabel = _protection.Diagnostic(run, activity.CommandLabel);
                }
            }
            else if (item is JobTaskLogRecord log)
            {
                var run = await FindActivityRunAsync(log.RequestId, ct);
                var task = await _db.JobTaskActivities.FirstOrDefaultAsync(x => x.RequestId == log.RequestId && x.TenantId == log.TenantId, ct);
                log.Message = ProjectLog(task, run?.TenantId == log.TenantId ? run : null, log);
            }
        }
    }

    private async Task ProtectLegacyTaskAsync(JobTaskActivityRecord row, CancellationToken ct)
    {
        if (!TaskOutputProtection.IsUnclassifiedScript(row) || row.ProtectedLegacyOutputJson is not null) return;
        var tracked = await _db.JobTaskActivities.FindAsync([row.Id], ct);
        if (tracked is null || tracked.ProtectedLegacyOutputJson is not null) return;
        await _db.Entry(tracked).ReloadAsync(ct);
        if (tracked.ProtectedLegacyOutputJson is not null)
        {
            CopyProtectedHistory();
            return;
        }
        var logs = await _db.JobTaskLogs.Where(x => x.RequestId == row.RequestId && x.TenantId == row.TenantId).ToListAsync(ct);
        var owner = await _db.McpOperatorTasks.FirstOrDefaultAsync(x => x.TaskActivityId == row.Id && x.TenantId == row.TenantId, ct);
        var archive = JsonSerializer.Serialize(new
        {
            tracked.Error, tracked.ResultJson, tracked.CommandLabel,
            OperatorSummary = owner?.ResultSummary,
            Logs = logs.Select(x => new { x.Id, x.Message })
        });
        tracked.ProtectedLegacyOutputJson = protection.CreateProtector("NetRatel.TaskOutput.Legacy.v1",
            row.TenantId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "global", row.RequestId).Protect(archive);
        tracked.Error = TaskOutputRedactor.Withhold(tracked.Error);
        tracked.ResultJson = TaskOutputRedactor.WithholdEnvelope(tracked.ResultJson);
        tracked.CommandLabel = TaskOutputRedactor.Withhold(tracked.CommandLabel);
        foreach (var log in logs) log.Message = TaskOutputRedactor.Withhold(log.Message)!;
        if (owner is not null) owner.ResultSummary = TaskOutputRedactor.WithholdEnvelope(owner.ResultSummary);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException conflict) when (conflict.Entries.All(x => ReferenceEquals(x.Entity, tracked)))
        {
            // A simultaneous reader already archived the immutable original. Never
            // replace that archive with a later masked projection or retry its write.
            await _db.Entry(tracked).ReloadAsync(ct);
            foreach (var log in logs) await _db.Entry(log).ReloadAsync(ct);
            if (owner is not null) await _db.Entry(owner).ReloadAsync(ct);
            if (tracked.ProtectedLegacyOutputJson is null)
                throw new InvalidOperationException("Task history protection conflicted without a committed archive.");
        }
        CopyProtectedHistory();

        void CopyProtectedHistory()
        {
            row.Error = tracked.Error;
            row.ResultJson = tracked.ResultJson;
            row.CommandLabel = tracked.CommandLabel;
            row.ProtectedLegacyOutputJson = tracked.ProtectedLegacyOutputJson;
        }
    }

    private async Task ProtectLegacyAsync(long? runId, CancellationToken ct)
    {
        var query = _db.JobRuns.Where(x => x.ProtectedExecutionJson == null);
        if (runId.HasValue) query = query.Where(x => x.Id == runId.Value);
        var rows = await query.ToListAsync(ct);
        if (rows.Count == 0) return;
        // Archival saves and terminal-hash rebinding form one migration unit.
        // A crash must not commit masked task rows against the old proven hash.
        await using var ownedTransaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct) : null;
        foreach (var row in rows)
        {
            // Current definitions cannot prove the sensitivity of historical values.
            // Retain exact execution payload under DP and conservatively mask every input.
            var steps = await _db.JobStepRuns.Where(x => x.JobRunId == row.Id).ToListAsync(ct);
            var activities = await _db.JobTaskActivities.Where(x => x.JobRunId == row.Id).ToListAsync(ct);
            var control = await _db.Set<JobRunControlRecord>().FirstOrDefaultAsync(x => x.RunId == row.Id, ct);
            var originalHash = JobRunProjectionHash.Compute(new(Map(row), steps.Select(Map).ToList(), activities.Select(Map).ToList()));
            var previouslyAccepted = control?.TerminalReadyAtUtc is not null && control.TerminalResultHash == originalHash;
            // Preserve old task result and log bytes before the run's diagnostic
            // protection replaces visible history during this upgrade read.
            foreach (var activity in activities) await ProtectLegacyTaskAsync(activity, ct);
            _protection.Store(row, new("legacy-unknown-v1", null, []), row.InputsJson, row.OptionsJson);
            row.Error = _protection.Diagnostic(row, row.Error);
            foreach (var step in steps) step.Error = _protection.Diagnostic(row, step.Error);
            foreach (var activity in activities)
            {
                activity.Error = _protection.Diagnostic(row, activity.Error);
                activity.ResultJson = _protection.Diagnostic(row, activity.ResultJson);
                activity.CommandLabel = _protection.Diagnostic(row, activity.CommandLabel);
            }
            if (previouslyAccepted)
            {
                // Rebind only an already proven immutable terminal body as part of the same
                // protection write. A pre-existing mismatch never becomes accepted by upgrade.
                var protectedHash = JobRunProjectionHash.Compute(new(Map(row), steps.Select(Map).ToList(), activities.Select(Map).ToList()));
                control!.TerminalResultHash = protectedHash;
                row.InputSensitivityJson = System.Text.Json.JsonSerializer.Serialize(JobRunInputProtection.Metadata(row) with
                    { PriorTerminalResultHash = originalHash, ProtectedTerminalResultHash = protectedHash });
            }
            var requests = activities.Select(x => x.RequestId).ToArray();
            foreach (var log in await _db.JobTaskLogs.Where(x => requests.Contains(x.RequestId)).ToListAsync(ct))
                log.Message = _protection.Diagnostic(row, log.Message)!;
        }
        await _db.SaveChangesAsync(ct);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(ct);
    }

    private async Task SaveAndNotifyAsync(CancellationToken ct)
    {
        await SanitizePendingDiagnosticsAsync(ct);
        // Capture changed keys before SaveChanges clears tracking state. Notifications
        // inside an authority transaction remain invisible until its commit.
        var changed = _db.ChangeTracker.Entries()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(x => x.Entity).ToArray();
        await _db.SaveChangesAsync(ct);
        if (!_db.Database.IsNpgsql()) return;
        var keys = new HashSet<(int? TenantId, ulong? RunId, string? RequestId)>();
        foreach (var entity in changed)
        {
            switch (entity)
            {
                case JobRunRecord run:
                    keys.Add((run.TenantId, (ulong)run.Id, null));
                    break;
                case JobStepRunRecord step:
                    var tenant = await _db.JobRuns.AsNoTracking().Where(x => x.Id == step.JobRunId)
                        .Select(x => x.TenantId).FirstOrDefaultAsync(ct);
                    keys.Add((tenant, (ulong)step.JobRunId, step.TaskRequestId));
                    break;
                case JobTaskActivityRecord activity:
                    keys.Add((activity.TenantId, (ulong?)activity.JobRunId, activity.RequestId));
                    break;
                case JobTaskLogRecord log:
                    var runId = await _db.JobTaskActivities.AsNoTracking().Where(x => x.RequestId == log.RequestId)
                        .Select(x => x.JobRunId).FirstOrDefaultAsync(ct);
                    keys.Add((log.TenantId, (ulong?)runId, log.RequestId));
                    break;
            }
        }
        foreach (var key in keys)
            await ExecutionActivityNotifications.NotifyAsync(_db, key.TenantId, key.RunId, key.RequestId, ct);
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsTerminal(string status)
        => status is "Completed" or "Failed" or "Cancelled" or "TimedOut" or "Unknown" or "Skipped";

    private async Task<long> AllocateNextTaskActivityIdAsync(CancellationToken ct)
        => (await _db.JobTaskActivities.MaxAsync(x => (long?)x.Id, ct) ?? 0L) + 1L;

    private static JobTaskActivityRecord BuildTaskActivity(CreateJobTaskActivityCommand command, long id)
        => new()
        {
            Id = id,
            RequestId = command.RequestId,
            JobRunId = command.JobRunId.HasValue ? (long)command.JobRunId.Value : null,
            JobStepId = command.JobStepId.HasValue ? (long)command.JobStepId.Value : null,
            ClientIdentity = command.ClientIdentity.Trim(),
            TenantId = command.TenantId,
            AgentId = command.AgentId,
            TaskType = command.TaskType.Trim(),
            Status = command.Status.Trim(),
            Error = NormalizeNullable(command.Error),
            ResultJson = NormalizeNullable(command.ResultJson),
            CreatedAtUtc = command.CreatedAtUtc,
            CompletedAtUtc = command.CompletedAtUtc,
            Environment = command.Environment,
            StartedAtUtc = command.StartedAtUtc,
            ExitCode = command.ExitCode,
            TargetDisplayName = command.TargetDisplayName,
            TargetHostName = command.TargetHostName,
            TargetName = command.TargetName,
            ScriptName = command.ScriptName,
            ScriptType = command.ScriptType,
            CommandLabel = command.CommandLabel,
            ScriptContentHash = command.ScriptContentHash,
            OutputProtectionJson = TaskOutputProtection.Serialize(command.OutputPolicy)
        };

    private static JobTaskActivityRecord BuildTaskActivity(UpsertJobTaskActivityCommand command, long id)
    {
        var row = new JobTaskActivityRecord { Id = id };
        ApplyTaskActivity(row, command);
        return row;
    }

    private static void ApplyTaskActivity(JobTaskActivityRecord row, UpsertJobTaskActivityCommand command)
    {
        TaskOutputProtection.Bind(row, command.OutputPolicy);
        row.RequestId = command.RequestId;
        row.JobRunId = command.JobRunId.HasValue ? (long)command.JobRunId.Value : null;
        row.JobStepId = command.JobStepId.HasValue ? (long)command.JobStepId.Value : null;
        row.ClientIdentity = command.ClientIdentity.Trim();
        row.TenantId = command.TenantId;
        row.AgentId = command.AgentId;
        row.TaskType = command.TaskType.Trim();
        row.Status = command.Status.Trim();
        if (command.Error is not null)
        {
            row.Error = NormalizeNullable(command.Error);
        }
        if (command.ResultJson is not null)
        {
            row.ResultJson = NormalizeNullable(command.ResultJson);
        }
        row.CreatedAtUtc = command.CreatedAtUtc;
        row.CompletedAtUtc = command.CompletedAtUtc;
        row.Environment ??= command.Environment;
        row.StartedAtUtc ??= command.StartedAtUtc;
        row.ExitCode ??= command.ExitCode;
        row.TargetDisplayName ??= command.TargetDisplayName;
        row.TargetHostName ??= command.TargetHostName;
        row.TargetName ??= command.TargetName;
        row.ScriptName ??= command.ScriptName;
        row.ScriptType ??= command.ScriptType;
        row.CommandLabel ??= command.CommandLabel;
        row.ScriptContentHash ??= command.ScriptContentHash;
    }

    private static long ResolveRequestedOrAllocatedActivityId(UpsertJobTaskActivityCommand command, long allocatedId)
        => command.ActivityId > 0 ? (long)command.ActivityId : allocatedId;

    private static bool IsUniqueViolation(DbUpdateException ex)
        => DatabaseExceptionClassifier.IsUniqueViolation(ex);

    private static JobRunInfo Map(JobRunRecord row)
        => new(
            (ulong)row.Id,
            (ulong)row.JobId,
            row.TenantId,
            row.ClientIdentity,
            row.StartedBy,
            (JobRunState)row.Status,
            row.CurrentStepOrdinal,
            row.CreatedAtUtc,
            row.StartedAtUtc,
            row.CompletedAtUtc,
            row.Error,
            row.InputsJson,
            row.OptionsJson,
            row.AgentId,
            row.Environment,
            row.TargetDisplayName,
            row.TargetHostName,
            row.TargetName,
            JobRunInputProtection.Metadata(row).Provenance,
            JobRunInputProtection.Metadata(row).DefinitionRevision);

    private static JobStepRunInfo Map(JobStepRunRecord row)
        => new(
            (ulong)row.Id,
            (ulong)row.JobRunId,
            row.JobStepId is null ? null : (ulong?)row.JobStepId.Value,
            (JobStepRunState)row.Status,
            row.Ordinal,
            row.TaskRequestId,
            row.Error,
            row.StartedAtUtc,
            row.CompletedAtUtc);

    private static JobTaskActivityInfo Map(JobTaskActivityRecord row)
        => new(
            (ulong)row.Id,
            row.RequestId,
            row.JobRunId is null ? null : (ulong)row.JobRunId.Value,
            row.JobStepId is null ? null : (ulong)row.JobStepId.Value,
            row.ClientIdentity,
            row.TenantId,
            row.TaskType,
            row.Status,
            row.Error,
            row.CreatedAtUtc,
            row.CompletedAtUtc,
            row.AgentId,
            row.ResultJson,
            row.Environment,
            row.StartedAtUtc,
            row.ExitCode,
            row.TargetDisplayName,
            row.TargetHostName,
            row.TargetName,
            row.ScriptName,
            row.ScriptType,
            row.CommandLabel,
            row.ScriptContentHash);

    private static JobTaskLogInfo Map(JobTaskLogRecord row)
        => new(
            row.Id,
            row.RequestId,
            row.JobTaskActivityId is null ? null : (ulong)row.JobTaskActivityId.Value,
            row.ClientIdentity,
            row.TenantId,
            row.Stream,
            row.Message,
            row.Sequence,
            row.TimestampUtc);

}
