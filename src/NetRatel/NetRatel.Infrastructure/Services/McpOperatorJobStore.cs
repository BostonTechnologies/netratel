using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Owner-scoped Production job lifecycle store. It deliberately composes the
/// established job tables rather than widening their legacy administration
/// path: every V2 read first proves an ownership record and every V2 write
/// advances its ETag-backed revision with accepted-operation evidence.
/// </summary>
public sealed class McpOperatorJobStore(OrchestratorDbContext db) : IMcpOperatorJobStore
{
    private const int MaximumDefinitionJsonBytes = 8 * 1024;
    private const int MaximumParameters = 32;
    private const int MaximumSteps = 64;
    private readonly OrchestratorDbContext _db = db;

    public Task ValidateDraftAsync(McpOperatorDecision decision, McpOperatorJobDraft draft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAdmission(decision, null);
        _ = PrepareDraft(decision.Request.TenantId, draft);
        return Task.CompletedTask;
    }

    public Task ValidateParameterAsync(McpOperatorJobParameter parameter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = PrepareParameter(parameter);
        return Task.CompletedTask;
    }

    public async Task ValidateStepAsync(McpOperatorJobStep step, McpOperatorDecision decision, CancellationToken cancellationToken)
    {
        ValidateAdmission(decision, null);
        await PrepareStepAsync(step, decision.Request, lockSource: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpOperatorJobLease>> ListOwnedAsync(
        int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance,
        CancellationToken cancellationToken)
    {
        if (!IsOwnerInputValid(tenantId, agentId, principal, mcpResource, mcpInstance))
            return [];

        var subject = principal.Subject;
        var clientId = ClientId(principal);
        var rows = await _db.McpOperatorJobs.AsNoTracking()
            .Join(_db.Jobs.AsNoTracking(), owner => owner.JobId, job => job.Id, (owner, job) => new { owner, job })
            .Where(entry => entry.owner.TenantId == tenantId && entry.owner.AgentId == agentId && entry.owner.Subject == subject &&
                entry.owner.ClientId == clientId && entry.owner.McpResource == mcpResource && entry.owner.McpInstance == mcpInstance && entry.owner.DeletedAtUtc == null)
            .OrderByDescending(entry => entry.owner.UpdatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(entry => ToLease(entry.owner, entry.job)).ToArray();
    }

    public async Task<McpOperatorJobLease?> GetOwnedAsync(
        long jobId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance,
        CancellationToken cancellationToken)
    {
        if (jobId <= 0 || !IsOwnerInputValid(tenantId, agentId, principal, mcpResource, mcpInstance))
            return null;

        var row = await FindOwnedAsync(jobId, tenantId, agentId, principal, mcpResource, mcpInstance, includeDeleted: false, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToLease(row.owner, row.job);
    }

    public async Task<bool> IsExecutableAsync(
        long jobId, McpOperatorDecision decision, McpOperatorPrincipal principal, string mcpResource, string mcpInstance,
        CancellationToken cancellationToken)
    {
        ValidateAdmission(decision, null);
        var access = decision.Request;
        var agentId = access.AgentId!.Value;
        var row = await FindOwnedAsync(jobId, access.TenantId, agentId, principal, mcpResource, mcpInstance, includeDeleted: false, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || row.owner.TargetSetDigest != access.TargetSetDigest)
        {
            return false;
        }

        var steps = await _db.JobSteps.AsNoTracking()
            .Where(step => step.JobId == jobId && step.Enabled)
            .OrderBy(step => step.Ordinal)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (steps.Count == 0 || steps.Count > MaximumSteps)
            return false;

        foreach (var step in steps)
        {
            if (step.Type != (int)JobStepKind.LibraryScript || step.ScriptId is not { } scriptId ||
                !TryReadScriptReference(step.PayloadJson, out var reference))
            {
                return false;
            }
            var script = await _db.McpOperatorScripts.AsNoTracking().SingleOrDefaultAsync(candidate =>
                candidate.ScriptId == scriptId && candidate.TenantId == access.TenantId &&
                candidate.Subject == principal.Subject && candidate.ClientId == ClientId(principal) &&
                candidate.McpResource == mcpResource && candidate.McpInstance == mcpInstance && candidate.DeletedAtUtc == null,
                cancellationToken).ConfigureAwait(false);
            if (script is null || script.Version != reference.ScriptVersion ||
                !string.Equals(script.ContentHash, reference.ScriptContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    public async Task<IReadOnlyList<McpOperatorJobParameter>> ListParametersAsync(
        long jobId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance,
        CancellationToken cancellationToken)
    {
        if (await GetOwnedAsync(jobId, tenantId, agentId, principal, mcpResource, mcpInstance, cancellationToken).ConfigureAwait(false) is null)
            return [];
        var rows = await _db.JobParameters.AsNoTracking()
            .Where(parameter => parameter.JobId == jobId)
            .OrderBy(parameter => parameter.Name)
            .Take(MaximumParameters)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToParameter).ToArray();
    }

    public async Task<IReadOnlyList<McpOperatorJobStep>> ListStepsAsync(
        long jobId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance,
        CancellationToken cancellationToken)
    {
        if (await GetOwnedAsync(jobId, tenantId, agentId, principal, mcpResource, mcpInstance, cancellationToken).ConfigureAwait(false) is null)
            return [];
        var rows = await _db.JobSteps.AsNoTracking()
            .Where(step => step.JobId == jobId)
            .OrderBy(step => step.Ordinal)
            .ThenBy(step => step.Id)
            .Take(MaximumSteps)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToStep).Where(step => step is not null).Cast<McpOperatorJobStep>().ToArray();
    }

    public async Task<McpOperatorJobLease> CreateAsync(McpOperatorJobCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAdmission(request.Decision, request.AcceptedAudit);
        var prepared = PrepareDraft(request.Decision.Request.TenantId, request.Draft);
        var access = request.Decision.Request;
        var now = request.OccurredAtUtc;
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;

        var job = new JobDefinition
        {
            Name = prepared.Name,
            FolderPath = prepared.FolderPath,
            Description = prepared.Description,
            TenantId = access.TenantId,
            AgentId = access.AgentId,
            // The persisted V2 AgentId is authoritative. This display value
            // retains an explicit legacy-compatible identity rather than None.
            ClientIdentity = $"agent:{access.AgentId!.Value:D}",
            OptionsJson = prepared.OptionsJson,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var owner = new McpOperatorJobRecord
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            TenantId = access.TenantId,
            AgentId = access.AgentId!.Value,
            Subject = access.Principal.Subject,
            ClientId = ClientId(access.Principal),
            McpResource = access.McpResource!,
            McpInstance = access.McpInstance!,
            PolicyId = request.Decision.MatchingPolicyIds.Single(),
            PolicyVersion = request.Decision.SelectedPolicyVersion!.Value,
            TargetSetDigest = access.TargetSetDigest!,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1
        };
        _db.McpOperatorJobs.Add(owner);
        _db.McpOperatorJobAudits.Add(JobAudit(owner, "created", request.AcceptedAudit.AuditId, now));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToLease(owner, job);
    }

    public async Task<McpOperatorJobLease?> ReplaceAsync(McpOperatorJobReplaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAdmission(request.Decision, request.AcceptedAudit);
        if (request.JobId <= 0 || request.ExpectedVersion <= 0)
            throw new ArgumentException("A positive job id and expected ETag version are required.", nameof(request));
        var prepared = PrepareDraft(request.Decision.Request.TenantId, request.Draft);
        var row = await FindMutableOwnedAsync(request.JobId, request.Decision.Request, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureVersion(row.owner, request.ExpectedVersion);

        row.job.Name = prepared.Name;
        row.job.FolderPath = prepared.FolderPath;
        row.job.Description = prepared.Description;
        row.job.OptionsJson = prepared.OptionsJson;
        row.job.UpdatedAtUtc = request.OccurredAtUtc;
        Advance(row.owner, request.Decision, request.OccurredAtUtc);
        _db.McpOperatorJobAudits.Add(JobAudit(row.owner, "updated", request.AcceptedAudit.AuditId, request.OccurredAtUtc));
        await SaveWithConcurrencyAsync(cancellationToken).ConfigureAwait(false);
        return ToLease(row.owner, row.job);
    }

    public async Task<McpOperatorJobLease?> DeleteAsync(long jobId, long expectedVersion, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken)
    {
        ValidateAdmission(decision, acceptedAudit);
        if (jobId <= 0 || expectedVersion <= 0) throw new ArgumentException("A positive job id and expected ETag version are required.");
        var row = await FindMutableOwnedAsync(jobId, decision.Request, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureVersion(row.owner, expectedVersion);
        if (await HasActiveRunsAsync(jobId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("An active operator job run must be cancelled or settled before deleting its definition.");

        Advance(row.owner, decision, occurredAtUtc);
        row.owner.DeletedAtUtc = occurredAtUtc;
        row.job.UpdatedAtUtc = occurredAtUtc;
        _db.McpOperatorJobAudits.Add(JobAudit(row.owner, "deleted", acceptedAudit.AuditId, occurredAtUtc));
        await SaveWithConcurrencyAsync(cancellationToken).ConfigureAwait(false);
        return ToLease(row.owner, row.job);
    }

    public async Task<(McpOperatorJobLease Job, long ParameterId)?> AddParameterAsync(McpOperatorJobParameterMutationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAdmission(request.Decision, request.AcceptedAudit);
        if (request.JobId <= 0 || request.ExpectedVersion <= 0) throw new ArgumentException("A positive job id and expected ETag version are required.");
        var parameter = PrepareParameter(request.Parameter);
        var row = await FindMutableOwnedAsync(request.JobId, request.Decision.Request, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureVersion(row.owner, request.ExpectedVersion);
        var count = await _db.JobParameters.CountAsync(candidate => candidate.JobId == request.JobId, cancellationToken).ConfigureAwait(false);
        if (count >= MaximumParameters || await _db.JobParameters.AnyAsync(candidate => candidate.JobId == request.JobId && candidate.Name == parameter.Name, cancellationToken).ConfigureAwait(false))
            throw new ArgumentException("The job parameter limit or uniqueness constraint was violated.");

        var created = new JobParameterDefinition
        {
            JobId = request.JobId,
            Name = parameter.Name,
            Type = parameter.Type,
            Required = parameter.Required,
            DefaultValue = parameter.DefaultValue,
            Description = parameter.Description,
            OptionsJson = SerializeParameterMetadata(parameter)
        };
        _db.JobParameters.Add(created);
        Touch(row, request.Decision, request.AcceptedAudit, "param_added", request.OccurredAtUtc);
        await SaveWithConcurrencyAsync(cancellationToken).ConfigureAwait(false);
        return (ToLease(row.owner, row.job), created.Id);
    }

    public async Task<McpOperatorJobLease?> ReplaceParameterAsync(McpOperatorJobParameterMutationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAdmission(request.Decision, request.AcceptedAudit);
        if (request.JobId <= 0 || request.ParameterId is not > 0 || request.ExpectedVersion <= 0) throw new ArgumentException("A job, parameter, and expected ETag version are required.");
        var parameter = PrepareParameter(request.Parameter);
        var row = await FindMutableOwnedAsync(request.JobId, request.Decision.Request, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureVersion(row.owner, request.ExpectedVersion);
        var existing = await _db.JobParameters.SingleOrDefaultAsync(candidate => candidate.Id == request.ParameterId && candidate.JobId == request.JobId, cancellationToken).ConfigureAwait(false);
        if (existing is null) return null;
        if (await _db.JobParameters.AnyAsync(candidate => candidate.JobId == request.JobId && candidate.Id != existing.Id && candidate.Name == parameter.Name, cancellationToken).ConfigureAwait(false))
            throw new ArgumentException("Job parameter names must remain unique.");
        existing.Name = parameter.Name;
        existing.Type = parameter.Type;
        existing.Required = parameter.Required;
        existing.DefaultValue = parameter.DefaultValue;
        existing.Description = parameter.Description;
        existing.OptionsJson = SerializeParameterMetadata(parameter);
        Touch(row, request.Decision, request.AcceptedAudit, "param_updated", request.OccurredAtUtc);
        await SaveWithConcurrencyAsync(cancellationToken).ConfigureAwait(false);
        return ToLease(row.owner, row.job);
    }

    public async Task<McpOperatorJobLease?> DeleteParameterAsync(long jobId, long parameterId, long expectedVersion, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken)
    {
        ValidateAdmission(decision, acceptedAudit);
        if (jobId <= 0 || parameterId <= 0 || expectedVersion <= 0) throw new ArgumentException("A job, parameter, and expected ETag version are required.");
        var row = await FindMutableOwnedAsync(jobId, decision.Request, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureVersion(row.owner, expectedVersion);
        var existing = await _db.JobParameters.SingleOrDefaultAsync(candidate => candidate.Id == parameterId && candidate.JobId == jobId, cancellationToken).ConfigureAwait(false);
        if (existing is null) return null;
        _db.JobParameters.Remove(existing);
        Touch(row, decision, acceptedAudit, "param_deleted", occurredAtUtc);
        await SaveWithConcurrencyAsync(cancellationToken).ConfigureAwait(false);
        return ToLease(row.owner, row.job);
    }

    public async Task<(McpOperatorJobLease Job, long StepId)?> AddStepAsync(McpOperatorJobStepMutationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAdmission(request.Decision, request.AcceptedAudit);
        await using var transaction = await ScriptReferenceFence.BeginAsync(_db, cancellationToken);
        if (request.JobId <= 0 || request.ExpectedVersion <= 0) throw new ArgumentException("A positive job id and expected ETag version are required.");
        var row = await FindMutableOwnedAsync(request.JobId, request.Decision.Request, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureVersion(row.owner, request.ExpectedVersion);
        var step = await PrepareStepAsync(request.Step, request.Decision.Request, lockSource: true, cancellationToken).ConfigureAwait(false);
        var existing = await _db.JobSteps.Where(candidate => candidate.JobId == request.JobId).OrderBy(candidate => candidate.Ordinal).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Count >= MaximumSteps) throw new ArgumentException("The job step limit was reached.");
        var ordinal = Math.Clamp(step.Ordinal ?? (existing.Count + 1), 1, existing.Count + 1);
        foreach (var sibling in existing.Where(candidate => candidate.Ordinal >= ordinal).OrderByDescending(candidate => candidate.Ordinal)) sibling.Ordinal++;
        var created = new JobStepDefinition
        {
            JobId = request.JobId,
            Ordinal = ordinal,
            Type = (int)JobStepKind.LibraryScript,
            Runner = "mcp-operator",
            ScriptId = step.ScriptId,
            PayloadJson = SerializeScriptReference(step),
            Enabled = step.Enabled
        };
        _db.JobSteps.Add(created);
        Touch(row, request.Decision, request.AcceptedAudit, "step_added", request.OccurredAtUtc);
        await SaveWithConcurrencyAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return (ToLease(row.owner, row.job), created.Id);
    }

    public async Task<McpOperatorJobLease?> ReplaceStepAsync(McpOperatorJobStepMutationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAdmission(request.Decision, request.AcceptedAudit);
        await using var transaction = await ScriptReferenceFence.BeginAsync(_db, cancellationToken);
        if (request.JobId <= 0 || request.StepId is not > 0 || request.ExpectedVersion <= 0) throw new ArgumentException("A job, step, and expected ETag version are required.");
        var row = await FindMutableOwnedAsync(request.JobId, request.Decision.Request, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureVersion(row.owner, request.ExpectedVersion);
        var existing = await _db.JobSteps.SingleOrDefaultAsync(candidate => candidate.Id == request.StepId && candidate.JobId == request.JobId, cancellationToken).ConfigureAwait(false);
        if (existing is null) return null;
        var step = await PrepareStepAsync(request.Step, request.Decision.Request, lockSource: true, cancellationToken).ConfigureAwait(false);
        existing.Type = (int)JobStepKind.LibraryScript;
        existing.Runner = "mcp-operator";
        existing.Command = null;
        existing.ScriptId = step.ScriptId;
        existing.PayloadJson = SerializeScriptReference(step);
        existing.Enabled = step.Enabled;
        Touch(row, request.Decision, request.AcceptedAudit, "step_updated", request.OccurredAtUtc);
        await SaveWithConcurrencyAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return ToLease(row.owner, row.job);
    }

    public async Task<McpOperatorJobLease?> ReorderStepAsync(long jobId, long stepId, int ordinal, long expectedVersion, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken)
    {
        ValidateAdmission(decision, acceptedAudit);
        if (jobId <= 0 || stepId <= 0 || expectedVersion <= 0) throw new ArgumentException("A job, step, and expected ETag version are required.");
        var row = await FindMutableOwnedAsync(jobId, decision.Request, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureVersion(row.owner, expectedVersion);
        var existing = await _db.JobSteps.SingleOrDefaultAsync(candidate => candidate.Id == stepId && candidate.JobId == jobId, cancellationToken).ConfigureAwait(false);
        if (existing is null) return null;
        var siblings = await _db.JobSteps.Where(candidate => candidate.JobId == jobId && candidate.Id != stepId).OrderBy(candidate => candidate.Ordinal).ToListAsync(cancellationToken).ConfigureAwait(false);
        var target = Math.Clamp(ordinal, 1, siblings.Count + 1);
        if (target < existing.Ordinal)
            foreach (var sibling in siblings.Where(candidate => candidate.Ordinal >= target && candidate.Ordinal < existing.Ordinal)) sibling.Ordinal++;
        else if (target > existing.Ordinal)
            foreach (var sibling in siblings.Where(candidate => candidate.Ordinal <= target && candidate.Ordinal > existing.Ordinal)) sibling.Ordinal--;
        existing.Ordinal = target;
        Touch(row, decision, acceptedAudit, "step_reordered", occurredAtUtc);
        await SaveWithConcurrencyAsync(cancellationToken).ConfigureAwait(false);
        return ToLease(row.owner, row.job);
    }

    public async Task<McpOperatorJobLease?> DeleteStepAsync(long jobId, long stepId, long expectedVersion, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken)
    {
        ValidateAdmission(decision, acceptedAudit);
        if (jobId <= 0 || stepId <= 0 || expectedVersion <= 0) throw new ArgumentException("A job, step, and expected ETag version are required.");
        var row = await FindMutableOwnedAsync(jobId, decision.Request, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureVersion(row.owner, expectedVersion);
        var existing = await _db.JobSteps.SingleOrDefaultAsync(candidate => candidate.Id == stepId && candidate.JobId == jobId, cancellationToken).ConfigureAwait(false);
        if (existing is null) return null;
        // Run history survives definition edits. Clear only its optional definition
        // links, as JobDefinitionService does, before deleting the referenced step.
        var stepRuns = await _db.JobStepRuns.Where(candidate => candidate.JobStepId == stepId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var stepRun in stepRuns) stepRun.JobStepId = null;
        var activities = await _db.JobTaskActivities.Where(candidate => candidate.JobStepId == stepId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var activity in activities) activity.JobStepId = null;
        var siblings = await _db.JobSteps.Where(candidate => candidate.JobId == jobId && candidate.Ordinal > existing.Ordinal).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var sibling in siblings) sibling.Ordinal--;
        _db.JobSteps.Remove(existing);
        Touch(row, decision, acceptedAudit, "step_deleted", occurredAtUtc);
        await SaveWithConcurrencyAsync(cancellationToken).ConfigureAwait(false);
        return ToLease(row.owner, row.job);
    }

    public async Task<McpOperatorJobRunLease?> GetRunOwnedAsync(ulong runId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, CancellationToken cancellationToken)
    {
        if (runId == 0 || runId > long.MaxValue || !IsOwnerInputValid(tenantId, agentId, principal, mcpResource, mcpInstance)) return null;
        var row = await FindOwnedRunAsync(runId, tenantId, agentId, principal, mcpResource, mcpInstance, includeDeleted: false, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToRunLease(row.run, row.projection);
    }

    public async Task<IReadOnlyList<McpOperatorJobRunLease>> ListRunsOwnedAsync(long? jobId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string mcpResource, string mcpInstance, CancellationToken cancellationToken)
    {
        if (!IsOwnerInputValid(tenantId, agentId, principal, mcpResource, mcpInstance)) return [];
        var subject = principal.Subject;
        var clientId = ClientId(principal);
        var query = _db.McpOperatorJobRuns.AsNoTracking()
            .Join(_db.McpOperatorJobs.AsNoTracking(), run => run.JobRecordId, owner => owner.Id, (run, owner) => new { run, owner })
            .Join(_db.JobRuns.AsNoTracking(), entry => entry.run.JobRunId, projection => projection.Id, (entry, projection) => new { entry.run, entry.owner, projection })
            .Where(entry => entry.owner.TenantId == tenantId && entry.owner.AgentId == agentId && entry.owner.Subject == subject &&
                entry.owner.ClientId == clientId && entry.owner.McpResource == mcpResource && entry.owner.McpInstance == mcpInstance &&
                entry.run.DeletedAtUtc == null);
        if (jobId is > 0) query = query.Where(entry => entry.run.JobId == jobId.Value);
        var rows = await query.OrderByDescending(entry => entry.run.CreatedAtUtc).Take(100).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(entry => ToRunLease(entry.run, entry.projection)).ToArray();
    }

    public async Task<McpOperatorJobRunLease> RecordStartedRunAsync(long jobId, ulong runId, Guid idempotencyId, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken)
    {
        ValidateAdmission(decision, acceptedAudit);
        if (jobId <= 0 || runId == 0 || runId > long.MaxValue || idempotencyId == Guid.Empty) throw new ArgumentException("A job, run, and idempotency admission are required.");
        var row = await FindMutableOwnedAsync(jobId, decision.Request, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The operator-owned job was not found.");
        if (row.owner.TargetSetDigest != decision.Request.TargetSetDigest) throw new InvalidOperationException("The frozen job target set changed before dispatch.");
        var projection = await _db.JobRuns.SingleOrDefaultAsync(candidate => candidate.Id == (long)runId && candidate.JobId == jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The job authority did not create the requested run projection.");
        var existing = await _db.McpOperatorJobRuns.SingleOrDefaultAsync(candidate => candidate.IdempotencyId == idempotencyId || candidate.JobRunId == checked((long)runId), cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.JobId != jobId || existing.JobRunId != checked((long)runId)) throw new InvalidOperationException("The job run idempotency admission is already bound to another run.");
            return ToRunLease(existing, projection);
        }
        var record = new McpOperatorJobRunRecord
        {
            Id = Guid.NewGuid(),
            JobRunId = checked((long)runId),
            JobRecordId = row.owner.Id,
            JobId = jobId,
            TenantId = row.owner.TenantId,
            AgentId = row.owner.AgentId,
            AcceptedAuditId = acceptedAudit.AuditId,
            IdempotencyId = idempotencyId,
            CorrelationId = decision.Request.CorrelationId,
            TargetSetDigest = decision.Request.TargetSetDigest!,
            CreatedAtUtc = occurredAtUtc,
            UpdatedAtUtc = occurredAtUtc,
            Version = 1
        };
        _db.McpOperatorJobRuns.Add(record);
        _db.McpOperatorJobRunAudits.Add(RunAudit(record, "started", acceptedAudit.AuditId, occurredAtUtc));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToRunLease(record, projection);
    }

    public async Task<McpOperatorJobRunLease?> RecordCancellationRequestedAsync(ulong runId, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken)
    {
        ValidateAdmission(decision, acceptedAudit);
        if (runId == 0 || runId > long.MaxValue) return null;
        var row = await FindMutableRunAsync(runId, decision.Request, cancellationToken).ConfigureAwait(false);
        if (row is null || row.run.TargetSetDigest != decision.Request.TargetSetDigest || IsTerminal((JobRunState)row.projection.Status)) return null;
        if (row.run.CancellationRequested) return ToRunLease(row.run, row.projection);
        row.run.CancellationRequested = true;
        row.run.CancellationRequestedAtUtc = occurredAtUtc;
        row.run.UpdatedAtUtc = occurredAtUtc;
        row.run.Version++;
        _db.McpOperatorJobRunAudits.Add(RunAudit(row.run, "cancel_requested", acceptedAudit.AuditId, occurredAtUtc));
        await SaveWithConcurrencyAsync(cancellationToken).ConfigureAwait(false);
        return ToRunLease(row.run, row.projection);
    }

    public async Task<McpOperatorJobRunLease?> DeleteRunAsync(ulong runId, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken)
    {
        ValidateAdmission(decision, acceptedAudit);
        if (runId == 0 || runId > long.MaxValue) return null;
        var row = await FindMutableRunAsync(runId, decision.Request, cancellationToken).ConfigureAwait(false);
        if (row is null || row.run.TargetSetDigest != decision.Request.TargetSetDigest || !IsTerminal((JobRunState)row.projection.Status)) return null;
        if (row.run.DeletedAtUtc is not null) return ToRunLease(row.run, row.projection);
        row.run.DeletedAtUtc = occurredAtUtc;
        row.run.UpdatedAtUtc = occurredAtUtc;
        row.run.Version++;
        _db.McpOperatorJobRunAudits.Add(RunAudit(row.run, "deleted", acceptedAudit.AuditId, occurredAtUtc));
        await SaveWithConcurrencyAsync(cancellationToken).ConfigureAwait(false);
        return ToRunLease(row.run, row.projection);
    }

    private async Task<OwnedJob?> FindOwnedAsync(long jobId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string resource, string instance, bool includeDeleted, CancellationToken cancellationToken)
    {
        var subject = principal.Subject;
        var clientId = ClientId(principal);
        var rows = await _db.McpOperatorJobs.AsNoTracking()
            .Join(_db.Jobs.AsNoTracking(), owner => owner.JobId, job => job.Id, (owner, job) => new { owner, job })
            .SingleOrDefaultAsync(entry => entry.owner.JobId == jobId && entry.owner.TenantId == tenantId && entry.owner.AgentId == agentId &&
                entry.owner.Subject == subject && entry.owner.ClientId == clientId && entry.owner.McpResource == resource && entry.owner.McpInstance == instance &&
                (includeDeleted || entry.owner.DeletedAtUtc == null), cancellationToken)
            .ConfigureAwait(false);
        return rows is null ? null : new OwnedJob(rows.owner, rows.job);
    }

    private async Task<OwnedJob?> FindMutableOwnedAsync(long jobId, McpOperatorAccessRequest access, CancellationToken cancellationToken)
    {
        var agentId = access.AgentId!.Value;
        var subject = access.Principal.Subject;
        var clientId = ClientId(access.Principal);
        var resource = access.McpResource!;
        var instance = access.McpInstance!;
        var rows = await _db.McpOperatorJobs.Join(_db.Jobs, owner => owner.JobId, job => job.Id, (owner, job) => new { owner, job })
            .SingleOrDefaultAsync(entry => entry.owner.JobId == jobId && entry.owner.DeletedAtUtc == null &&
                entry.owner.TenantId == access.TenantId && entry.owner.AgentId == agentId && entry.owner.Subject == subject &&
                entry.owner.ClientId == clientId && entry.owner.McpResource == resource && entry.owner.McpInstance == instance, cancellationToken).ConfigureAwait(false);
        return rows is null ? null : new OwnedJob(rows.owner, rows.job);
    }

    private async Task<OwnedRun?> FindOwnedRunAsync(ulong runId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string resource, string instance, bool includeDeleted, CancellationToken cancellationToken)
    {
        var subject = principal.Subject;
        var clientId = ClientId(principal);
        var rows = await _db.McpOperatorJobRuns.AsNoTracking()
            .Join(_db.McpOperatorJobs.AsNoTracking(), run => run.JobRecordId, owner => owner.Id, (run, owner) => new { run, owner })
            .Join(_db.JobRuns.AsNoTracking(), entry => entry.run.JobRunId, projection => projection.Id, (entry, projection) => new { entry.run, entry.owner, projection })
            .SingleOrDefaultAsync(entry => entry.run.JobRunId == checked((long)runId) && entry.owner.TenantId == tenantId && entry.owner.AgentId == agentId &&
                entry.owner.Subject == subject && entry.owner.ClientId == clientId && entry.owner.McpResource == resource && entry.owner.McpInstance == instance &&
                (includeDeleted || entry.run.DeletedAtUtc == null), cancellationToken).ConfigureAwait(false);
        return rows is null ? null : new OwnedRun(rows.run, rows.projection);
    }

    private async Task<OwnedRun?> FindMutableRunAsync(ulong runId, McpOperatorAccessRequest access, CancellationToken cancellationToken)
    {
        var agentId = access.AgentId!.Value;
        var subject = access.Principal.Subject;
        var clientId = ClientId(access.Principal);
        var resource = access.McpResource!;
        var instance = access.McpInstance!;
        var rows = await _db.McpOperatorJobRuns.Join(_db.McpOperatorJobs, run => run.JobRecordId, owner => owner.Id, (run, owner) => new { run, owner })
            .Join(_db.JobRuns, entry => entry.run.JobRunId, projection => projection.Id, (entry, projection) => new { entry.run, entry.owner, projection })
            .SingleOrDefaultAsync(entry => entry.run.JobRunId == checked((long)runId) && entry.run.DeletedAtUtc == null &&
                entry.owner.TenantId == access.TenantId && entry.owner.AgentId == agentId && entry.owner.Subject == subject &&
                entry.owner.ClientId == clientId && entry.owner.McpResource == resource && entry.owner.McpInstance == instance, cancellationToken).ConfigureAwait(false);
        return rows is null ? null : new OwnedRun(rows.run, rows.projection);
    }

    private async Task<McpOperatorJobStep> PrepareStepAsync(McpOperatorJobStep step, McpOperatorAccessRequest access, bool lockSource, CancellationToken cancellationToken)
    {
        if (step.ScriptId <= 0 || step.ScriptVersion <= 0 || !IsSha256(step.ScriptContentHash) || step.Ordinal is < 1 or > MaximumSteps)
            throw new ArgumentException("The job step must identify an exact reviewed script revision.");
        var script = await _db.McpOperatorScripts.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.ScriptId == step.ScriptId && candidate.TenantId == access.TenantId && candidate.Subject == access.Principal.Subject &&
            candidate.ClientId == ClientId(access.Principal) && candidate.McpResource == access.McpResource && candidate.McpInstance == access.McpInstance && candidate.DeletedAtUtc == null,
            cancellationToken).ConfigureAwait(false);
        if (script is null || script.Version != step.ScriptVersion || !string.Equals(script.ContentHash, step.ScriptContentHash, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The job step script is not an owned current reviewed revision.");
        var active = lockSource
            ? await ScriptReferenceFence.LockActiveAsync(_db, step.ScriptId, cancellationToken)
            : await _db.Scripts.AsNoTracking().AnyAsync(source => source.Id == step.ScriptId, cancellationToken);
        if (!active)
            throw new ArgumentException("The referenced script does not exist or has been deleted.");
        return step with { ScriptContentHash = step.ScriptContentHash.ToUpperInvariant(), Id = null };
    }

    private static PreparedJobDraft PrepareDraft(int tenantId, McpOperatorJobDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new PreparedJobDraft(
            NormalizeText(draft.Name, 120, "name"),
            NormalizeFolder(tenantId, draft.FolderPath),
            NormalizeNullable(draft.Description, 512, "description"),
            NormalizeOptions(draft.OptionsJson));
    }

    private static McpOperatorJobParameter PrepareParameter(McpOperatorJobParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var name = NormalizeIdentifier(parameter.Name, 64, "parameter name");
        var type = parameter.Type?.Trim().ToLowerInvariant();
        if (type is not ("string" or "integer" or "boolean" or "choice" or "secret_reference"))
            throw new ArgumentException("The job parameter type is not supported.");
        var description = NormalizeNullable(parameter.Description, 256, "parameter description");
        var options = parameter.Options ?? [];
        if (options.Count > 32 || options.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)) || options.Distinct(StringComparer.Ordinal).Count() != options.Count)
            throw new ArgumentException("The job parameter options are invalid.");
        var defaultValue = parameter.DefaultValue;
        if (defaultValue is { Length: > 1024 } || defaultValue?.Any(char.IsControl) == true)
            throw new ArgumentException("The job parameter default is invalid.");
        if (type == "choice" && options.Count == 0) throw new ArgumentException("A choice parameter requires bounded options.");
        if (type == "secret_reference")
        {
            if (defaultValue is not null || !IsIdentifier(parameter.SecretReference, 128))
                throw new ArgumentException("A secret parameter must contain an opaque reference and no default value.");
        }
        else if (parameter.SecretReference is not null || (LooksSensitive(name) && (defaultValue is not null || options.Count > 0)))
        {
            throw new ArgumentException("Sensitive job parameters must use secret_reference without an inline default or options.");
        }
        return new McpOperatorJobParameter(name, type, parameter.Required, description, defaultValue, options.ToArray(), parameter.SecretReference, parameter.Id);
    }

    private static McpOperatorJobParameter ToParameter(JobParameterDefinition row)
    {
        var metadata = ReadParameterMetadata(row.OptionsJson);
        return new McpOperatorJobParameter(row.Name, row.Type, row.Required, row.Description, row.DefaultValue, metadata.Options, metadata.SecretReference, row.Id);
    }

    private static McpOperatorJobStep? ToStep(JobStepDefinition row)
        => row.Type == (int)JobStepKind.LibraryScript && row.ScriptId is { } scriptId && TryReadScriptReference(row.PayloadJson, out var reference)
            ? new McpOperatorJobStep(row.Ordinal, scriptId, reference.ScriptVersion, reference.ScriptContentHash, row.Enabled, row.Id)
            : null;

    private static string SerializeParameterMetadata(McpOperatorJobParameter parameter) => JsonSerializer.Serialize(new ParameterMetadata(parameter.Options ?? [], parameter.SecretReference));
    private static ParameterMetadata ReadParameterMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new([], null);
        try { return JsonSerializer.Deserialize<ParameterMetadata>(value) ?? new([], null); }
        catch (JsonException) { return new([], null); }
    }

    private static string SerializeScriptReference(McpOperatorJobStep step) => JsonSerializer.Serialize(new LibraryScriptReference(step.ScriptVersion, step.ScriptContentHash));
    private static bool TryReadScriptReference(string? value, out LibraryScriptReference reference)
    {
        reference = default!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            reference = JsonSerializer.Deserialize<LibraryScriptReference>(value)!;
            return reference is not null && reference.ScriptVersion > 0 && IsSha256(reference.ScriptContentHash);
        }
        catch (JsonException) { return false; }
    }

    private static McpOperatorJobLease ToLease(McpOperatorJobRecord owner, JobDefinition job) => new(
        owner.JobId, owner.TenantId, owner.AgentId, owner.Subject, owner.ClientId, owner.McpResource, owner.McpInstance,
        owner.PolicyId, owner.PolicyVersion, owner.TargetSetDigest, job.Name, job.FolderPath, job.Description, job.OptionsJson,
        owner.DeletedAtUtc is not null, owner.CreatedAtUtc, owner.UpdatedAtUtc, owner.Version);

    private static McpOperatorJobRunLease ToRunLease(McpOperatorJobRunRecord run, JobRunRecord projection) => new(
        checked((ulong)run.JobRunId), run.JobId, run.TenantId, run.AgentId, run.AcceptedAuditId, run.IdempotencyId, run.CorrelationId,
        run.TargetSetDigest, ((JobRunState)projection.Status).ToString(), projection.CurrentStepOrdinal, run.CreatedAtUtc,
        projection.StartedAtUtc, projection.CompletedAtUtc, projection.Error, run.CancellationRequested, run.DeletedAtUtc is not null, run.Version);

    private static McpOperatorJobAuditRecord JobAudit(McpOperatorJobRecord owner, string action, Guid acceptedAuditId, DateTimeOffset occurredAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        JobRecordId = owner.Id,
        JobId = owner.JobId,
        JobVersion = owner.Version,
        Action = action,
        AcceptedAuditId = acceptedAuditId,
        OccurredAtUtc = occurredAtUtc
    };

    private static McpOperatorJobRunAuditRecord RunAudit(McpOperatorJobRunRecord run, string action, Guid acceptedAuditId, DateTimeOffset occurredAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        JobRunRecordId = run.Id,
        Action = action,
        AcceptedAuditId = acceptedAuditId,
        OccurredAtUtc = occurredAtUtc
    };

    private void Touch(OwnedJob row, McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit, string action, DateTimeOffset occurredAtUtc)
    {
        Advance(row.owner, decision, occurredAtUtc);
        row.job.UpdatedAtUtc = occurredAtUtc;
        _db.McpOperatorJobAudits.Add(JobAudit(row.owner, action, acceptedAudit.AuditId, occurredAtUtc));
    }

    private static void Advance(McpOperatorJobRecord owner, McpOperatorDecision decision, DateTimeOffset occurredAtUtc)
    {
        owner.PolicyId = decision.MatchingPolicyIds.Single();
        owner.PolicyVersion = decision.SelectedPolicyVersion!.Value;
        owner.TargetSetDigest = decision.Request.TargetSetDigest!;
        owner.UpdatedAtUtc = occurredAtUtc;
        owner.Version++;
    }

    private async Task<bool> HasActiveRunsAsync(long jobId, CancellationToken cancellationToken)
        => await _db.JobRuns.AnyAsync(run => run.JobId == jobId && run.Status != (int)JobRunState.Succeeded && run.Status != (int)JobRunState.Failed &&
            run.Status != (int)JobRunState.Cancelled && run.Status != (int)JobRunState.TimedOut, cancellationToken).ConfigureAwait(false);

    private async Task SaveWithConcurrencyAsync(CancellationToken cancellationToken)
    {
        try { await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { throw new McpOperatorJobConcurrencyException(); }
    }

    private static void EnsureVersion(McpOperatorJobRecord owner, long expectedVersion)
    {
        if (owner.Version != expectedVersion) throw new McpOperatorJobConcurrencyException();
    }

    private static void ValidateAdmission(McpOperatorDecision decision, McpOperatorAcceptedAudit? acceptedAudit)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var access = decision.Request;
        if (!decision.IsAllowed || access.Environment is not (McpOperatorEnvironment.Development or McpOperatorEnvironment.Production) || access.TenantId <= 0 ||
            access.AgentId is null || access.AgentId == Guid.Empty || decision.MatchingPolicyIds.Count != 1 || decision.SelectedPolicyVersion is null ||
            decision.EffectiveConstraints is null || decision.EffectiveConstraints.MaxJobTargetCount is not > 0 || decision.EffectiveConstraints.MaxFanOut is not > 0 ||
            !IsSha256(access.TargetSetDigest) || string.IsNullOrWhiteSpace(access.McpResource) || string.IsNullOrWhiteSpace(access.McpInstance))
        {
            throw new ArgumentException("The job request is not an allowed, target-frozen Production policy admission.");
        }
        if (acceptedAudit is not null && (acceptedAudit.AuditId == Guid.Empty || acceptedAudit.PolicyId != decision.MatchingPolicyIds[0] ||
            acceptedAudit.TenantId != access.TenantId || acceptedAudit.AgentId != access.AgentId ||
            !string.Equals(acceptedAudit.Subject, access.Principal.Subject, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The job accepted-operation audit does not match the policy admission.");
        }
    }

    private static bool IsOwnerInputValid(int tenantId, Guid agentId, McpOperatorPrincipal principal, string resource, string instance) =>
        tenantId > 0 && agentId != Guid.Empty && IsSafe(principal.Subject, 256) && (principal.ClientId is null || IsSafe(principal.ClientId, 256)) && IsSafe(resource, 512) && IsSafe(instance, 32);

    private static string ClientId(McpOperatorPrincipal principal) => principal.ClientId ?? string.Empty;
    private static bool IsSafe(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool IsTerminal(JobRunState state) => state is JobRunState.Succeeded or JobRunState.Failed or JobRunState.Cancelled or JobRunState.TimedOut;
    private static bool LooksSensitive(string value) => value.Contains("password", StringComparison.OrdinalIgnoreCase) || value.Contains("secret", StringComparison.OrdinalIgnoreCase) || value.Contains("token", StringComparison.OrdinalIgnoreCase) || value.Contains("key", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeText(string? value, int maximum, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maximum || normalized.Any(char.IsControl)) throw new ArgumentException($"The job {name} is invalid.");
        return normalized;
    }

    private static string? NormalizeNullable(string? value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return NormalizeText(value, maximum, name);
    }

    private static string NormalizeFolder(int tenantId, string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim().Replace('\\', '/');
        if (raw.Any(char.IsControl) || raw.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("The job folder is invalid.");
        if (!raw.StartsWith("/", StringComparison.Ordinal)) raw = "/" + raw;
        if (!raw.EndsWith("/", StringComparison.Ordinal)) raw += "/";
        var normalized = $"/mcp-operator/{tenantId}{raw}";
        if (normalized.Length > 512) throw new ArgumentException("The job folder is too long.");
        return normalized;
    }

    private static string? NormalizeOptions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Encoding.UTF8.GetByteCount(value) > MaximumDefinitionJsonBytes) throw new ArgumentException("The job options are too large.");
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind is not JsonValueKind.Object || ContainsSensitiveOption(doc.RootElement)) throw new ArgumentException("The job options are invalid.");
            return JsonSerializer.Serialize(doc.RootElement);
        }
        catch (JsonException exception) { throw new ArgumentException("The job options are not valid JSON.", exception); }
    }

    private static bool ContainsSensitiveOption(JsonElement element) => element.EnumerateObject().Any(property =>
        LooksSensitive(property.Name) || (property.Value.ValueKind == JsonValueKind.Object && ContainsSensitiveOption(property.Value)) ||
        (property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object && ContainsSensitiveOption(item))));

    private static string NormalizeIdentifier(string? value, int maximum, string name)
    {
        if (value is not { Length: > 0 } || value.Length > maximum || !(char.IsAsciiLetter(value[0]) || value[0] == '_') || !value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            throw new ArgumentException($"The job {name} is invalid.");
        return value;
    }

    private static bool IsIdentifier(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private sealed record PreparedJobDraft(string Name, string FolderPath, string? Description, string? OptionsJson);
    private sealed record ParameterMetadata(IReadOnlyList<string> Options, string? SecretReference);
    private sealed record LibraryScriptReference(long ScriptVersion, string ScriptContentHash);
    private sealed class OwnedJob(McpOperatorJobRecord ownerValue, JobDefinition jobValue)
    {
        public McpOperatorJobRecord owner { get; } = ownerValue;
        public JobDefinition job { get; } = jobValue;
    }

    private sealed class OwnedRun(McpOperatorJobRunRecord runValue, JobRunRecord projectionValue)
    {
        public McpOperatorJobRunRecord run { get; } = runValue;
        public JobRunRecord projection { get; } = projectionValue;
    }
}
