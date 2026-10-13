using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Persistence;
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
        return await authority.CanInvokeAsync(row.TenantId, actor, row.ActiveCredentialId, row.Automated, ct) &&
            (row.OccurrenceId is null || await authority.CanReadAlertAsync(row.TenantId, actor, row.ActiveCredentialId, ct));
    }

    private async Task<bool> AdmitAutomaticMessageAsync(IServiceProvider provider, AiInvestigationRecord row, NetclawRuntimeProfile profile, CancellationToken ct)
    {
        if (!row.Automated) return true;
        if (row.FlowRunId is not { } runId || row.SourceActionKey is null) return false;
        var db = provider.GetRequiredService<OrchestratorDbContext>();
        var action = await db.FlowActions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == row.TenantId && x.RunId == runId && x.IdempotencyKey == row.SourceActionKey, ct);
        if (action is null || action.Status is not (FlowActionStatus.Dispatching or FlowActionStatus.WaitingForAi or FlowActionStatus.AwaitingApproval or FlowActionStatus.Succeeded)) return false;
        var draft = JsonSerializer.Deserialize<FlowIncidentActionDraft>(action.DraftJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (draft is null || draft.Kind != FlowActionKind.NetclawInvestigation || draft.TenantId != row.TenantId || draft.RunId != runId ||
            draft.IdempotencyKey != row.SourceActionKey || draft.ConnectorId != profile.Id || draft.ConnectorRevision != profile.Revision ||
            draft.Event.OccurrenceId != row.OccurrenceId || draft.Event.EventId != row.ActiveMessageId ||
            draft.Event.Authority.PrincipalId != row.CreatedBy || draft.Event.Authority.IntegrationCredentialId != row.ActiveCredentialId) return false;
        // Historical AI-only runs recorded queue admission as success. They may finish their already admitted first turn,
        // while new completed actions can never admit another automated message.
        if (action.Status == FlowActionStatus.Succeeded && (draft.AiDeadlineAtUtc is not null || action.AiResultJson is not null)) return false;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var expired = draft.AiDeadlineAtUtc is { } deadline && clock.GetUtcNow() >= deadline;
        var decision = expired ? new FlowDispatchDecision(false, "ai-investigation-timeout")
            : await MonitoringStore.LockAndAdmitInvestigationAsync(db, draft.Event, runId, clock, ct);
        expired = draft.AiDeadlineAtUtc is { } currentDeadline && clock.GetUtcNow() >= currentDeadline;
        if (expired)
        {
            var current = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"Id\" = {row.Id} FOR UPDATE").AsNoTracking().SingleAsync(ct);
            if (current.ActiveMessageId == draft.Event.EventId)
                await InvestigationStore.StageTurnResultAsync(db, current, FlowAiResultStatus.Timeout, clock.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return !expired && decision.Allowed;
    }

    private async Task NotSentAsync(Guid id, Guid? messageId, string reason, CancellationToken ct)
    {
        if (messageId is null) return;
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        if (row.ActiveMessageId == messageId && row.State is InvestigationState.Processing or InvestigationState.Queued)
        {
            row.State = InvestigationState.Idle;
            InvestigationStore.Append(db, row, "dispatch_not_sent", reason);
            await InvestigationStore.StageTurnResultAsync(db, row, FlowAiResultStatus.Failed, clock.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
