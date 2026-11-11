using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.API.Services.Jobs;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Infrastructure.Services;
using System.Text.Json;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.SystemPairing;

namespace NetRatel.API.Services.Orchestration;

/// <summary>Rechecks the server-owned ingress grant at the real gateway dispatch boundary.</summary>
public sealed class ManagedOrchestrationInvocationGuard(OrchestratorDbContext db, IServicePrincipalRegistry registry, TimeProvider clock, PairingAuthority authority,
    IJobRunService? runs = null, IJobTargetResolver? targets = null)
{
    public async Task AuthorizeStartAsync(int requestId, JobDefinitionInfo job, ClaimsPrincipal principal, CancellationToken ct, ulong? preparedRunId = null)
    {
        var caller = await registry.ResolvePrincipalAsync(principal, OrchestrationManagedAuthorization.InvokeScope, ct);
        var binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (caller is null || binding is null || caller.Id != binding.ServicePrincipalId ||
            binding.ExecutionId is not null && binding.ExecutionId != preparedRunId?.ToString(CultureInfo.InvariantCulture))
            throw new ManagedOrchestrationGrantUnavailableException("The managed invocation has no current recorded authority.");
        await AuthorizeBindingAsync(binding, job, ct);
    }

    public async Task BindRunAsync(int requestId, JobRunInfo run, CancellationToken ct)
    {
        var binding = await db.Set<ManagedOrchestrationRequestBinding>().SingleAsync(x => x.RequestId == requestId, ct);
        if (binding.ExecutionId is not null && binding.ExecutionId != run.Id.ToString(CultureInfo.InvariantCulture) || binding.SourceSystem != run.StartedBy || binding.TenantId != run.TenantId ||
            binding.AgentId != run.AgentId || binding.JobDefinitionId != run.JobId.ToString(CultureInfo.InvariantCulture))
            throw new ManagedOrchestrationGrantUnavailableException("The managed run differs from its recorded ingress target.");
        binding.ExecutionId = run.Id.ToString(CultureInfo.InvariantCulture);
        await db.SaveChangesAsync(ct);
    }

    public async Task AuthorizeDispatchAsync(JobRunInfo run, JobDefinitionInfo job, CancellationToken ct)
    {
        if (!run.StartedBy.StartsWith("service:", StringComparison.Ordinal)) return;
        var executionId = run.Id.ToString(CultureInfo.InvariantCulture);
        var binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleOrDefaultAsync(x => x.ExecutionId == executionId, ct);
        if (binding is null || binding.SourceSystem != run.StartedBy || binding.TenantId != run.TenantId || binding.AgentId != run.AgentId ||
            binding.JobDefinitionId != run.JobId.ToString(CultureInfo.InvariantCulture))
            throw new ManagedOrchestrationGrantUnavailableException("The managed run has no recorded ingress authority.");
        await AuthorizeBindingAsync(binding, job, ct);
    }

    public async Task AuthorizeAggregateAsync(JobRunInfo parent, JobResolvedTargetPlan plan, string? inputs, CancellationToken ct)
    {
        var executionId = parent.Id.ToString(CultureInfo.InvariantCulture);
        var binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleOrDefaultAsync(row => row.ExecutionId == executionId, ct);
        if (binding is null || binding.AgentId is not null || binding.TargetPlanDigest != plan.ComputeDigest() ||
            parent.TargetPlanDigest != binding.TargetPlanDigest || !parent.IsAggregate || parent.ParentRunId is not null ||
            parent.StartedBy != binding.SourceSystem || runs is null || targets is null)
            throw new ManagedOrchestrationGrantUnavailableException("The managed aggregate differs from its recorded ingress authority.");
        // Hold the definition fence until the native dispatch intent commits. Reuse
        // the accepted protected content/defaults; descriptive edits cannot invalidate
        // an in-flight execution, while target, policy and environment drift can.
        if (db.Database.IsRelational())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Jobs\" SET \"UpdatedAtUtc\" = \"UpdatedAtUtc\" WHERE \"Id\" = {checked((long)parent.JobId)}", ct);
        var currentJob = await new JobDefinitionService(db).GetAsync(parent.JobId, ct)
            ?? throw new ManagedOrchestrationGrantUnavailableException("The managed job is unavailable.");
        await AuthorizeBindingAsync(binding, currentJob, ct);
        var snapshot = await runs.GetPinnedExecutionSnapshotAsync(parent.Id, ct)
            ?? throw new ManagedOrchestrationGrantUnavailableException("The admitted managed definition is unavailable.");
        var currentTarget = currentJob.Target ?? (currentJob.AgentId is { } fixedAgent ? JobTargetDefinition.FromLegacy(fixedAgent) : null);
        static string TargetIdentity(JobTargetDefinition? value) => JsonSerializer.Serialize(value is null ? null :
            value with { AgentIds = value.AgentIds?.Distinct().Order().ToArray() });
        if (snapshot.ComputeDigest() != plan.DefinitionDigest || TargetIdentity(currentTarget) != TargetIdentity(plan.Target) ||
            (currentJob.ExecutionPolicy ?? new()) != plan.ExecutionPolicy || JobExecutionContextPolicy.FromJob(currentJob) != parent.Environment)
            throw new ManagedOrchestrationGrantUnavailableException("The managed target or execution policy changed after admission.");
        var invocation = JobAggregateOrchestrator.ManagedRequest(snapshot.Definition.Job, binding.SourceSystem, inputs);
        var current = await targets.ResolveAsync(binding.TenantId, plan.Target, parent.Environment,
            JobAggregateOrchestrator.TargetParameters(snapshot.Definition, inputs), invocation.RuntimeTarget, null, ct);
        var admitted = plan.HaGroupSelection?.Candidates ?? plan.Nodes;
        if (current.GroupRevision != plan.GroupRevision || !current.Nodes.Select(node => node.AgentId).ToHashSet().SetEquals(admitted.Select(node => node.AgentId)))
            throw new ManagedOrchestrationGrantUnavailableException("The managed target membership changed after admission.");
    }

    private async Task AuthorizeBindingAsync(ManagedOrchestrationRequestBinding binding, JobDefinitionInfo job, CancellationToken ct)
    {
        var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.ServicePrincipalId, ct);
        if (row is null || row.Status != "active" || row.TenantId != binding.TenantId || row.LinkId != binding.LinkId ||
            row.LinkRevision != binding.LinkRevision || row.GrantHash != binding.GrantHash || row.PeerInstanceId != binding.PeerInstanceId ||
            row.PeerTenantId != binding.PeerTenantId || job.TenantId != binding.TenantId || binding.AgentId is not null && job.AgentId != binding.AgentId ||
            job.Id.ToString(CultureInfo.InvariantCulture) != binding.JobDefinitionId)
            throw new ManagedOrchestrationGrantUnavailableException("The managed target or approved grant changed before dispatch.");
        var constraints = await authority.ResourcesAsync(row.TenantId, ct);
        if (binding.AgentId is { } agent && !constraints.ResourceIds.Contains(agent.ToString("D"), StringComparer.Ordinal) ||
            !constraints.RequestDefinitionIds.Contains(binding.JobDefinitionId, StringComparer.Ordinal))
            throw new ManagedOrchestrationGrantUnavailableException("The managed target is outside its current approved grant.");
        var jobId = checked((long)job.Id);
        if (!await db.Jobs.AsNoTracking().AnyAsync(x => x.Id == jobId && x.TenantId == binding.TenantId && (binding.AgentId == null || x.AgentId == binding.AgentId), ct))
            throw new ManagedOrchestrationGrantUnavailableException("The recorded job target changed before dispatch.");
        var now = clock.GetUtcNow();
        var credential = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ServicePrincipalId == row.Id && x.CredentialRevision == row.CurrentCredentialRevision, ct);
        if (credential is null || credential.Status != "active" || credential.ExpiresAtUtc <= now ||
            !await registry.CanIssueScopesAsync(new AuthenticatedServiceClient(row, credential), [OrchestrationManagedAuthorization.InvokeScope], ct) ||
            binding.AgentId is not null && !await db.Agents.AsNoTracking().AnyAsync(x => x.Id == binding.AgentId && x.TenantId == binding.TenantId && x.IsEnabled &&
                x.Status == AgentStatus.Active && x.RevokedAtUtc == null && x.DeletedAtUtc == null && x.SupersededAtUtc == null && x.SupersededByAgentId == null, ct))
            throw new ManagedOrchestrationGrantUnavailableException("The managed grant is no longer available at dispatch.");
    }
}

public sealed class ManagedOrchestrationGrantUnavailableException(string message) : InvalidOperationException(message);
