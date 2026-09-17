using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class JobDefinitionService(OrchestratorDbContext db) : IJobDefinitionService
{
    private readonly OrchestratorDbContext _db = db;

    public async Task<IReadOnlyList<JobDefinitionInfo>> ListAsync(CancellationToken ct = default)
        => await _db.Jobs
            .AsNoTracking()
            .OrderBy(x => x.FolderPath)
            .ThenBy(x => x.Name)
            .Select(MapJob())
            .ToListAsync(ct);

    public async Task<JobDefinitionInfo?> GetAsync(ulong jobId, CancellationToken ct = default)
        => await _db.Jobs
            .AsNoTracking()
            .Where(x => x.Id == (long)jobId)
            .Select(MapJob())
            .FirstOrDefaultAsync(ct);

    public async Task<JobDefinitionDetails?> GetDetailsAsync(ulong jobId, CancellationToken ct = default)
    {
        var job = await _db.Jobs
            .AsNoTracking()
            .Include(x => x.Parameters)
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == (long)jobId, ct);

        if (job is null)
        {
            return null;
        }

        return new JobDefinitionDetails(
            Map(job),
            job.Parameters.OrderBy(x => x.Name).Select(Map).ToList(),
            job.Steps.OrderBy(x => x.Ordinal).ThenBy(x => x.Id).Select(Map).ToList());
    }

    public async Task<JobDefinitionInfo> CreateAsync(CreateJobDefinitionCommand command, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new JobDefinition
        {
            Name = command.Name.Trim(),
            FolderPath = NormalizeFolder(command.FolderPath),
            Description = NormalizeNullable(command.Description),
            TenantId = command.TenantId,
            AgentId = command.AgentId,
            ClientIdentity = command.ClientIdentity.Trim(),
            OptionsJson = NormalizeNullable(command.OptionsJson),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.Jobs.Add(job);
        await _db.SaveChangesAsync(ct);
        return Map(job);
    }

    public async Task<JobDefinitionInfo?> UpdateAsync(UpdateJobDefinitionCommand command, CancellationToken ct = default)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(x => x.Id == (long)command.JobId, ct);
        if (job is null)
        {
            return null;
        }

        if (command.Name is not null) job.Name = command.Name.Trim();
        if (command.FolderPath is not null) job.FolderPath = NormalizeFolder(command.FolderPath);
        if (command.Description is not null) job.Description = NormalizeNullable(command.Description);
        if (command.TenantId.HasValue || command.TenantId is null) job.TenantId = command.TenantId;
        if (command.AgentId.HasValue) job.AgentId = command.AgentId;
        if (command.ClientIdentity is not null) job.ClientIdentity = command.ClientIdentity.Trim();
        if (command.OptionsJson is not null) job.OptionsJson = NormalizeNullable(command.OptionsJson);
        job.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Map(job);
    }

    public async Task<JobDefinitionInfo?> DeleteAsync(ulong jobId, CancellationToken ct = default)
    {
        var job = await _db.Jobs
            .Include(x => x.Parameters)
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == (long)jobId, ct);
        if (job is null)
        {
            return null;
        }

        var runIds = await _db.JobRuns
            .Where(x => x.JobId == (long)jobId)
            .Select(x => x.Id)
            .ToListAsync(ct);
        var stepIds = job.Steps.Select(x => x.Id).ToList();

        var activities = await _db.JobTaskActivities
            .Include(x => x.Logs)
            .Where(x =>
                (x.JobRunId.HasValue && runIds.Contains(x.JobRunId.Value)) ||
                (x.JobStepId.HasValue && stepIds.Contains(x.JobStepId.Value)))
            .ToListAsync(ct);
        if (activities.Count > 0)
        {
            _db.JobTaskLogs.RemoveRange(activities.SelectMany(x => x.Logs));
            _db.JobTaskActivities.RemoveRange(activities);
        }

        var stepRuns = await _db.JobStepRuns
            .Where(x =>
                runIds.Contains(x.JobRunId) ||
                (x.JobStepId.HasValue && stepIds.Contains(x.JobStepId.Value)))
            .ToListAsync(ct);
        if (stepRuns.Count > 0)
        {
            _db.JobStepRuns.RemoveRange(stepRuns);
        }

        var runs = await _db.JobRuns
            .Where(x => x.JobId == (long)jobId)
            .ToListAsync(ct);
        _db.JobRuns.RemoveRange(runs);
        _db.Jobs.Remove(job);
        await _db.SaveChangesAsync(ct);
        return Map(job);
    }

    public async Task<IReadOnlyList<JobParameterInfo>> ListParamsAsync(ulong jobId, CancellationToken ct = default)
        => await _db.JobParameters
            .AsNoTracking()
            .Where(x => x.JobId == (long)jobId)
            .OrderBy(x => x.Name)
            .Select(MapParam())
            .ToListAsync(ct);

    public async Task<JobParameterInfo?> AddParamAsync(AddJobParameterCommand command, CancellationToken ct = default)
    {
        var jobExists = await _db.Jobs.AnyAsync(x => x.Id == (long)command.JobId, ct);
        if (!jobExists)
        {
            return null;
        }

        var row = new JobParameterDefinition
        {
            JobId = (long)command.JobId,
            Name = command.Name.Trim(),
            Type = NormalizeParamType(command.Type),
            Required = command.Required,
            DefaultValue = command.DefaultValue,
            Description = NormalizeNullable(command.Description),
            OptionsJson = NormalizeNullable(command.OptionsJson)
        };

        _db.JobParameters.Add(row);
        await _db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<JobParameterInfo?> UpdateParamAsync(UpdateJobParameterCommand command, CancellationToken ct = default)
    {
        var row = await _db.JobParameters.FirstOrDefaultAsync(x => x.Id == (long)command.ParamId, ct);
        if (row is null)
        {
            return null;
        }

        if (command.Name is not null) row.Name = command.Name.Trim();
        if (command.Type is not null) row.Type = NormalizeParamType(command.Type);
        if (command.Required.HasValue) row.Required = command.Required.Value;
        if (command.DefaultValue is not null) row.DefaultValue = command.DefaultValue;
        if (command.Description is not null) row.Description = NormalizeNullable(command.Description);
        if (command.OptionsJson is not null) row.OptionsJson = NormalizeNullable(command.OptionsJson);

        await _db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<bool> DeleteParamAsync(ulong paramId, CancellationToken ct = default)
    {
        var row = await _db.JobParameters.FirstOrDefaultAsync(x => x.Id == (long)paramId, ct);
        if (row is null)
        {
            return false;
        }

        _db.JobParameters.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<JobStepInfo>> ListStepsAsync(ulong jobId, CancellationToken ct = default)
        => await _db.JobSteps
            .AsNoTracking()
            .Where(x => x.JobId == (long)jobId)
            .OrderBy(x => x.Ordinal)
            .ThenBy(x => x.Id)
            .Select(MapStep())
            .ToListAsync(ct);

    public async Task<JobStepInfo?> AddStepAsync(AddJobStepCommand command, CancellationToken ct = default)
    {
        await using var transaction = await ScriptReferenceFence.BeginAsync(_db, ct);
        var job = await _db.Jobs.Include(x => x.Steps).FirstOrDefaultAsync(x => x.Id == (long)command.JobId, ct);
        if (job is null)
        {
            return null;
        }

        if (command.ScriptId is { } scriptId &&
            !await ScriptReferenceFence.LockActiveAsync(_db, checked((long)scriptId), ct))
            throw new ArgumentException("The referenced script does not exist or has been deleted.", nameof(command));

        var maxOrdinal = job.Steps.Count == 0 ? 0 : job.Steps.Max(x => x.Ordinal);
        var ordinal = command.Ordinal.HasValue && command.Ordinal.Value > 0 ? command.Ordinal.Value : maxOrdinal + 1;
        foreach (var step in job.Steps.Where(x => x.Ordinal >= ordinal).OrderByDescending(x => x.Ordinal))
        {
            step.Ordinal += 1;
        }

        var row = new JobStepDefinition
        {
            JobId = (long)command.JobId,
            Ordinal = ordinal,
            Type = (int)command.Type,
            Runner = NormalizeNullable(command.Runner),
            Command = command.Command,
            ScriptId = command.ScriptId.HasValue ? checked((long)command.ScriptId.Value) : null,
            PayloadJson = command.PayloadJson,
            Enabled = command.Enabled ?? true
        };

        _db.JobSteps.Add(row);
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(row);
    }

    public async Task<JobStepInfo?> UpdateStepAsync(UpdateJobStepCommand command, CancellationToken ct = default)
    {
        await using var transaction = await ScriptReferenceFence.BeginAsync(_db, ct);
        var row = await _db.JobSteps.FirstOrDefaultAsync(x => x.Id == (long)command.StepId, ct);
        if (row is null)
        {
            return null;
        }

        var scriptId = command.ScriptId.HasValue ? checked((long)command.ScriptId.Value) : row.ScriptId;
        if (scriptId.HasValue && !await ScriptReferenceFence.LockActiveAsync(_db, scriptId.Value, ct))
            throw new ArgumentException("The referenced script does not exist or has been deleted.", nameof(command));

        if (command.Type.HasValue) row.Type = (int)command.Type.Value;
        if (command.Runner is not null) row.Runner = NormalizeNullable(command.Runner);
        if (command.Command is not null) row.Command = command.Command;
        if (command.ScriptId.HasValue) row.ScriptId = checked((long)command.ScriptId.Value);
        if (command.PayloadJson is not null) row.PayloadJson = command.PayloadJson;
        if (command.Enabled.HasValue) row.Enabled = command.Enabled.Value;

        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(row);
    }

    public async Task<JobStepInfo?> ReorderStepAsync(ulong stepId, int newOrdinal, CancellationToken ct = default)
    {
        var row = await _db.JobSteps.FirstOrDefaultAsync(x => x.Id == (long)stepId, ct);
        if (row is null)
        {
            return null;
        }

        var siblings = await _db.JobSteps
            .Where(x => x.JobId == row.JobId && x.Id != row.Id)
            .OrderBy(x => x.Ordinal)
            .ToListAsync(ct);

        var bounded = Math.Max(1, newOrdinal);
        var current = row.Ordinal;
        if (bounded < current)
        {
            foreach (var sibling in siblings.Where(x => x.Ordinal >= bounded && x.Ordinal < current))
            {
                sibling.Ordinal += 1;
            }
        }
        else if (bounded > current)
        {
            foreach (var sibling in siblings.Where(x => x.Ordinal <= bounded && x.Ordinal > current))
            {
                sibling.Ordinal -= 1;
            }
        }

        row.Ordinal = bounded;
        await _db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<bool> DeleteStepAsync(ulong stepId, CancellationToken ct = default)
    {
        var row = await _db.JobSteps.FirstOrDefaultAsync(x => x.Id == (long)stepId, ct);
        if (row is null)
        {
            return false;
        }

        var stepRuns = await _db.JobStepRuns
            .Where(x => x.JobStepId == row.Id)
            .ToListAsync(ct);

        foreach (var stepRun in stepRuns)
        {
            stepRun.JobStepId = null;
        }

        var activities = await _db.JobTaskActivities
            .Where(x => x.JobStepId == row.Id)
            .ToListAsync(ct);

        foreach (var activity in activities)
        {
            activity.JobStepId = null;
        }

        var siblings = await _db.JobSteps
            .Where(x => x.JobId == row.JobId && x.Ordinal > row.Ordinal)
            .ToListAsync(ct);

        foreach (var sibling in siblings)
        {
            sibling.Ordinal -= 1;
        }

        _db.JobSteps.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string NormalizeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "/";
        var f = folder.Replace("\\", "/", StringComparison.Ordinal);
        if (!f.StartsWith("/", StringComparison.Ordinal)) f = "/" + f;
        if (!f.EndsWith("/", StringComparison.Ordinal)) f += "/";
        return f;
    }

    private static string NormalizeParamType(string type)
        => string.IsNullOrWhiteSpace(type) ? "string" : type.Trim();

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JobDefinitionInfo Map(JobDefinition row)
        => new(
            checked((ulong)row.Id),
            row.Name,
            row.FolderPath,
            row.Description,
            row.TenantId,
            row.ClientIdentity,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.OptionsJson,
            row.AgentId);

    private static JobParameterInfo Map(JobParameterDefinition row)
        => new(
            checked((ulong)row.Id),
            checked((ulong)row.JobId),
            row.Name,
            row.Type,
            row.Required,
            row.DefaultValue,
            row.Description,
            row.OptionsJson);

    private static JobStepInfo Map(JobStepDefinition row)
        => new(
            checked((ulong)row.Id),
            checked((ulong)row.JobId),
            row.Ordinal,
            (JobStepKind)row.Type,
            row.Runner,
            row.Command,
            row.ScriptId.HasValue ? checked((ulong)row.ScriptId.Value) : null,
            row.PayloadJson,
            row.Enabled);

    private static Expression<Func<JobDefinition, JobDefinitionInfo>> MapJob() =>
        row => new JobDefinitionInfo(
            (ulong)row.Id,
            row.Name,
            row.FolderPath,
            row.Description,
            row.TenantId,
            row.ClientIdentity,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.OptionsJson,
            row.AgentId);

    private static Expression<Func<JobParameterDefinition, JobParameterInfo>> MapParam() =>
        row => new JobParameterInfo(
            (ulong)row.Id,
            (ulong)row.JobId,
            row.Name,
            row.Type,
            row.Required,
            row.DefaultValue,
            row.Description,
            row.OptionsJson);

    private static Expression<Func<JobStepDefinition, JobStepInfo>> MapStep() =>
        row => new JobStepInfo(
            (ulong)row.Id,
            (ulong)row.JobId,
            row.Ordinal,
            (JobStepKind)row.Type,
            row.Runner,
            row.Command,
            row.ScriptId.HasValue ? (ulong?)row.ScriptId.Value : null,
            row.PayloadJson,
            row.Enabled);
}
