using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetRatel.API.Gateway;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

public sealed class JobAggregateOptions
{
    public const string SectionName = "Jobs:Aggregate";
    // Explicit per-parent infrastructure capacity; the configured Thread Count stays in the plan.
    public int MaximumActiveNodes { get; set; } = 100;
}

/// <summary>
/// Resolves one complete plan, then persists a parent, a queue and dispatch gates. A turn only
/// enqueues one existing native workflow; the native authority continues to own its steps and receipts.
/// </summary>
public sealed class JobAggregateOrchestrator(
    OrchestratorDbContext db, IJobDefinitionService definitions, IJobRunService runs,
    IJobExecutionSnapshotCapture snapshots, IJobTargetResolver resolver, JobAuthorityIdGenerator ids,
    IJobAggregateWakeup wakeup, IServiceScopeFactory scopes, IEffectiveAccessService access,
    TimeProvider clock, IOptions<JobAggregateOptions> options, IHttpContextAccessor? http = null,
    IExecutionAdmissionStore? admissions = null, IKeyedExecutionAuthority? keyedAuthority = null,
    IMcpAggregateExecutionAuthority? mcpAuthority = null, IRequestService? requests = null) : IJobAggregatePump
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<JobResolvedTargetPlan> PlanAsync(ulong jobId, RunJobRequest request, CancellationToken ct)
    {
        var job = await definitions.GetAsync(jobId, ct) ?? throw new KeyNotFoundException("The job definition is unavailable.");
        var snapshot = await snapshots.CaptureAsync(jobId, job.TenantId ?? 0, ct);
        return await ResolvePlanAsync(snapshot, request, ct);
    }

    public async Task<JobResolvedTargetPlan> ResolvePlanAsync(PinnedJobExecutionSnapshot snapshot,
        RunJobRequest request, CancellationToken ct)
    {
        snapshot.Validate(snapshot.Definition.Job.TenantId ?? 0);
        if (!string.IsNullOrWhiteSpace(request.ClientIdentityOverride))
            throw new InvalidOperationException("A job target cannot be replaced by ClientIdentityOverride; use the declared typed runtime target.");
        var job = snapshot.Definition.Job;
        var target = job.Target ?? (job.AgentId is { } agent ? JobTargetDefinition.FromLegacy(agent) :
            throw new InvalidOperationException("A complete current target is required."));
        target.Validate();
        var parameters = TargetParameters(snapshot.Definition, request.InputsJson);
        var result = await resolver.ResolveAsync(job.TenantId!.Value, target,
            request.Environment ?? JobExecutionContextPolicy.FromJob(job), parameters, request.RuntimeTarget,
            authorizedAgentIds: null, cancellationToken: ct);
        var plan = new JobResolvedTargetPlan(1, job.TenantId.Value, job.Id, snapshot.ComputeDigest(), target,
            result.GroupRevision, result.Bindings, result.Nodes, job.ExecutionPolicy ?? new());
        RequireExpectedPlan(request, plan);
        return plan;
    }

    public async Task<JobRunInfo> StartFreshAsync(ulong jobId, RunJobRequest request, CancellationToken ct,
        string? occurrenceId = null)
    {
        var key = occurrenceId is null ? null : AdmissionKey("schedule", jobId.ToString(), occurrenceId);
        if (key is not null && await ExistingByKeyAsync(key, ct) is { } existing) return existing;
        var job = await definitions.GetAsync(jobId, ct) ?? throw new KeyNotFoundException("The job definition is unavailable.");
        var snapshot = await snapshots.CaptureAsync(jobId, job.TenantId ?? 0, ct);
        var plan = await ResolvePlanAsync(snapshot, request, ct);
        var principal = occurrenceId is null ? http?.HttpContext?.User : JobScheduleAuthority.Read(job.OptionsJson);
        if (occurrenceId is not null && principal is null && job.Target is null && job.AgentId is { } fixedAgent &&
            request.RuntimeTarget is null && plan.Nodes.Count == 1 && plan.Nodes[0].AgentId == fixedAgent)
        {
            // Historical fixed schedules did not record a configuring principal. Preserve only
            // their existing exact native target authority; never lend it to a new target mode.
            await RequireActiveTargetAsync(plan.TenantId, plan.JobId, fixedAgent, ct);
            return await PersistAsync(ids.Next(), snapshot, request, plan, key, null, null,
                "legacy-schedule", null, null, ct);
        }
        var authority = CaptureAuthority(principal);
        await RequirePrincipalAsync(authority.PrincipalId, authority.CredentialId, plan, ct);
        return await PersistAsync(ids.Next(), snapshot, request, plan, key, authority.PrincipalId,
            authority.CredentialId, occurrenceId is null ? "manual" : "schedule", null, null, ct);
    }

    public async Task<JobRunInfo> StartReviewedAsync(McpOperatorJobExecutionSnapshot snapshot,
        RunJobRequest request, CancellationToken ct)
    {
        if (snapshot.TargetPlan is not { } plan || snapshot.ReviewIdempotencyId is not { } idempotency ||
            idempotency == Guid.Empty || request.StartedBy != $"mcp-reviewed:{snapshot.Subject}" || mcpAuthority is null)
            throw new ExecutionAdmissionDeniedException("The reviewed complete target plan is unavailable.");
        var key = McpAggregateAdmissionKey.For(plan.TenantId, snapshot.Subject, idempotency);
        if (await ExistingByKeyAsync(key, ct) is { } existing)
        {
            if (existing.TargetPlanDigest != plan.ComputeDigest() || existing.DefinitionDigest != snapshot.ComputeDefinitionDigest())
                throw new ExecutionAdmissionDeniedException("The reviewed admission differs from its committed plan.");
            return existing;
        }
        ValidateFrozenPlan(snapshot.Definition.Job, snapshot.ComputeDefinitionDigest(), plan);
        RequireExpectedPlan(request, plan);
        foreach (var node in plan.Nodes) await mcpAuthority.RequireAllowedAsync(snapshot, node.AgentId, ct);
        // Reviewed working-directory/timeout material remains in the existing protected MCP snapshot.
        var pin = new PinnedJobExecutionSnapshot(snapshot.Definition, snapshot.Scripts.Select(script =>
            new PinnedJobScriptSnapshot(script.Script, script.Parameters, script.ContentHash)).ToArray());
        return await PersistAsync(ids.Next(), pin, request, plan, key, null, null, "mcp", snapshot, null, ct);
    }

    public async Task<JobResolvedTargetPlan> PlanRequestAsync(ulong jobId, string? inputsJson, Guid protectedAgentId, CancellationToken ct)
    {
        var definition = await definitions.GetAsync(jobId, ct) ?? throw new KeyNotFoundException("The requested job is unavailable.");
        var runtime = RequestTargetInput(definition.Target, protectedAgentId);
        var plan = await PlanAsync(jobId, new("request:preview", inputsJson, null) { RuntimeTarget = runtime }, ct);
        RequireSingularRequestPlan(plan, protectedAgentId);
        return plan;
    }

    public async Task<JobRunInfo> StartRequestAsync(RequestExecutionMaterial material, CancellationToken ct)
    {
        var key = $"request:{material.TenantId}:{material.RequestId}";
        if (await ExistingByKeyAsync(key, ct) is { } existing)
        {
            var original = await runs.GetResolvedTargetPlanAsync(existing.Id, ct)
                ?? throw new CanonicalRequestAdmissionRejectedException("The original request target plan is unavailable.");
            RequireSingularRequestPlan(original, material.AgentId);
            if (existing.JobId != material.JobDefinitionId || existing.TenantId != material.TenantId || existing.StartedBy != material.SourceSystem)
                throw new CanonicalRequestAdmissionRejectedException("The accepted request differs from its immutable job binding.");
            return existing;
        }
        try
        {
            var snapshot = await snapshots.CaptureAsync(material.JobDefinitionId, material.TenantId, ct);
            var request = new RunJobRequest(material.SourceSystem, material.InputsJson, null)
                { RuntimeTarget = RequestTargetInput(snapshot.Definition.Job.Target, material.AgentId) };
            var plan = await ResolvePlanAsync(snapshot, request, ct);
            RequireSingularRequestPlan(plan, material.AgentId);
            var authority = CaptureAuthority(http?.HttpContext?.User);
            await RequirePrincipalAsync(authority.PrincipalId, authority.CredentialId, plan, ct);
            var parentId = await ExecutionLifecycleOutbox.ReserveCanonicalRequestRunAsync(db, material.RequestId, ids.Next, ct);
            return await PersistAsync(parentId, snapshot, request, plan, key, authority.PrincipalId,
                authority.CredentialId, "request", null, null, ct, material.RequestId);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            if (await ExistingByKeyAsync(key, ct) is { } committed) return committed;
            throw new CanonicalRequestAdmissionRejectedException("The protected singular request was rejected before job acceptance.", error);
        }
    }

    public async Task<JobRunInfo> StartKeyedAsync(KeyedJobAdmissionRequest request, CancellationToken ct)
    {
        if (admissions is null || keyedAuthority is null)
            throw new ExecutionAdmissionDeniedException("Canonical aggregate keyed admission is unavailable.");
        request.Identity.Validate();
        request.Snapshot.Validate(request.Identity.TenantId);
        var existing = await admissions.FindByIdentityAsync(request.Identity, ExecutionAdmissionKind.Job, ct);
        if (existing is not null)
        {
            var original = JsonSerializer.Deserialize<KeyedJobAdmissionRequest>(existing.PayloadJson)
                ?? throw new ExecutionAdmissionDeniedException("The original keyed admission is unavailable.");
            var expected = request.Request with { StartedBy = $"flow-keyed:{request.Identity.ActionKey:N}" };
            if (original.Snapshot.ComputeDigest() != request.Snapshot.ComputeDigest() ||
                JsonSerializer.Serialize(original.Request) != JsonSerializer.Serialize(expected) ||
                original.ActionDeadlineAtUtc != request.ActionDeadlineAtUtc ||
                request.TargetPlan is { } provided && provided.ComputeDigest() != original.TargetPlan?.ComputeDigest())
                throw new ExecutionAdmissionDeniedException("The action redelivery differs from its immutable definition, inputs or target plan.");
            return await MaterializeKeyedAsync(existing, request.FlowLease, ct);
        }
        var plan = request.TargetPlan ?? await ResolvePlanAsync(request.Snapshot, request.Request, ct);
        ValidateFrozenPlan(request.Snapshot.Definition.Job, request.Snapshot.ComputeDigest(), plan);
        await keyedAuthority.RequireTargetsAllowedAsync(request.Identity, request.Snapshot.Definition.Job,
            plan.Nodes.Select(node => node.AgentId).ToArray(), ct);
        var pinned = request with { TargetPlan = plan, Request = request.Request with { StartedBy = $"flow-keyed:{request.Identity.ActionKey:N}" } };
        var binding = await admissions.ReserveAsync(new(request.Identity, ExecutionAdmissionKind.Job, JsonSerializer.Serialize(pinned)),
            () => ids.Next().ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        return await MaterializeKeyedAsync(binding, request.FlowLease, ct);
    }

    private async Task<JobRunInfo> MaterializeKeyedAsync(ExecutionAdmissionBinding binding,
        NetRatel.Application.Flows.FlowRunLease? lease, CancellationToken ct)
    {
        var submitted = JsonSerializer.Deserialize<KeyedJobAdmissionRequest>(binding.PayloadJson)
            ?? throw new ExecutionAdmissionDeniedException("The protected keyed submission is unavailable.");
        var plan = submitted.TargetPlan ?? throw new ExecutionAdmissionDeniedException("The protected aggregate target plan is unavailable.");
        ValidateFrozenPlan(submitted.Snapshot.Definition.Job, submitted.Snapshot.ComputeDigest(), plan);
        if (binding.Identity != submitted.Identity || binding.Kind != ExecutionAdmissionKind.Job ||
            submitted.Request.StartedBy != $"flow-keyed:{binding.Identity.ActionKey:N}")
            throw new ExecutionAdmissionDeniedException("The aggregate action identity differs from its immutable submission.");
        await keyedAuthority!.RequireTargetsAllowedAsync(submitted.Identity, submitted.Snapshot.Definition.Job,
            plan.Nodes.Select(node => node.AgentId).ToArray(), ct);
        var parent = ulong.Parse(binding.ExecutionId, System.Globalization.CultureInfo.InvariantCulture);
        if (await runs.GetAsync(parent, ct) is { } existing)
        {
            if (!existing.IsAggregate || existing.TargetPlanDigest != plan.ComputeDigest() ||
                existing.DefinitionDigest != submitted.Snapshot.ComputeDigest() || existing.StartedBy != submitted.Request.StartedBy)
                throw new ExecutionAdmissionDeniedException("The aggregate receipt differs from its admitted action.");
            return existing;
        }
        await keyedAuthority.RequireFirstDispatchAsync(submitted.Identity, lease, ct);
        return await PersistAsync(parent, submitted.Snapshot, submitted.Request, plan,
            AdmissionKey("flow", submitted.Identity.TenantId.ToString(), submitted.Identity.ActionKey.ToString("N")),
            submitted.Identity.PrincipalId, submitted.Identity.IntegrationCredentialId, "flow", null,
            submitted.ActionDeadlineAtUtc, ct);
    }

    private async Task<JobRunInfo> PersistAsync(ulong parentId, PinnedJobExecutionSnapshot snapshot,
        RunJobRequest request, JobResolvedTargetPlan plan, string? admissionKey, string? principalId,
        string? credentialId, string sourceKind, McpOperatorJobExecutionSnapshot? mcpSnapshot,
        DateTimeOffset? actionDeadline, CancellationToken ct, int? sourceRequestId = null)
    {
        // An advisory key also covers the absent-row case for same-occurrence reservations.
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql())
        {
            var fence = admissionKey ?? $"aggregate:{parentId}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockKey(fence)})", ct);
        }
        if (admissionKey is not null && await ExistingByKeyAsync(admissionKey, ct) is { } existing)
        {
            if (existing.TargetPlanDigest != plan.ComputeDigest() || existing.DefinitionDigest != plan.DefinitionDigest)
                throw new ExecutionAdmissionDeniedException("The occurrence already owns a different immutable admission.");
            return existing;
        }
        if (mcpSnapshot is not null)
        {
            var reviewedRoute = mcpSnapshot.ReviewedAuthority?.Route;
            var payloadHash = new McpReviewedJobSubmission(mcpSnapshot, request).ComputePayloadHash();
            if (reviewedRoute is null || mcpSnapshot.ReviewIdempotencyId is not { } receiptId ||
                !await db.McpOperatorIdempotencyRecords.AsNoTracking().AnyAsync(receipt => receipt.Id == receiptId &&
                    receipt.Outcome == McpOperatorIdempotencyOutcome.Pending && receipt.TenantId == plan.TenantId &&
                    receipt.Subject == mcpSnapshot.Subject && receipt.ClientId == mcpSnapshot.ClientId &&
                    receipt.Operation == "netratel_job_runs/start" && receipt.OperationFamily == McpOperatorOperationFamily.JobExecution &&
                    receipt.AgentId == reviewedRoute.AgentId && receipt.PayloadHash == payloadHash, ct))
                throw new ExecutionAdmissionDeniedException("The reviewed target admission is no longer pending for this original protected submission.");
        }
        if (mcpSnapshot is null) runs.BindPinnedExecutionSnapshot(parentId, snapshot);
        else runs.BindMcpExecutionSnapshot(parentId, mcpSnapshot);
        var now = clock.GetUtcNow();
        var policy = JobExecutionRuntimePolicy.FromJobAndRequest(snapshot.Definition.Job, request);
        var environment = request.Environment ?? JobExecutionContextPolicy.FromJob(snapshot.Definition.Job);
        await runs.UpsertRunAsync(new(parentId, plan.JobId, plan.TenantId, string.Empty,
            string.IsNullOrWhiteSpace(request.StartedBy) ? "aggregate:manual" : request.StartedBy.Trim(),
            JobRunState.Pending, 0, now, null, null, null, request.InputsJson,
            JobExecutionContextPolicy.MergeOptionsJson(JobExecutionRuntimePolicy.MergeOptionsJson(snapshot.Definition.Job.OptionsJson, policy), environment),
            AgentId: null, Environment: environment) { IsAggregate = true, TargetPlan = plan }, ct);
        var row = await db.JobRuns.SingleAsync(row => row.Id == checked((long)parentId), ct);
        if (!row.IsAggregate || row.DefinitionDigest != plan.DefinitionDigest || row.TargetPlanDigest != plan.ComputeDigest())
            throw new InvalidOperationException("The protected parent differs from its admitted complete target plan.");
        db.Set<JobAggregateControlRecord>().Add(new()
        {
            ParentRunId = row.Id, AdmissionKey = admissionKey, PrincipalId = principalId,
            IntegrationCredentialId = credentialId, SourceKind = sourceKind, ActionDeadlineAtUtc = actionDeadline
        });
        db.Set<JobRunNodeRecord>().AddRange(plan.Nodes.Select((node, index) => new JobRunNodeRecord
        {
            ParentRunId = row.Id, TenantId = plan.TenantId, AgentId = node.AgentId, Ordinal = index + 1,
            State = JobRunNodeState.NeverStarted, TargetJson = JsonSerializer.Serialize(node)
        }));
        await db.SaveChangesAsync(ct);
        if (sourceRequestId is { } requestId)
        {
            await ExecutionLifecycleOutbox.RecordCanonicalRequestBindingAsync(db, requestId,
                (await runs.GetAsync(parentId, ct))!, now, ct);
            await db.SaveChangesAsync(ct);
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
            await transaction.DisposeAsync();
        }
        await wakeup.WakeAsync(parentId, ct);
        return (await runs.GetAsync(parentId, ct))!;
    }

    public async Task<JobAggregatePumpResult> PumpAsync(ulong parentRunId, CancellationToken ct)
    {
        var parentId = checked((long)parentRunId);
        // The existing native recovery worker reconciles each child against its original
        // dispatch deadline. Parent turns only observe those committed outcomes; they never
        // acquire native owners while holding the aggregate gate or replay a claimed child.
        db.ChangeTracker.Clear();
        long? dispatch = null;
        var cancellations = new List<long>();
        var active = true;
        var immediate = false;
        await using (var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null)
        {
            var control = await LockControlAsync(parentId, ct);
            if (control is null || control.TerminalReadyAtUtc is not null) return new(false);
            var parent = await db.JobRuns.SingleAsync(row => row.Id == parentId && row.IsAggregate, ct);
            var plan = ReadPlan(parent);
            var nodes = await db.Set<JobRunNodeRecord>().Where(node => node.ParentRunId == parentId)
                .OrderBy(node => node.Ordinal).ToArrayAsync(ct);
            var nativeIds = nodes.Where(node => node.NativeRunId is not null).Select(node => node.NativeRunId!.Value).ToArray();
            var native = await db.JobRuns.AsNoTracking().Where(run => nativeIds.Contains(run.Id)).ToDictionaryAsync(run => run.Id, ct);
            var now = clock.GetUtcNow();
            foreach (var node in nodes.Where(node => node.State is JobRunNodeState.Claimed or JobRunNodeState.Running))
            {
                if (node.NativeRunId is not { } childId || !native.TryGetValue(childId, out var child))
                    throw new InvalidOperationException("A claimed node lost its durable native binding.");
                if (child.ParentRunId != parentId || child.AgentId != node.AgentId || child.TenantId != node.TenantId)
                    throw new InvalidOperationException("A native node binding differs from its parent plan.");
                node.State = NodeState((JobRunState)child.Status);
                node.StartedAtUtc = child.StartedAtUtc;
                node.CompletedAtUtc = child.CompletedAtUtc;
                node.Reason = Bound(child.Error);
                if (node.State == JobRunNodeState.Unknown)
                    CloseGate(control, JobRunNodeReasons.StoppedAfterUnknownExecution);
                else if (IsNodeTerminal(node.State) && node.State != JobRunNodeState.Succeeded &&
                    plan.ExecutionPolicy.NodeFailurePolicy == JobNodeFailurePolicy.StopRemaining)
                    CloseGate(control, JobRunNodeReasons.StoppedAfterNodeFailure);
            }
            var deadline = ActionDeadlineReached(control, nodes, now);
            if (deadline) CloseGate(control, "ParentActionDeadline");
            if (control.CancellationRequestedAtUtc is not null) CloseGate(control, JobRunNodeReasons.ParentCancelled);
            if (control.DispatchClosed)
            {
                foreach (var node in nodes.Where(node => node.State == JobRunNodeState.NeverStarted))
                {
                    node.State = control.CancellationRequestedAtUtc is not null ? JobRunNodeState.Cancelled :
                        deadline ? JobRunNodeState.TimedOut : JobRunNodeState.Skipped;
                    node.Reason = control.GateReason;
                    node.CompletedAtUtc = now;
                }
                if (control.CancellationRequestedAtUtc is not null || deadline)
                    cancellations.AddRange(nodes.Where(node => node.State is JobRunNodeState.Claimed or JobRunNodeState.Running)
                        .Select(node => node.NativeRunId!.Value));
            }
            var running = nodes.Count(node => node.State is JobRunNodeState.Claimed or JobRunNodeState.Running);
            var capacity = EffectiveConcurrency(plan);
            if (!control.DispatchClosed && running < capacity && nodes.FirstOrDefault(node => node.State == JobRunNodeState.NeverStarted) is { } next)
            {
                var childId = ids.Next();
                await PrepareNativeIntentAsync(parent, plan, next, childId, now, ct);
                next.NativeRunId = checked((long)childId);
                next.State = JobRunNodeState.Claimed;
                next.ClaimedAtUtc = now;
                dispatch = checked((long)childId);
                running++;
                immediate = running < capacity && nodes.Any(node => node.State == JobRunNodeState.NeverStarted);
            }
            parent.Status = nodes.Any(node => !IsNodeTerminal(node.State)) ? (int)JobRunState.Running :
                (int)TerminalState(nodes, control);
            parent.StartedAtUtc ??= nodes.Any(node => node.State != JobRunNodeState.NeverStarted) ? now : null;
            active = !IsTerminal((JobRunState)parent.Status);
            if (!active)
            {
                parent.CompletedAtUtc = now;
                parent.Error = control.GateReason ?? (nodes.Any(node => node.State != JobRunNodeState.Succeeded) ? "One or more required nodes did not succeed." : null);
            }
            control.Revision++;
            await db.SaveChangesAsync(ct);
            await NetRatel.Infrastructure.Services.ExecutionActivityNotifications.NotifyAsync(db, parent.TenantId, parentRunId, null, ct);
            if (!active) await MarkParentTerminalAsync(parentRunId, control, nodes, now, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        // The claim, native identity, protected snapshot and gate are committed before any gateway IO.
        foreach (var nativeId in cancellations)
        {
            await using var childScope = scopes.CreateAsyncScope();
            await childScope.ServiceProvider.GetRequiredService<IAkkaJobAuthorityService>()
                .CancelAsync(checked((ulong)nativeId), "aggregate-parent-cancelled-or-deadline", ct);
        }
        if (dispatch is { } selected)
        {
            await using var childScope = scopes.CreateAsyncScope();
            await childScope.ServiceProvider.GetRequiredService<IAkkaJobAuthorityService>()
                .StartAggregateNodeAsync(checked((ulong)selected), ct);
            immediate = true;
        }
        return new(active, immediate);
    }

    private async Task PrepareNativeIntentAsync(JobRunRecord parent, JobResolvedTargetPlan plan,
        JobRunNodeRecord node, ulong childId, DateTimeOffset now, CancellationToken ct)
    {
        var pin = await runs.GetPinnedExecutionSnapshotAsync(checked((ulong)parent.Id), ct);
        var reviewed = await runs.GetMcpExecutionSnapshotAsync(checked((ulong)parent.Id), ct);
        if (reviewed is not null)
            runs.BindMcpExecutionSnapshot(childId, reviewed with { Definition = ProjectDefinition(reviewed.Definition, node.AgentId) });
        else if (pin is not null)
            runs.BindPinnedExecutionSnapshot(childId, pin with { Definition = ProjectDefinition(pin.Definition, node.AgentId) });
        else throw new InvalidOperationException("The protected parent definition is unavailable; no native work was admitted.");
        var target = plan.Nodes.Single(target => target.AgentId == node.AgentId);
        runs.BindNodeExecutionContext(childId, target);
        var child = await runs.UpsertRunAsync(new(childId, checked((ulong)parent.JobId), parent.TenantId,
            string.Empty, $"aggregate-node:{parent.Id}", JobRunState.Pending, 0, now, null, null, null,
            await runs.GetExecutionInputsAsync(checked((ulong)parent.Id), ct),
            await runs.GetExecutionOptionsAsync(checked((ulong)parent.Id), ct), node.AgentId, parent.Environment,
            target.DisplayName, target.Hostname, target.Name), ct);
        var row = await db.JobRuns.SingleAsync(row => row.Id == checked((long)childId), ct);
        row.ParentRunId = parent.Id;
        row.DefinitionDigest = parent.DefinitionDigest;
        row.TargetPlanDigest = parent.TargetPlanDigest;
        db.Set<JobRunControlRecord>().Add(new() { RunId = checked((long)child.Id) });
        // The PostgreSQL tuple FK is immediate: establish the child's exact tenant/agent/parent
        // identity before the queue row references it. Both saves share the same claim transaction.
        await db.SaveChangesAsync(ct);
    }

    public async Task RequireNodeAuthorityAsync(JobRunInfo native, CancellationToken ct)
    {
        if (native.ParentRunId is not { } parentId || native.AgentId is not { } agentId)
            throw new ExecutionAdmissionDeniedException("The native execution has no aggregate node binding.");
        var parent = await db.JobRuns.AsNoTracking().SingleAsync(row => row.Id == checked((long)parentId), ct);
        var control = await db.Set<JobAggregateControlRecord>().AsNoTracking().SingleAsync(row => row.ParentRunId == parent.Id, ct);
        var plan = ReadPlan(parent);
        if (parent.TenantId != native.TenantId || parent.JobId != checked((long)native.JobId) ||
            native.DefinitionDigest != parent.DefinitionDigest || native.TargetPlanDigest != parent.TargetPlanDigest ||
            !await db.Set<JobRunNodeRecord>().AsNoTracking().AnyAsync(node => node.ParentRunId == parent.Id &&
                node.AgentId == agentId && node.NativeRunId == checked((long)native.Id), ct))
            throw new ExecutionAdmissionDeniedException("The native execution differs from its admitted target plan.");
        if (control.CancellationRequestedAtUtc is not null || control.ActionDeadlineAtUtc is { } end && clock.GetUtcNow() >= end)
            throw new ExecutionAdmissionDeniedException("The aggregate parent no longer admits native dispatch.");
        await RequireActiveTargetAsync(plan.TenantId, plan.JobId, agentId, ct);
        if (control.SourceKind == "flow")
        {
            var binding = admissions is null ? null : await admissions.FindByExecutionAsync(ExecutionAdmissionKind.Job, parent.Id.ToString(), ct);
            var submitted = binding is null ? null : JsonSerializer.Deserialize<KeyedJobAdmissionRequest>(binding.PayloadJson);
            if (submitted is null || keyedAuthority is null || submitted.TargetPlan?.ComputeDigest() != parent.TargetPlanDigest ||
                submitted.Snapshot.ComputeDigest() != parent.DefinitionDigest)
                throw new ExecutionAdmissionDeniedException("The canonical source action binding is unavailable.");
            await keyedAuthority.RequireTargetsAllowedAsync(submitted.Identity, submitted.Snapshot.Definition.Job, [agentId], ct);
            await keyedAuthority.RequireContinuationAsync(submitted.Identity, ct);
        }
        else if (control.SourceKind == "mcp")
        {
            var snapshot = await runs.GetMcpExecutionSnapshotAsync(parentId, ct);
            if (snapshot is null || mcpAuthority is null || snapshot.TargetPlan?.ComputeDigest() != parent.TargetPlanDigest)
                throw new ExecutionAdmissionDeniedException("The reviewed aggregate authority is unavailable.");
            await mcpAuthority.RequireAllowedAsync(snapshot, agentId, ct);
        }
        else if (control.SourceKind == "legacy-schedule")
        {
            var original = await runs.GetPinnedExecutionSnapshotAsync(parentId, ct);
            if (original?.Definition.Job.Target is not null || original?.Definition.Job.AgentId != agentId ||
                original.ComputeDigest() != parent.DefinitionDigest || plan.Nodes.Count != 1 ||
                plan.Nodes[0].AgentId != agentId || plan.Target.Source != JobTargetSource.ExplicitNodes ||
                control.PrincipalId is not null || control.IntegrationCredentialId is not null)
                throw new ExecutionAdmissionDeniedException("The historical fixed schedule cannot authorize this target policy.");
        }
        else await RequirePrincipalAsync(control.PrincipalId, control.IntegrationCredentialId, plan, ct, [agentId]);
    }

    /// <summary>
    /// Accepts a native terminal fact under its existing owner transaction. The aggregate row
    /// lock orders this gate against claims: a claim before this lock counts as in-flight.
    /// No parent actor ask or native-owner acquisition occurs while holding the gate.
    /// </summary>
    public async Task AcceptNodeTerminalAsync(JobRunInfo native, CancellationToken ct)
    {
        if (native.ParentRunId is not { } parentId) return;
        if (!IsTerminal(native.Status) || db.Database.IsNpgsql() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Aggregate terminal acceptance requires the native owner's committed-intent transaction.");
        var control = await LockControlAsync(checked((long)parentId), ct)
            ?? throw new InvalidOperationException("The native node lost its durable aggregate gate.");
        if (control.TerminalReadyAtUtc is not null) return;
        var parent = await db.JobRuns.AsNoTracking().SingleAsync(row => row.Id == checked((long)parentId), ct);
        var plan = ReadPlan(parent);
        var node = await db.Set<JobRunNodeRecord>().SingleAsync(row => row.ParentRunId == parent.Id &&
            row.NativeRunId == checked((long)native.Id) && row.AgentId == native.AgentId && row.TenantId == native.TenantId, ct);
        if (parent.DefinitionDigest != native.DefinitionDigest || parent.TargetPlanDigest != native.TargetPlanDigest)
            throw new InvalidOperationException("The native terminal fact differs from its protected aggregate plan.");
        node.State = NodeState(native.Status); node.StartedAtUtc = native.StartedAtUtc;
        node.CompletedAtUtc = native.CompletedAtUtc; node.Reason = Bound(native.Error);
        if (native.Status == JobRunState.Unknown) CloseGate(control, JobRunNodeReasons.StoppedAfterUnknownExecution);
        else if (native.Status != JobRunState.Succeeded && plan.ExecutionPolicy.NodeFailurePolicy == JobNodeFailurePolicy.StopRemaining)
            CloseGate(control, JobRunNodeReasons.StoppedAfterNodeFailure);
        var now = clock.GetUtcNow();
        var allNodes = await db.Set<JobRunNodeRecord>().Where(row => row.ParentRunId == parent.Id).ToArrayAsync(ct);
        var deadline = ActionDeadlineReached(control, allNodes, now);
        if (deadline) CloseGate(control, "ParentActionDeadline");
        if (control.CancellationRequestedAtUtc is not null) CloseGate(control, JobRunNodeReasons.ParentCancelled);
        if (control.DispatchClosed)
        {
            var queued = await db.Set<JobRunNodeRecord>().Where(row => row.ParentRunId == parent.Id && row.State == JobRunNodeState.NeverStarted).ToArrayAsync(ct);
            foreach (var pending in queued)
            {
                pending.State = control.CancellationRequestedAtUtc is not null ? JobRunNodeState.Cancelled : deadline ? JobRunNodeState.TimedOut : JobRunNodeState.Skipped;
                pending.Reason = control.GateReason; pending.CompletedAtUtc = now;
            }
        }
        control.Revision++;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> CancelAsync(ulong parentRunId, string reason, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var control = await LockControlAsync(checked((long)parentRunId), ct);
        if (control is null || control.TerminalReadyAtUtc is not null) return false;
        control.CancellationRequestedAtUtc ??= clock.GetUtcNow();
        CloseGate(control, JobRunNodeReasons.ParentCancelled);
        control.Revision++;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        await wakeup.WakeAsync(parentRunId, ct);
        return true;
    }

    private async Task MarkParentTerminalAsync(ulong parent, JobAggregateControlRecord control,
        IReadOnlyList<JobRunNodeRecord> nodes, DateTimeOffset now, CancellationToken ct)
    {
        var detail = await runs.GetDetailsAsync(parent, ct) ?? throw new InvalidOperationException("The aggregate terminal projection disappeared.");
        var projections = await JobRunNodeProjection.ReadAsync(db, checked((long)parent), ct);
        control.TerminalReadyAtUtc = now;
        control.TerminalResultHash = JobRunProjectionHash.ComputeAggregate(detail, projections);
        await ExecutionLifecycleOutbox.RecordJobAsync(db, detail, control.TerminalResultHash, now, ct,
            pinnedSnapshot: await runs.GetPinnedExecutionSnapshotAsync(parent, ct));
        if (requests is not null)
            await ExecutionLifecycleOutbox.ProjectAcceptedRequestTerminalsAsync(db, detail, control.TerminalResultHash, requests, ct);
        await db.SaveChangesAsync(ct);
    }

    public int EffectiveConcurrency(JobResolvedTargetPlan plan) =>
        Math.Min(plan.Nodes.Count, Math.Min(plan.ExecutionPolicy.ThreadCount, Math.Clamp(options.Value.MaximumActiveNodes, 1, 100)));

    private async Task<JobAggregateControlRecord?> LockControlAsync(long parent, CancellationToken ct) =>
        await (db.Database.IsNpgsql() ? db.Set<JobAggregateControlRecord>().FromSqlInterpolated($"SELECT * FROM \"JobAggregateControls\" WHERE \"ParentRunId\"={parent} FOR UPDATE") :
            db.Set<JobAggregateControlRecord>().Where(row => row.ParentRunId == parent)).SingleOrDefaultAsync(ct);

    private async Task<JobRunInfo?> ExistingByKeyAsync(string key, CancellationToken ct)
    {
        var parent = await db.Set<JobAggregateControlRecord>().AsNoTracking()
            .Where(row => row.AdmissionKey == key).Select(row => (long?)row.ParentRunId).SingleOrDefaultAsync(ct);
        return parent is { } id ? await runs.GetAsync(checked((ulong)id), ct) : null;
    }

    public static JobDefinitionDetails ProjectDefinition(JobDefinitionDetails source, Guid agentId) =>
        source with { Job = source.Job with { AgentId = agentId, Target = null } };

    public static JobResolvedTargetPlan ReadPlan(JobRunRecord parent)
    {
        var plan = JsonSerializer.Deserialize<JobResolvedTargetPlan>(parent.TargetPlanJson ?? "null", Json)
            ?? throw new InvalidOperationException("The admitted target plan is unavailable.");
        if (!parent.IsAggregate || parent.ParentRunId is not null || parent.AgentId is not null ||
            plan.JobId != checked((ulong)parent.JobId) || plan.TenantId != parent.TenantId ||
            plan.DefinitionDigest != parent.DefinitionDigest || plan.ComputeDigest() != parent.TargetPlanDigest)
            throw new InvalidOperationException("The admitted target plan differs from its immutable parent binding.");
        return plan;
    }

    private static void ValidateFrozenPlan(JobDefinitionInfo job, string digest, JobResolvedTargetPlan plan)
    {
        plan.Validate();
        var target = job.Target ?? (job.AgentId is { } agent ? JobTargetDefinition.FromLegacy(agent) : null);
        if (job.Id != plan.JobId || job.TenantId != plan.TenantId || digest != plan.DefinitionDigest || target is null ||
            (job.ExecutionPolicy ?? new()) != plan.ExecutionPolicy ||
            new JobResolvedTargetPlan(plan.Version, plan.TenantId, plan.JobId, plan.DefinitionDigest, target,
                plan.GroupRevision, plan.Bindings, plan.Nodes, plan.ExecutionPolicy).ComputeDigest() != plan.ComputeDigest())
            throw new ExecutionAdmissionDeniedException("The resolved target plan differs from the protected definition and execution policy.");
    }

    private async Task RequirePrincipalAsync(string? principalId, string? credentialId, JobResolvedTargetPlan plan,
        CancellationToken ct, IReadOnlyList<Guid>? subset = null)
    {
        if (string.IsNullOrWhiteSpace(principalId))
            throw new ExecutionAdmissionDeniedException("A current configuring principal is required; save the schedule again before activation.");
        var claims = new List<Claim> { new("netratel_principal_id", principalId) };
        if (credentialId is not null) claims.Add(new("netratel_integration_credential_id", credentialId));
        // Rehydrate only the existing persisted, expiring validated OIDC authority.
        // No role claim is copied into a durable admission and credential attenuation still applies.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "NetclawSavedAuthority"));
        if (!await access.AuthorizeAsync(principal, NetRatelPermissions.JobManagement, plan.TenantId, ct))
            throw new ExecutionAdmissionDeniedException("Current job execution permission was revoked.");
        foreach (var agent in subset ?? plan.Nodes.Select(node => node.AgentId).ToArray())
            await RequireActiveTargetAsync(plan.TenantId, plan.JobId, agent, ct);
    }

    private async Task RequireActiveTargetAsync(int tenantId, ulong jobId, Guid agentId, CancellationToken ct)
    {
        if (!await db.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == tenantId && tenant.DeletedAtUtc == null, ct) ||
            !await db.Jobs.AsNoTracking().AnyAsync(job => job.Id == checked((long)jobId) && job.TenantId == tenantId, ct) ||
            !await db.Agents.AsNoTracking().AnyAsync(agent => agent.Id == agentId && agent.TenantId == tenantId &&
                agent.IsEnabled && agent.Status == AgentStatus.Active && agent.DeletedAtUtc == null &&
                agent.RevokedAtUtc == null && agent.SupersededAtUtc == null && agent.SupersededByAgentId == null, ct))
            throw new ExecutionAdmissionDeniedException("A current target or source definition is unavailable in the authorized tenant.");
    }

    private static (string PrincipalId, string? CredentialId) CaptureAuthority(ClaimsPrincipal? principal)
    {
        var id = principal?.FindFirstValue("netratel_principal_id");
        if (string.IsNullOrWhiteSpace(id)) throw new ExecutionAdmissionDeniedException("A current configuring principal is required.");
        return (id, principal?.FindFirstValue("netratel_integration_credential_id"));
    }

    public static IReadOnlyDictionary<string, string[]> TargetParameters(JobDefinitionDetails definition, string? inputsJson)
    {
        var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in definition.Params.Where(parameter => parameter.DefaultValue is not null))
            values[parameter.Name] = [parameter.DefaultValue!];
        if (string.IsNullOrWhiteSpace(inputsJson)) return values;
        using var document = ParseTargetInputs(inputsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JobTargetValidationException("target_inputs_invalid", "Job inputs must be a JSON object.");
        Apply(document.RootElement);
        if (document.RootElement.TryGetProperty("input", out var nested) && nested.ValueKind == JsonValueKind.Object) Apply(nested);
        return values;

        void Apply(JsonElement root)
        {
            var providedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in root.EnumerateObject())
            {
                if (pair.Name.StartsWith("node.", StringComparison.OrdinalIgnoreCase))
                    throw new JobTargetValidationException("node_context_reserved", "The node context is reserved and cannot be supplied as a job parameter.");
                if (!definition.Params.Any(parameter => string.Equals(parameter.Name, pair.Name, StringComparison.OrdinalIgnoreCase))) continue;
                if (!providedNames.Add(pair.Name))
                    throw new JobTargetValidationException("target_parameter_ambiguous", "A target parameter is supplied more than once in the same input object.");
                values[pair.Name] = pair.Value.ValueKind == JsonValueKind.Array
                    ? pair.Value.EnumerateArray().Select(element => element.ValueKind == JsonValueKind.String
                        ? element.GetString()! : throw new JobTargetValidationException("target_inputs_invalid", "Target operand arrays require literal string values.")).ToArray()
                    : [pair.Value.ValueKind == JsonValueKind.String ? pair.Value.GetString()! : pair.Value.ValueKind == JsonValueKind.Null ? string.Empty : pair.Value.GetRawText()];
            }
        }
    }

    private static JsonDocument ParseTargetInputs(string inputsJson)
    {
        try { return JsonDocument.Parse(inputsJson); }
        catch (JsonException) { throw new JobTargetValidationException("target_inputs_invalid", "Job inputs must be a valid JSON object."); }
    }

    private static void RequireExpectedPlan(RunJobRequest request, JobResolvedTargetPlan plan)
    {
        if (request.ExpectedTargetPlanDigest is { } expected && !string.Equals(expected, plan.ComputeDigest(), StringComparison.Ordinal))
            throw new JobTargetValidationException("target_review_stale", "Refresh the target preview before starting this changed plan.", 409);
    }

    private static JobRuntimeTargetInput? RequestTargetInput(JobTargetDefinition? target, Guid agentId)
    {
        if (target?.Source != JobTargetSource.RuntimeInput) return null;
        if (target.RuntimeInput?.Kind is not (JobRuntimeTargetKind.ClientId or JobRuntimeTargetKind.ClientIds))
            throw new JobTargetValidationException("request_runtime_target_kind", "Singular Requests require a ClientId or ClientIds runtime target binding.");
        return new(target.RuntimeInput.Kind, [agentId.ToString("D")]);
    }

    private static void RequireSingularRequestPlan(JobResolvedTargetPlan plan, Guid agentId)
    {
        if (plan.Nodes.Count != 1 || plan.Nodes[0].AgentId != agentId)
            throw new JobTargetValidationException("request_target_set_mismatch", "The complete job target set must be exactly the Request's protected TargetAgentId.", 409);
    }

    private static string AdmissionKey(string kind, string scope, string occurrence) =>
        kind + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "|" + occurrence)));
    private static long LockKey(string key) => McpAggregateAdmissionKey.LockKey(key);
    private static string? Bound(string? value) => value is null ? null : value[..Math.Min(value.Length, 256)];
    private static void CloseGate(JobAggregateControlRecord control, string reason)
    {
        control.DispatchClosed = true;
        if (reason == JobRunNodeReasons.StoppedAfterUnknownExecution || control.GateReason is null ||
            control.GateReason != JobRunNodeReasons.StoppedAfterUnknownExecution &&
            (reason == JobRunNodeReasons.ParentCancelled || reason == "ParentActionDeadline" && control.GateReason != JobRunNodeReasons.ParentCancelled))
            control.GateReason = reason;
    }
    public static bool IsNodeTerminal(JobRunNodeState state) => state is not (JobRunNodeState.NeverStarted or JobRunNodeState.Claimed or JobRunNodeState.Running);
    private static bool ActionDeadlineReached(JobAggregateControlRecord control, IReadOnlyList<JobRunNodeRecord> nodes, DateTimeOffset now)
        => control.ActionDeadlineAtUtc is { } end && now >= end &&
            (control.GateReason == "ParentActionDeadline" || nodes.Any(node => !IsNodeTerminal(node.State) ||
                node.CompletedAtUtc is null || node.CompletedAtUtc > end));
    public static bool IsTerminal(JobRunState state) => state is not (JobRunState.Pending or JobRunState.Running);
    private static JobRunNodeState NodeState(JobRunState state) => state switch
    {
        JobRunState.Pending => JobRunNodeState.Claimed, JobRunState.Running => JobRunNodeState.Running,
        JobRunState.Succeeded => JobRunNodeState.Succeeded, JobRunState.Cancelled => JobRunNodeState.Cancelled,
        JobRunState.TimedOut => JobRunNodeState.TimedOut, JobRunState.Unknown => JobRunNodeState.Unknown,
        _ => JobRunNodeState.Failed
    };
    public static JobRunState TerminalState(IEnumerable<JobRunNodeRecord> nodes, JobAggregateControlRecord control)
    {
        var states = nodes.Select(node => node.State).ToArray();
        if (states.Any(state => !IsNodeTerminal(state))) throw new InvalidOperationException("In-flight nodes prevent aggregate terminal completion.");
        if (states.Contains(JobRunNodeState.Unknown)) return JobRunState.Unknown;
        if (control.CancellationRequestedAtUtc is not null) return JobRunState.Cancelled;
        if (control.GateReason == "ParentActionDeadline") return JobRunState.TimedOut;
        if (states.Contains(JobRunNodeState.Cancelled)) return JobRunState.Cancelled;
        if (states.Contains(JobRunNodeState.TimedOut)) return JobRunState.TimedOut;
        return states.Length > 0 && states.All(state => state == JobRunNodeState.Succeeded) ? JobRunState.Succeeded : JobRunState.Failed;
    }
}
