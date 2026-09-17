using Microsoft.EntityFrameworkCore;
using Npgsql;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class JobRunService(OrchestratorDbContext db) : IJobRunService
{
    private const int TaskActivityInsertAttempts = 4;
    private readonly OrchestratorDbContext _db = db;

    public async Task<IReadOnlyList<JobRunInfo>> ListAsync(CancellationToken ct = default)
        => await _db.JobRuns
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(MapRun())
            .ToListAsync(ct);

    public async Task<JobRunInfo?> GetAsync(ulong runId, CancellationToken ct = default)
        => await _db.JobRuns
            .AsNoTracking()
            .Where(x => x.Id == (long)runId)
            .Select(MapRun())
            .FirstOrDefaultAsync(ct);

    public async Task<JobRunDetails?> GetDetailsAsync(ulong runId, CancellationToken ct = default)
    {
        var run = await _db.JobRuns
            .AsNoTracking()
            .Include(x => x.Steps)
            .Include(x => x.Activities)
            .FirstOrDefaultAsync(x => x.Id == (long)runId, ct);

        if (run is null)
        {
            return null;
        }

        return new JobRunDetails(
            Map(run),
            run.Steps.OrderBy(x => x.Ordinal).ThenBy(x => x.Id).Select(Map).ToList(),
            run.Activities.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Select(Map).ToList());
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
        await _db.SaveChangesAsync(ct);
    }

    public async Task<JobTaskActivityInfo?> GetActivityByRequestIdAsync(string requestId, CancellationToken ct = default)
        => await _db.JobTaskActivities
            .AsNoTracking()
            .Where(x => x.RequestId == requestId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Select(MapActivity())
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<JobTaskActivityInfo>> ListTaskActivitiesAsync(CancellationToken ct = default)
        => await _db.JobTaskActivities
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(MapActivity())
            .ToListAsync(ct);

    public async Task<JobTaskActivityInfo?> GetActivityByIdAsync(ulong activityId, CancellationToken ct = default)
        => await _db.JobTaskActivities
            .AsNoTracking()
            .Where(x => x.Id == (long)activityId)
            .Select(MapActivity())
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<JobTaskLogInfo>> GetLogsByRequestIdAsync(string requestId, CancellationToken ct = default)
        => await _db.JobTaskLogs
            .AsNoTracking()
            .Where(x => x.RequestId == requestId)
            .OrderBy(x => x.Sequence)
            .ThenBy(x => x.Id)
            .Select(MapLog())
            .ToListAsync(ct);

    public async Task<JobRunInfo> UpsertRunAsync(UpsertJobRunCommand command, CancellationToken ct = default)
    {
        var row = await _db.JobRuns.FirstOrDefaultAsync(x => x.Id == (long)command.RunId, ct);
        if (row is null)
        {
            row = new JobRunRecord { Id = (long)command.RunId };
            _db.JobRuns.Add(row);
        }

        row.JobId = (long)command.JobId;
        row.TenantId = command.TenantId;
        row.AgentId = command.AgentId;
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
        row.Error = NormalizeNullable(command.Error);
        row.InputsJson = command.InputsJson;
        row.OptionsJson = command.OptionsJson;

        await _db.SaveChangesAsync(ct);
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

        await _db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<JobTaskActivityInfo> CreateTaskActivityAsync(CreateJobTaskActivityCommand command, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < TaskActivityInsertAttempts; attempt++)
        {
            var existing = await _db.JobTaskActivities.FirstOrDefaultAsync(x => x.RequestId == command.RequestId, ct);
            if (existing is not null)
            {
                return Map(existing);
            }

            var row = BuildTaskActivity(command, await AllocateNextTaskActivityIdAsync(ct));
            _db.JobTaskActivities.Add(row);

            try
            {
                await _db.SaveChangesAsync(ct);
                return Map(row);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _db.Entry(row).State = EntityState.Detached;

                existing = await _db.JobTaskActivities
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.RequestId == command.RequestId, ct);
                if (existing is not null)
                {
                    return Map(existing);
                }
            }
        }

        var finalExisting = await _db.JobTaskActivities.FirstOrDefaultAsync(x => x.RequestId == command.RequestId, ct);
        if (finalExisting is not null)
        {
            return Map(finalExisting);
        }

        var finalRow = BuildTaskActivity(command, await AllocateNextTaskActivityIdAsync(ct));
        _db.JobTaskActivities.Add(finalRow);
        await _db.SaveChangesAsync(ct);
        return Map(finalRow);
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
                ApplyTaskActivity(row, command);
            }

            try
            {
                await _db.SaveChangesAsync(ct);
                return Map(row);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _db.Entry(row).State = EntityState.Detached;

                var existing = await _db.JobTaskActivities
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.RequestId == command.RequestId, ct);
                if (existing is not null)
                {
                    return Map(existing);
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
            ApplyTaskActivity(finalRow, command);
        }

        await _db.SaveChangesAsync(ct);
        return Map(finalRow);
    }

    public async Task<JobTaskLogInfo> AppendTaskLogAsync(AppendJobTaskLogCommand command, CancellationToken ct = default)
    {
        var existing = await _db.JobTaskLogs.FirstOrDefaultAsync(
            x => x.RequestId == command.RequestId &&
                 x.Sequence == command.Sequence &&
                 x.Stream == command.Stream,
            ct);

        if (existing is not null)
        {
            return Map(existing);
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
            TimestampUtc = command.TimestampUtc
        };

        _db.JobTaskLogs.Add(row);
        await _db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<JobTaskActivityInfo?> UpdateTaskActivityStatusAsync(UpdateJobTaskActivityStatusCommand command, CancellationToken ct = default)
    {
        var row = await _db.JobTaskActivities.FirstOrDefaultAsync(x => x.RequestId == command.RequestId, ct);
        if (row is null)
        {
            return null;
        }

        if (IsTerminal(row.Status) && !IsTerminal(command.Status))
        {
            return Map(row);
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

        await _db.SaveChangesAsync(ct);
        return Map(row);
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsTerminal(string status)
        => status is "Completed" or "Failed" or "Cancelled";

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
            CompletedAtUtc = command.CompletedAtUtc
        };

    private static JobTaskActivityRecord BuildTaskActivity(UpsertJobTaskActivityCommand command, long id)
    {
        var row = new JobTaskActivityRecord { Id = id };
        ApplyTaskActivity(row, command);
        return row;
    }

    private static void ApplyTaskActivity(JobTaskActivityRecord row, UpsertJobTaskActivityCommand command)
    {
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
    }

    private static long ResolveRequestedOrAllocatedActivityId(UpsertJobTaskActivityCommand command, long allocatedId)
        => command.ActivityId > 0 ? (long)command.ActivityId : allocatedId;

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

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
            row.AgentId);

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
            row.ResultJson);

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

    private static System.Linq.Expressions.Expression<Func<JobTaskActivityRecord, JobTaskActivityInfo>> MapActivity()
        => row => new JobTaskActivityInfo(
            (ulong)row.Id,
            row.RequestId,
            row.JobRunId == null ? null : (ulong?)row.JobRunId.Value,
            row.JobStepId == null ? null : (ulong?)row.JobStepId.Value,
            row.ClientIdentity,
            row.TenantId,
            row.TaskType,
            row.Status,
            row.Error,
            row.CreatedAtUtc,
            row.CompletedAtUtc,
            row.AgentId,
            row.ResultJson);

    private static System.Linq.Expressions.Expression<Func<JobTaskLogRecord, JobTaskLogInfo>> MapLog()
        => row => new JobTaskLogInfo(
            row.Id,
            row.RequestId,
            row.JobTaskActivityId == null ? null : (ulong?)row.JobTaskActivityId.Value,
            row.ClientIdentity,
            row.TenantId,
            row.Stream,
            row.Message,
            row.Sequence,
            row.TimestampUtc);

    private static System.Linq.Expressions.Expression<Func<JobRunRecord, JobRunInfo>> MapRun()
        => row => new JobRunInfo(
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
            row.AgentId);
}
