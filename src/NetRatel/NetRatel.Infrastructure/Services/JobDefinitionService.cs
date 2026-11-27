using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Secrets;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Secrets;
using System.Text.Json;

namespace NetRatel.Infrastructure.Services;

public sealed class JobDefinitionService(OrchestratorDbContext db, JobSecretDefaultProtection? secretDefaults = null,
    ITenantSecretService? vault = null, IJobShellCompatibility? shellCompatibility = null) : IJobDefinitionService
{
    private readonly OrchestratorDbContext _db = db;
    private static readonly JsonSerializerOptions ProviderDefaultJson = new(JsonSerializerDefaults.Web);

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
        ValidateTargetReplacement(command.Target, command.AgentId, command.ExecutionPolicy);
        await using var transaction = await JobDefinitionMutationFence.BeginAsync(_db, ct);
        await JobTargetDefinitionGuard.RequireAsync(_db, command.TenantId, command.Target, ct);
        JobTargetScheduleValidation.RequireComplete(command.Target, command.OptionsJson, []);
        var now = DateTimeOffset.UtcNow;
        var job = new JobDefinition
        {
            Name = command.Name.Trim(),
            FolderPath = NormalizeFolder(command.FolderPath),
            Description = NormalizeNullable(command.Description),
            TenantId = command.TenantId,
            AgentId = command.Target is null ? command.AgentId : null,
            TargetJson = command.Target is null ? null : JsonSerializer.Serialize(command.Target, JobTargetJsonContext.Default.JobTargetDefinition),
            ExecutionPolicyJson = command.ExecutionPolicy is null ? null : JsonSerializer.Serialize(command.ExecutionPolicy, JobTargetJsonContext.Default.JobExecutionPolicy),
            ClientIdentity = command.Target is null ? command.ClientIdentity.Trim() : string.Empty,
            OptionsJson = NormalizeNullable(command.OptionsJson),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.Jobs.Add(job);
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(job);
    }

    public async Task<JobDefinitionInfo?> UpdateAsync(UpdateJobDefinitionCommand command, CancellationToken ct = default)
    {
        await using var transaction = await JobDefinitionMutationFence.BeginAsync(_db, ct);
        if (!await JobDefinitionMutationFence.LockActiveAsync(_db, checked((long)command.JobId), ct)) return null;
        var job = await _db.Jobs.FirstOrDefaultAsync(x => x.Id == (long)command.JobId, ct);
        if (job is null)
        {
            return null;
        }

        ValidateTargetReplacement(command.Target, command.AgentId, command.ExecutionPolicy);
        var effectiveTarget = command.Target ?? (command.AgentId.HasValue ? null : ReadTarget(job.TargetJson));
        await JobTargetDefinitionGuard.RequireAsync(_db, command.TenantId ?? job.TenantId, effectiveTarget, ct);
        var parameterDefaults = await _db.JobParameters.AsNoTracking().Where(x => x.JobId == job.Id)
            .Select(x => new { x.Name, x.DefaultValue, x.ProtectedDefaultValue, x.ProviderDefaultReferenceJson }).ToArrayAsync(ct);
        if (command.TenantId.HasValue && command.TenantId != job.TenantId &&
            parameterDefaults.Any(x => x.ProtectedDefaultValue is not null || x.ProviderDefaultReferenceJson is not null))
            throw new InvalidOperationException("Clear or rebind the protected defaults before moving this job to another tenant.");
        JobTargetScheduleValidation.RequireComplete(effectiveTarget, command.OptionsJson ?? job.OptionsJson,
            parameterDefaults.Select(x => new KeyValuePair<string, string?>(x.Name, x.DefaultValue)));
        if (command.Target is { } target)
        {
            job.TargetJson = JsonSerializer.Serialize(target, JobTargetJsonContext.Default.JobTargetDefinition);
            job.AgentId = null;
            job.ClientIdentity = string.Empty;
        }
        else if (command.AgentId.HasValue)
        {
            job.AgentId = command.AgentId;
            job.TargetJson = null;
        }
        if (command.ExecutionPolicy is { } executionPolicy)
            job.ExecutionPolicyJson = JsonSerializer.Serialize(executionPolicy, JobTargetJsonContext.Default.JobExecutionPolicy);
        if (command.Name is not null) job.Name = command.Name.Trim();
        if (command.FolderPath is not null) job.FolderPath = NormalizeFolder(command.FolderPath);
        if (command.Description is not null) job.Description = NormalizeNullable(command.Description);
        if (command.TenantId.HasValue) job.TenantId = command.TenantId;
        if (command.Target is null && command.ClientIdentity is not null) job.ClientIdentity = command.ClientIdentity.Trim();
        if (command.OptionsJson is not null) job.OptionsJson = NormalizeNullable(command.OptionsJson);
        job.UpdatedAtUtc = DateTimeOffset.UtcNow;

        if (shellCompatibility is not null)
            await ValidateShellsAsync(job, await _db.JobSteps.AsNoTracking().Where(step => step.JobId == job.Id).ToArrayAsync(ct), ct);
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(job);
    }

    public async Task<JobDefinitionInfo?> DeleteAsync(ulong jobId, CancellationToken ct = default)
    {
        await using var transaction = await JobDefinitionMutationFence.BeginAsync(_db, ct);
        if (!await JobDefinitionMutationFence.LockActiveAsync(_db, checked((long)jobId), ct)) return null;
        var job = await _db.Jobs
            .FirstOrDefaultAsync(x => x.Id == (long)jobId, ct);
        if (job is null)
        {
            return null;
        }

        // Retirement and new run admission share the row fence. Preserve the
        // definition and all historical work regardless of how the job was created.
        if (await JobDefinitionMutationFence.HasPendingReviewedAdmissionAsync(_db, job.Id, ct) ||
            await _db.JobRuns.AnyAsync(run => run.JobId == job.Id &&
                run.Status != (int)JobRunState.Succeeded && run.Status != (int)JobRunState.Failed &&
                run.Status != (int)JobRunState.Cancelled && run.Status != (int)JobRunState.TimedOut &&
                run.Status != (int)JobRunState.Unknown, ct))
            throw new JobDefinitionInUseException();

        var retiredAt = DateTimeOffset.UtcNow;
        job.DeletedAtUtc = retiredAt;
        job.UpdatedAtUtc = retiredAt;
        var operatorOwner = await _db.McpOperatorJobs.SingleOrDefaultAsync(x => x.JobId == job.Id, ct);
        if (operatorOwner is not null)
        {
            // Current tenant administration can retire an orphaned owner binding
            // without impersonating its old credential or discarding accepted audits.
            operatorOwner.DeletedAtUtc = retiredAt;
            operatorOwner.UpdatedAtUtc = retiredAt;
            operatorOwner.Version++;
        }

        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(job);
    }

    public async Task<IReadOnlyList<JobParameterInfo>> ListParamsAsync(ulong jobId, CancellationToken ct = default)
        => await _db.JobParameters
            .AsNoTracking()
            .Where(x => x.JobId == (long)jobId && _db.Jobs.Any(job => job.Id == x.JobId))
            .OrderBy(x => x.Name)
            .Select(MapParam())
            .ToListAsync(ct);

    public async Task<JobParameterInfo?> AddParamAsync(AddJobParameterCommand command, CancellationToken ct = default)
    {
        await using var transaction = await JobDefinitionMutationFence.BeginAsync(_db, ct);
        if (!await JobDefinitionMutationFence.LockActiveAsync(_db, checked((long)command.JobId), ct))
        {
            return null;
        }

        var type = NormalizeParamType(command.Type);
        var secret = type.Equals("secret", StringComparison.OrdinalIgnoreCase);
        var row = new JobParameterDefinition
        {
            JobId = (long)command.JobId,
            Name = command.Name.Trim(),
            Type = type,
            Required = command.Required,
            DefaultValue = secret ? null : command.DefaultValue,
            Description = NormalizeNullable(command.Description),
            OptionsJson = NormalizeNullable(command.OptionsJson)
        };
        if (command.DefaultSecretReference is { } reference)
        {
            if (!secret || command.DefaultValue is not null) throw InvalidSecretDefault();
            await SaveProviderDefaultAsync(row, reference, command.ExpectedSecretTenantId, ct);
        }

        await RequireParameterScheduleAsync(row.JobId, null, row.Name, row.DefaultValue, ct);
        _db.JobParameters.Add(row);
        await _db.SaveChangesAsync(ct);
        if (secret && command.DefaultValue is not null)
        {
            var tenant = await ReadJobTenantAsync(row.JobId, ct);
            Defaults().Store(row, tenant, command.DefaultValue);
            await _db.SaveChangesAsync(ct);
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(row);
    }

    public async Task<JobParameterInfo?> UpdateParamAsync(UpdateJobParameterCommand command, CancellationToken ct = default)
    {
        await using var transaction = await JobDefinitionMutationFence.BeginAsync(_db, ct);
        var row = await _db.JobParameters.FirstOrDefaultAsync(x => x.Id == (long)command.ParamId, ct);
        if (row is null || !await JobDefinitionMutationFence.LockActiveAsync(_db, row.JobId, ct))
        {
            return null;
        }

        // The child may have changed or disappeared while the parent fence was held.
        var entry = _db.Entry(row);
        await entry.ReloadAsync(ct);
        if (entry.State == EntityState.Detached) return null;

        var type = command.Type is null ? row.Type : NormalizeParamType(command.Type);
        var secret = type.Equals("secret", StringComparison.OrdinalIgnoreCase);
        var oldDefault = row.Type.Equals("secret", StringComparison.OrdinalIgnoreCase) ? null : row.DefaultValue;
        if (command.ClearProtectedDefault && command.DefaultValue is not null)
            throw new ArgumentException("Supply either a replacement default or remove the protected default.");
        if (command.DefaultSecretReference is not null && (!secret || command.ClearProtectedDefault || command.DefaultValue is not null))
            throw InvalidSecretDefault();
        await RequireParameterScheduleAsync(row.JobId, row.Id, command.Name?.Trim() ?? row.Name, secret ? null : command.DefaultValue ?? oldDefault, ct);
        if (command.Name is not null) row.Name = command.Name.Trim();
        row.Type = type;
        if (command.Required.HasValue) row.Required = command.Required.Value;
        if (secret)
        {
            if (command.DefaultSecretReference is { } reference)
                await SaveProviderDefaultAsync(row, reference, command.ExpectedSecretTenantId, ct);
            else if (command.ClearProtectedDefault)
            {
                (row.ProtectedDefaultValue, row.DefaultValue) = (null, null);
                ClearProviderDefault(row);
            }
            else if (command.DefaultValue is not null || row.DefaultValue is not null)
            {
                Defaults().Store(row, await ReadJobTenantAsync(row.JobId, ct), command.DefaultValue ?? row.DefaultValue);
                ClearProviderDefault(row);
            }
        }
        else
        {
            row.DefaultValue = command.DefaultValue ?? oldDefault;
            row.ProtectedDefaultValue = null;
            ClearProviderDefault(row);
        }
        if (command.Description is not null) row.Description = NormalizeNullable(command.Description);
        if (command.OptionsJson is not null) row.OptionsJson = NormalizeNullable(command.OptionsJson);

        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(row);
    }

    public async Task<bool> DeleteParamAsync(ulong paramId, CancellationToken ct = default)
    {
        await using var transaction = await JobDefinitionMutationFence.BeginAsync(_db, ct);
        var row = await _db.JobParameters.FirstOrDefaultAsync(x => x.Id == (long)paramId, ct);
        if (row is null || !await JobDefinitionMutationFence.LockActiveAsync(_db, row.JobId, ct))
        {
            return false;
        }

        // The child may have changed or disappeared while the parent fence was held.
        var entry = _db.Entry(row);
        await entry.ReloadAsync(ct);
        if (entry.State == EntityState.Detached) return false;

        await RequireParameterScheduleAsync(row.JobId, row.Id, null, null, ct);
        _db.JobParameters.Remove(row);
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<JobStepInfo>> ListStepsAsync(ulong jobId, CancellationToken ct = default)
        => await _db.JobSteps
            .AsNoTracking()
            .Where(x => x.JobId == (long)jobId && _db.Jobs.Any(job => job.Id == x.JobId))
            .OrderBy(x => x.Ordinal)
            .ThenBy(x => x.Id)
            .Select(MapStep())
            .ToListAsync(ct);

    public async Task<JobStepInfo?> AddStepAsync(AddJobStepCommand command, CancellationToken ct = default)
    {
        await using var transaction = await JobDefinitionMutationFence.BeginAsync(_db, ct);
        if (!await JobDefinitionMutationFence.LockActiveAsync(_db, checked((long)command.JobId), ct)) return null;
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

        await ValidateShellsAsync(job, job.Steps.Append(row), ct);
        _db.JobSteps.Add(row);
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(row);
    }

    public async Task<JobStepInfo?> UpdateStepAsync(UpdateJobStepCommand command, CancellationToken ct = default)
    {
        await using var transaction = await JobDefinitionMutationFence.BeginAsync(_db, ct);
        var row = await _db.JobSteps.FirstOrDefaultAsync(x => x.Id == (long)command.StepId, ct);
        if (row is null || !await JobDefinitionMutationFence.LockActiveAsync(_db, row.JobId, ct))
        {
            return null;
        }

        // The child may have changed or disappeared while the parent fence was held.
        var entry = _db.Entry(row);
        await entry.ReloadAsync(ct);
        if (entry.State == EntityState.Detached) return null;

        var scriptId = command.ScriptId.HasValue ? checked((long)command.ScriptId.Value) : row.ScriptId;
        if (scriptId.HasValue && !await ScriptReferenceFence.LockActiveAsync(_db, scriptId.Value, ct))
            throw new ArgumentException("The referenced script does not exist or has been deleted.", nameof(command));

        if (command.Type.HasValue) row.Type = (int)command.Type.Value;
        if (command.Runner is not null) row.Runner = NormalizeNullable(command.Runner);
        if (command.Command is not null) row.Command = command.Command;
        if (command.ScriptId.HasValue) row.ScriptId = checked((long)command.ScriptId.Value);
        if (command.PayloadJson is not null) row.PayloadJson = command.PayloadJson;
        if (command.Enabled.HasValue) row.Enabled = command.Enabled.Value;

        if (shellCompatibility is not null)
        {
            var job = await _db.Jobs.AsNoTracking().SingleAsync(parent => parent.Id == row.JobId, ct);
            var otherSteps = await _db.JobSteps.AsNoTracking().Where(step => step.JobId == row.JobId && step.Id != row.Id).ToArrayAsync(ct);
            await ValidateShellsAsync(job, otherSteps.Append(row), ct);
        }
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(row);
    }

    private async Task ValidateShellsAsync(JobDefinition job, IEnumerable<JobStepDefinition> steps, CancellationToken ct)
    {
        if (shellCompatibility is null) return;
        var enabled = steps.Where(step => step.Enabled).Select(Map).OrderBy(step => step.Ordinal).ToArray();
        if (enabled.Length == 0) return;
        var parameters = await ListParamsAsync(checked((ulong)job.Id), ct);
        await shellCompatibility.RequireDefinitionAsync(new(Map(job), parameters, enabled), ct);
    }

    public async Task<JobStepInfo?> ReorderStepAsync(ulong stepId, int newOrdinal, CancellationToken ct = default)
    {
        await using var transaction = await JobDefinitionMutationFence.BeginAsync(_db, ct);
        var row = await _db.JobSteps.FirstOrDefaultAsync(x => x.Id == (long)stepId, ct);
        if (row is null || !await JobDefinitionMutationFence.LockActiveAsync(_db, row.JobId, ct))
        {
            return null;
        }

        // The child may have changed or disappeared while the parent fence was held.
        var entry = _db.Entry(row);
        await entry.ReloadAsync(ct);
        if (entry.State == EntityState.Detached) return null;

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
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Map(row);
    }

    public async Task<bool> DeleteStepAsync(ulong stepId, CancellationToken ct = default)
    {
        await using var transaction = await JobDefinitionMutationFence.BeginAsync(_db, ct);
        var row = await _db.JobSteps.FirstOrDefaultAsync(x => x.Id == (long)stepId, ct);
        if (row is null || !await JobDefinitionMutationFence.LockActiveAsync(_db, row.JobId, ct))
        {
            return false;
        }

        // The child may have changed or disappeared while the parent fence was held.
        var entry = _db.Entry(row);
        await entry.ReloadAsync(ct);
        if (entry.State == EntityState.Detached) return false;

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
        if (transaction is not null) await transaction.CommitAsync(ct);
        return true;
    }

    private async Task RequireParameterScheduleAsync(long jobId, long? replacedId, string? name, string? defaultValue, CancellationToken ct)
    {
        var job = await _db.Jobs.AsNoTracking().SingleAsync(x => x.Id == jobId, ct);
        var parameters = await _db.JobParameters.AsNoTracking().Where(x => x.JobId == jobId && x.Id != replacedId)
            .Select(x => new { x.Name, x.DefaultValue }).ToArrayAsync(ct);
        var defaults = parameters.Select(x => new KeyValuePair<string, string?>(x.Name, x.DefaultValue)).ToList();
        if (name is not null) defaults.Add(new(name, defaultValue));
        JobTargetScheduleValidation.RequireComplete(ReadTarget(job.TargetJson), job.OptionsJson, defaults);
    }

    private static void ValidateTargetReplacement(JobTargetDefinition? target, Guid? agentId, JobExecutionPolicy? executionPolicy)
    {
        target?.Validate();
        executionPolicy?.Validate();
        if (target is not null && agentId is not null)
            throw new ArgumentException("A target definition cannot be combined with legacy AgentId.");
    }

    private static JobTargetDefinition? ReadTarget(string? json)
        => json is null ? null : JsonSerializer.Deserialize(json, JobTargetJsonContext.Default.JobTargetDefinition);
    private static JobExecutionPolicy? ReadExecutionPolicy(string? json)
        => json is null ? null : JsonSerializer.Deserialize(json, JobTargetJsonContext.Default.JobExecutionPolicy);

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

    internal static JobDefinitionInfo Map(JobDefinition row)
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
            row.AgentId)
        {
            Target = ReadTarget(row.TargetJson),
            ExecutionPolicy = ReadExecutionPolicy(row.ExecutionPolicyJson)
        };

    private static JobParameterInfo Map(JobParameterDefinition row)
        => new(
            checked((ulong)row.Id),
            checked((ulong)row.JobId),
            row.Name,
            row.Type,
            row.Required,
            row.Type.Equals("secret", StringComparison.OrdinalIgnoreCase) ? null : row.DefaultValue,
            row.Description,
            row.OptionsJson) { HasProtectedDefault = row.ProtectedDefaultValue is not null, ProviderDefault = ReadProviderDefault(row.ProviderDefaultReferenceJson) };

    internal static JobStepInfo Map(JobStepDefinition row)
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
            row.AgentId)
        {
            Target = ReadTarget(row.TargetJson),
            ExecutionPolicy = ReadExecutionPolicy(row.ExecutionPolicyJson)
        };

    private static Expression<Func<JobParameterDefinition, JobParameterInfo>> MapParam() =>
        row => new JobParameterInfo(
            (ulong)row.Id,
            (ulong)row.JobId,
            row.Name,
            row.Type,
            row.Required,
            row.Type.ToLower() == "secret" ? null : row.DefaultValue,
            row.Description,
            row.OptionsJson) { HasProtectedDefault = row.ProtectedDefaultValue != null, ProviderDefault = ReadProviderDefault(row.ProviderDefaultReferenceJson) };

    private async Task SaveProviderDefaultAsync(JobParameterDefinition row, SecretReference reference, int? expectedTenant, CancellationToken ct)
    {
        var tenant = await ReadJobTenantAsync(row.JobId, ct);
        if (tenant is not > 0 || tenant != expectedTenant || vault is null || !ProtectedJobParameterRules.IsName(row.Name))
            throw InvalidSecretDefault();
        await SecretConsumerQuery.LockTenantAsync(_db, tenant.Value, ct);
        BoundSecretReference bound;
        try { bound = await vault.BindReferenceAsync(tenant.Value, reference, ct); }
        catch (SecretProviderException error) { throw new JobTargetValidationException("job_provider_default_unavailable", error.Message); }
        row.ProviderDefaultReferenceJson = JsonSerializer.Serialize(bound, ProviderDefaultJson);
        row.ProviderDefaultConnectionId = bound.Reference.ConnectionId;
        row.ProviderDefaultLocator = bound.Reference.Locator;
        (row.ProtectedDefaultValue, row.DefaultValue) = (null, null);
    }

    internal static BoundSecretReference? ReadProviderDefault(string? json) => json is null ? null :
        JsonSerializer.Deserialize<BoundSecretReference>(json, ProviderDefaultJson) ?? throw InvalidSecretDefault();

    private static void ClearProviderDefault(JobParameterDefinition row) =>
        (row.ProviderDefaultReferenceJson, row.ProviderDefaultConnectionId, row.ProviderDefaultLocator) = (null, null, null);

    private static JobTargetValidationException InvalidSecretDefault() => new("job_provider_default_invalid",
        "Select one saved secret reference for a named Secret parameter in this job's tenant.");

    private JobSecretDefaultProtection Defaults() => secretDefaults ?? throw new InvalidOperationException("The API secret default protector is unavailable.");
    private Task<int?> ReadJobTenantAsync(long jobId, CancellationToken ct) => _db.Jobs.AsNoTracking().Where(job => job.Id == jobId)
        .Select(job => job.TenantId).SingleAsync(ct);

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
