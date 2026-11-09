using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Flows;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Netclaw;
using Npgsql;

namespace NetRatel.Infrastructure.Netclaw;

public sealed partial class NetclawInvestigationWorker
{
    private readonly SemaphoreSlim ownershipProbe = new(1);
    private async Task<bool> StillOwnAsync(CancellationToken ct)
    {
        await ownershipProbe.WaitAsync(ct);
        try
        {
            if (!ready || ownership is null) return false;
            await using var command = new NpgsqlCommand("SELECT 1", ownership);
            return await command.ExecuteScalarAsync(ct) is 1;
        }
        finally { ownershipProbe.Release(); }
    }

    private static async Task<bool> AuthorizedAsync(IServiceProvider provider, AiInvestigationRecord row, string actor, CancellationToken ct)
    {
        var authority = provider.GetRequiredService<NetclawAuthority>();
        if (row.Automated && row.OccurrenceId is null)
        {
            var action = await ReadSavedFlowActionAsync(provider, row, ct);
            var draft = FlowInvestigationBinding.ReadDraft(action);
            return draft?.Event?.ExecutionEvent is { } source &&
                FlowInvestigationBinding.MatchesExecution(row, action, draft) && actor == row.CreatedBy &&
                await authority.CanInvestigateExecutionAsync(row.TenantId, actor, row.ActiveCredentialId, [source.SourceKind], ct);
        }
        return await authority.CanInvokeAsync(row.TenantId, actor, row.ActiveCredentialId, row.Automated, ct) &&
            (!await provider.GetRequiredService<InvestigationStore>().HasAlertAsync(row.TenantId, row.Id, ct) ||
             await authority.CanReadAlertAsync(row.TenantId, actor, row.ActiveCredentialId, ct));
    }

    private async Task<bool> AdmitAutomaticMessageAsync(IServiceProvider provider, AiInvestigationRecord row, NetclawRuntimeProfile profile, string expectedSessionId, CancellationToken ct)
    {
        if (!row.Automated) return true;
        if (row.FlowRunId is not { } runId || row.SourceActionKey is null) return false;
        var db = provider.GetRequiredService<OrchestratorDbContext>();
        var action = await db.FlowActions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == row.TenantId && x.RunId == runId && x.IdempotencyKey == row.SourceActionKey, ct);
        if (action is null || action.Status is not (FlowActionStatus.Dispatching or FlowActionStatus.WaitingForAi or FlowActionStatus.AwaitingApproval or FlowActionStatus.Succeeded)) return false;
        var draft = JsonSerializer.Deserialize<FlowIncidentActionDraft>(action.DraftJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var source = draft?.Event?.ExecutionEvent is not null || row.OccurrenceId is null;
        if (source)
        {
            if (!FlowInvestigationBinding.MatchesExecution(row, action, draft, profile)) return false;
        }
        else if (draft is null || draft.Kind != FlowActionKind.NetclawInvestigation || draft.TenantId != row.TenantId || draft.RunId != runId ||
            draft.IdempotencyKey != row.SourceActionKey || draft.ConnectorId != profile.Id || draft.ConnectorRevision != profile.Revision ||
            draft.Event.OccurrenceId != row.OccurrenceId || draft.Event.EventId != row.ActiveMessageId ||
            draft.Event.Authority.PrincipalId != row.CreatedBy || draft.Event.Authority.IntegrationCredentialId != row.ActiveCredentialId) return false;
        if (draft is null) return false;
        // Historical AI-only runs recorded queue admission as success. They may finish their already admitted first turn,
        // while new completed actions can never admit another automated message.
        if (action.Status == FlowActionStatus.Succeeded && (draft.AiDeadlineAtUtc is not null || action.AiResultJson is not null)) return false;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var expired = draft.AiDeadlineAtUtc is { } deadline && clock.GetUtcNow() >= deadline;
        var decision = expired ? new FlowDispatchDecision(false, "ai-investigation-timeout")
            : source
                ? await provider.GetRequiredService<IFlowExecutionEventAdmission>().LockAndAdmitInvestigationAsync(db, draft.Event, runId, ct)
                : await MonitoringStore.LockAndAdmitInvestigationAsync(db, draft.Event, runId, clock, ct);
        if (source && decision.Allowed)
        {
            // Source admission locks the source/intent/version/run first. Read the saved turn fresh
            // under subsequent locks; an EF identity-map snapshot must not authorize another turn.
            var current = await db.Set<AiInvestigationRecord>().FromSqlInterpolated(
                $"SELECT * FROM \"AiInvestigations\" WHERE \"TenantId\"={row.TenantId} AND \"Id\"={row.Id} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            var savedAction = await db.FlowActions.FromSqlInterpolated(
                $"SELECT * FROM \"FlowActions\" WHERE \"TenantId\"={row.TenantId} AND \"RunId\"={runId} AND \"NodeId\"={action.NodeId} FOR SHARE")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            if (current is null || savedAction?.DraftJson != action.DraftJson ||
                !FlowInvestigationBinding.MatchesExecution(current, savedAction, FlowInvestigationBinding.ReadDraft(savedAction), profile, expectedSessionId))
                return false;
        }
        expired = draft.AiDeadlineAtUtc is { } currentDeadline && clock.GetUtcNow() >= currentDeadline;
        if (expired)
        {
            var current = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"Id\" = {row.Id} FOR UPDATE").AsNoTracking().SingleAsync(ct);
            if (current.ActiveMessageId == FlowAiCorrelation.MessageId(draft))
                await InvestigationStore.StageTurnResultAsync(db, current, FlowAiResultStatus.Timeout, clock.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return !expired && decision.Allowed;
    }

    private static Task<FlowActionRecord?> ReadSavedFlowActionAsync(IServiceProvider provider,
        AiInvestigationRecord row, CancellationToken ct) =>
        provider.GetRequiredService<OrchestratorDbContext>().FlowActions.AsNoTracking().SingleOrDefaultAsync(
            action => action.TenantId == row.TenantId && action.RunId == row.FlowRunId &&
                action.IdempotencyKey == row.SourceActionKey, ct);

    private static async Task<FlowDispatchDecision> EnforceSourceInvestigationPolicyAsync(IServiceProvider provider,
        AiInvestigationRecord row, SessionOwner owner, CancellationToken ct)
    {
        if (!row.Automated || row.OccurrenceId is not null) return new(true, "legacy-investigation-policy");
        var db = provider.GetRequiredService<OrchestratorDbContext>();
        var current = await db.Set<AiInvestigationRecord>().AsNoTracking()
            .SingleOrDefaultAsync(saved => saved.TenantId == row.TenantId && saved.Id == row.Id, ct);
        if (current is null) return new(false, "netclaw-source-investigation-binding-invalid");
        var action = await ReadSavedFlowActionAsync(provider, current, ct);
        var draft = FlowInvestigationBinding.ReadDraft(action);
        if (draft is null || current.ActiveMessageId is not { } messageId ||
            !FlowInvestigationBinding.MatchesExecution(current, action, draft, owner.Profile, owner.SessionId))
            return new(false, "netclaw-source-investigation-binding-invalid");
        return await provider.GetRequiredService<IFlowInvestigationToolPolicy>().EnforceDiagnosticsOnlyAsync(
            owner.Client, owner.Profile, owner.SessionId, current.Id, messageId, draft, ct);
    }

    private async Task NotSentAsync(Guid id, Guid? messageId, string reason, CancellationToken ct,
        FlowAiResultStatus status = FlowAiResultStatus.Failed)
    {
        if (messageId is null) return;
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        if (row.ActiveMessageId == messageId && row.State is InvestigationState.Processing or InvestigationState.Queued)
        {
            row.State = InvestigationState.Idle;
            InvestigationStore.Append(db, row, "dispatch_not_sent", reason);
            await InvestigationStore.StageTurnResultAsync(db, row, status, clock.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
