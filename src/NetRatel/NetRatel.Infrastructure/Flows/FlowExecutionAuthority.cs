using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

/// <summary>The canonical authority rechecks the configuring principal and bound target without borrowing service roles.</summary>
public sealed class FlowExecutionAuthority(OrchestratorDbContext db, IFlowExecutionAuthorityVerifier verifier,
    IEffectiveAccessService access, IFlowTransactionAdmission sourceAdmission, TimeProvider clock,
    IFlowExecutionEventAdmission? executionSourceAdmission = null) : IKeyedExecutionAuthority
{
    public async Task RequireAllowedAsync(ExecutionAdmissionIdentity identity, JobDefinitionInfo pinnedDefinition,
        CancellationToken cancellationToken)
    {
        identity.Validate();
        if (pinnedDefinition.TenantId != identity.TenantId ||
            pinnedDefinition.Target is null && pinnedDefinition.AgentId is null || pinnedDefinition.AgentId == Guid.Empty ||
            !await db.Jobs.AsNoTracking().AnyAsync(job => job.Id == checked((long)pinnedDefinition.Id) &&
                job.TenantId == identity.TenantId, cancellationToken))
            throw new ExecutionAdmissionDeniedException("The pinned source definition is no longer available in its authorized tenant.");
        if (pinnedDefinition.Target is not null)
        {
            try { pinnedDefinition.Target.Validate(); }
            catch (ArgumentException) { throw new ExecutionAdmissionDeniedException("The pinned target policy is invalid."); }
            if (pinnedDefinition.AgentId is not null)
                throw new ExecutionAdmissionDeniedException("A target policy cannot retain a singular execution authority.");
            await RequireTenantAsync(identity, NetRatelPermissions.JobManagement, cancellationToken);
        }
        else await RequireAsync(identity, NetRatelPermissions.JobManagement, pinnedDefinition.AgentId!.Value, cancellationToken);
    }

    public async Task RequireTargetsAllowedAsync(ExecutionAdmissionIdentity identity, JobDefinitionInfo pinnedDefinition,
        IReadOnlyList<Guid> agentIds, CancellationToken cancellationToken)
    {
        await RequireAllowedAsync(identity, pinnedDefinition, cancellationToken);
        if (agentIds.Count is < 1 or > 10000 || agentIds.Any(id => id == Guid.Empty) ||
            agentIds.Distinct().Count() != agentIds.Count ||
            pinnedDefinition.Target is null && (agentIds.Count != 1 || agentIds[0] != pinnedDefinition.AgentId))
            throw new ExecutionAdmissionDeniedException("The complete admitted target set is invalid.");
        var ids = agentIds.ToArray();
        var eligible = await db.Agents.AsNoTracking().CountAsync(agent => agent.TenantId == identity.TenantId &&
            ids.Contains(agent.Id) && agent.IsEnabled && agent.Status == AgentStatus.Active && agent.DeletedAtUtc == null &&
            agent.RevokedAtUtc == null && agent.SupersededAtUtc == null && agent.SupersededByAgentId == null, cancellationToken);
        if (eligible != ids.Length)
            throw new ExecutionAdmissionDeniedException("Current execution authority does not cover the complete admitted target set.");
    }

    public async Task RequireScriptAsync(ExecutionAdmissionIdentity identity, ulong scriptId, Guid agentId, CancellationToken cancellationToken)
    {
        if (!await db.Scripts.AsNoTracking().AnyAsync(script => script.Id == checked((long)scriptId), cancellationToken))
            throw new ExecutionAdmissionDeniedException("The library definition is no longer available.");
        await RequireAsync(identity, NetRatelPermissions.ScriptExecute, agentId, cancellationToken);
    }

    public async Task RequireFirstDispatchAsync(ExecutionAdmissionIdentity identity, FlowRunLease? lease, CancellationToken ct)
    {
        identity.Validate();
        if (lease is null || lease.RunId != identity.FlowRunId || lease.Version.Id != identity.FlowVersionId ||
            lease.Event.TenantId != identity.TenantId || lease.Event.Authority.PrincipalId != identity.PrincipalId ||
            lease.Event.Authority.IntegrationCredentialId != identity.IntegrationCredentialId || !db.Database.IsNpgsql())
            throw new ExecutionAdmissionDeniedException("The unstarted action has no current flow lease.");
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var decision = await sourceAdmission.CanStartActionAsync(db, lease, ct);
        if (!decision.Allowed) throw new ExecutionAdmissionDeniedException(decision.Code);
        var run = await db.FlowRuns.FromSqlInterpolated($"""
            SELECT * FROM "FlowRuns" WHERE "TenantId"={identity.TenantId} AND "Id"={identity.FlowRunId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(ct);
        if (run is null || run.FlowVersionId != identity.FlowVersionId || run.Status != FlowRunStatus.Running ||
            run.LeaseToken != lease.Token || run.LeaseOwner != lease.WorkerId || run.Fence != lease.Fence ||
            run.LeaseExpiresAtUtc != lease.ExpiresAtUtc ||
            run.EventFingerprint != FlowContractValidation.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lease.Event, Json))))
            throw new ExecutionAdmissionDeniedException("The flow lease was reclaimed or expired.");
        var version = await db.FlowVersions.AsNoTracking().SingleOrDefaultAsync(version =>
            version.TenantId == identity.TenantId && version.Id == identity.FlowVersionId, ct);
        if (version is null || version.ExecutionAuthorityJson is null ||
            JsonSerializer.Deserialize<FlowExecutionAuthorityDto>(version.ExecutionAuthorityJson, Json) != lease.Event.Authority ||
            version.ConfigurationHash != lease.Version.ConfigurationHash ||
            version.ConfigurationHash != FlowGraphSerialization.Hash(lease.Version.Graph) ||
            !lease.Version.Graph.Nodes.Any(node => node.Id == identity.NodeId &&
                node.Kind is FlowNodeKind.RunJob or FlowNodeKind.RunLibraryScript or FlowNodeKind.RerunOriginalExecution) ||
            !await db.FlowRuntimeIdentity.AsNoTracking().AnyAsync(row => row.Id == 1 && row.SourceInstanceId == lease.SourceInstanceId, ct))
            throw new ExecutionAdmissionDeniedException("The immutable version or published execution authority differs.");
        FlowContinuationDto? continuation;
        try { continuation = run.ContinuationJson is null ? null : JsonSerializer.Deserialize<FlowContinuationDto>(run.ContinuationJson, Json); }
        catch (JsonException) { throw new ExecutionAdmissionDeniedException("The persisted action checkpoint is invalid."); }
        var node = continuation?.Node;
        if (node is null || continuation!.ResumeNodeId != identity.NodeId || node.NodeId != identity.NodeId ||
            node.Progress != FlowNodeProgress.Dispatching || node.Attempt != identity.NodeAttempt ||
            !FlowNodeCheckpointRules.Valid(lease, node) ||
            FlowActionKeys.CreateExecution(lease, identity.NodeId, identity.NodeAttempt) != identity.ActionKey)
            throw new ExecutionAdmissionDeniedException("The persisted action checkpoint does not admit this physical dispatch.");
        var databaseNow = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var now = clock.GetUtcNow();
        if (databaseNow > now) now = databaseNow;
        if (run.LeaseExpiresAtUtc <= now)
            throw new ExecutionAdmissionDeniedException("The flow lease was reclaimed or expired.");
        if (node.DeadlineAtUtc <= now)
            throw new ExecutionAdmissionDeniedException("The flow node deadline expired before physical dispatch.");
        if (transaction is not null) await transaction.CommitAsync(ct);
        now = clock.GetUtcNow();
        if (lease.ExpiresAtUtc <= now)
            throw new ExecutionAdmissionDeniedException("The flow lease expired before physical dispatch.");
        if (node.DeadlineAtUtc <= now)
            throw new ExecutionAdmissionDeniedException("The flow node deadline expired before physical dispatch.");
    }

    /// <summary>Later native job steps use persisted source proof and current authority, never an expired worker lease.</summary>
    public async Task RequireContinuationAsync(ExecutionAdmissionIdentity identity, CancellationToken ct)
    {
        identity.Validate();
        if (!db.Database.IsNpgsql() || identity.NodeAttempt > FlowGraphV2Limits.MaximumRecoveryAttempts)
            throw new ExecutionAdmissionDeniedException("Persisted flow continuation admission is unavailable.");
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await FlowTenantTransactionLock.AcquireAsync(db, identity.TenantId, ct).ConfigureAwait(false);
        // Source admission owns the configuration/source-before-run lock order. This first read is intentionally nonlocking.
        var candidate = await db.FlowRuns.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == identity.TenantId && row.Id == identity.FlowRunId, ct);
        FlowEventEnvelope? input;
        try { input = candidate is null ? null : JsonSerializer.Deserialize<FlowEventEnvelope>(candidate.EventJson, Json); }
        catch (JsonException) { throw new ExecutionAdmissionDeniedException("The persisted source event is invalid."); }
        if (candidate is null || input is null || !FlowContractValidation.ValidEnvelope(input) || candidate.FlowVersionId != identity.FlowVersionId ||
            input.TenantId != identity.TenantId || input.FlowVersionId != identity.FlowVersionId ||
            input.Authority.PrincipalId != identity.PrincipalId ||
            input.Authority.IntegrationCredentialId != identity.IntegrationCredentialId ||
            candidate.EventFingerprint != FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(input, Json)))
            throw new ExecutionAdmissionDeniedException("The persisted source event differs from its execution identity.");
        var decision = input.ExecutionEvent is not null
            ? executionSourceAdmission is null ? new FlowDispatchDecision(false, "execution-source-admission-unavailable") :
                await executionSourceAdmission.LockAndAdmitInvestigationAsync(db, input, identity.FlowRunId, ct)
            : await MonitoringStore.LockAndAdmitInvestigationAsync(db, input, identity.FlowRunId, clock, ct);
        if (!decision.Allowed) throw new ExecutionAdmissionDeniedException(decision.Code);
        var version = await db.FlowVersions.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == identity.TenantId && row.Id == identity.FlowVersionId, ct);
        if (version is null) throw new ExecutionAdmissionDeniedException("The published continuation version is unavailable.");
        var definition = await db.FlowDefinitions.FromSqlInterpolated($"""
            SELECT * FROM "FlowDefinitions" WHERE "TenantId"={identity.TenantId} AND "Id"={version.FlowId} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(ct);
        var run = await db.FlowRuns.FromSqlInterpolated($"""
            SELECT * FROM "FlowRuns" WHERE "TenantId"={identity.TenantId} AND "Id"={identity.FlowRunId} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(ct);
        FlowGraphDto? graph;
        FlowExecutionAuthorityDto? publishedAuthority;
        try
        {
            graph = JsonSerializer.Deserialize<FlowGraphDto>(version.GraphJson, Json);
            publishedAuthority = version.ExecutionAuthorityJson is null ? null :
                JsonSerializer.Deserialize<FlowExecutionAuthorityDto>(version.ExecutionAuthorityJson, Json);
        }
        catch (JsonException) { throw new ExecutionAdmissionDeniedException("The immutable continuation version is invalid."); }
        var source = await db.FlowRuntimeIdentity.AsNoTracking().SingleOrDefaultAsync(row => row.Id == 1, ct);
        if (definition is not { Enabled: true, DeletedAtUtc: null } || definition.EnabledSinceUtc > input.OccurredAtUtc ||
            version.PublishedAtUtc > input.OccurredAtUtc || run is null || run.FlowId != version.FlowId ||
            run.FlowVersionId != identity.FlowVersionId || run.EventId != (input.SourceAdmissionKey ?? input.EventId) || run.OccurrenceId != input.OccurrenceId ||
            run.EventFingerprint != candidate.EventFingerprint ||
            run.Status is not (FlowRunStatus.Running or FlowRunStatus.RetryWaiting or FlowRunStatus.WaitingForAi or
                FlowRunStatus.AwaitingApproval or FlowRunStatus.WaitingForExecution) ||
            graph is null || !FlowGraphV2Validator.ValidatePublished(graph).Valid || publishedAuthority is null ||
            publishedAuthority != input.Authority || version.PublishedBy != publishedAuthority.PrincipalId ||
            version.ConfigurationHash != FlowGraphSerialization.Hash(graph) ||
            !graph.Nodes.Any(node => node.Id == identity.NodeId &&
                node.Kind is FlowNodeKind.RunJob or FlowNodeKind.RunLibraryScript or FlowNodeKind.RerunOriginalExecution) ||
            source is null || source.SourceInstanceId == Guid.Empty ||
            FlowActionKeys.CreateExecution(identity.TenantId, source.SourceInstanceId, input.OccurrenceId, input.EventId,
                identity.FlowVersionId, identity.NodeId, identity.NodeAttempt) != identity.ActionKey ||
            !await verifier.AuthorizeAsync(identity.TenantId, input.Authority, ct))
            throw new ExecutionAdmissionDeniedException("The source flow, published authority or stable action identity is no longer active.");
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task RequireAsync(ExecutionAdmissionIdentity identity, string permission, Guid agentId, CancellationToken ct)
    {
        await RequireTenantAsync(identity, permission, ct);
        if (!await db.Agents.AsNoTracking().AnyAsync(agent => agent.TenantId == identity.TenantId && agent.Id == agentId &&
                agent.IsEnabled && agent.Status == AgentStatus.Active && agent.DeletedAtUtc == null &&
                agent.RevokedAtUtc == null && agent.SupersededAtUtc == null && agent.SupersededByAgentId == null, ct))
            throw new ExecutionAdmissionDeniedException("Current execution permission or target authority was revoked.");
    }

    private async Task RequireTenantAsync(ExecutionAdmissionIdentity identity, string permission, CancellationToken ct)
    {
        identity.Validate();
        var authority = new FlowExecutionAuthorityDto(identity.PrincipalId, identity.IntegrationCredentialId);
        if (!await verifier.AuthorizeAsync(identity.TenantId, authority, ct) ||
            !await access.AuthorizeAsync(Principal(authority), permission, identity.TenantId, ct) ||
            !await db.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == identity.TenantId && tenant.DeletedAtUtc == null, ct))
            throw new ExecutionAdmissionDeniedException("Current execution permission was revoked.");
    }

    private static ClaimsPrincipal Principal(FlowExecutionAuthorityDto authority) => FlowAuthorityPrincipal.Create(authority);
}
