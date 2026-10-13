using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Notifications;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed class NetclawFlowConnector(OrchestratorDbContext db, NetclawAuthority authority,
    NetclawProfileService profiles, InvestigationStore investigations, TimeProvider clock) : IFlowConnectorCatalog
{
    public async Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid connectorId, FlowExecutionAuthorityDto actor, CancellationToken cancellationToken = default)
    {
        var connection = await profiles.GetAsync(tenantId, cancellationToken);
        if (connection.Id != connectorId || connectorId == Guid.Empty) return null;
        var allowed = await authority.CanInvokeAsync(tenantId, actor.PrincipalId, actor.IntegrationCredentialId, true, cancellationToken);
        var usable = connection.Enabled && connection.HasDeviceToken && connection.LastTestSucceeded == true && allowed;
        return new(connection.Id, tenantId, "Netclaw AI investigation", connection.Enabled, usable,
            usable ? null : "netclaw-connection-unverified-or-permission-denied", connection.Revision) { Kind = FlowActionKind.NetclawInvestigation };
    }

    public async Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto actor, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetAsync(tenantId, cancellationToken);
        var reference = await GetAsync(tenantId, profile.Id, actor, cancellationToken);
        return reference is null ? [] : [reference];
    }

    public async Task<FlowIncidentActionResult> DispatchAsync(FlowIncidentActionDraft draft, CancellationToken ct)
    {
        if (draft.Kind != FlowActionKind.NetclawInvestigation || !FlowContractValidation.ValidEnvelope(draft.Event) ||
            draft.Event.TenantId != draft.TenantId || draft.RunId == Guid.Empty || draft.IdempotencyKey is not { Length: > 0 and <= 256 })
            return new(FlowIncidentActionResultKind.Failed, "invalid-netclaw-action");
        var existing = await db.Set<AiInvestigationRecord>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == draft.TenantId && x.SourceActionKey == draft.IdempotencyKey, ct);
        FlowAiResultDto Outcome(FlowAiResultStatus status, Guid? investigation = null) => new(draft.TenantId, draft.RunId,
            draft.Event.FlowVersionId, draft.ActionNodeId, investigation ?? existing?.Id ?? Guid.Empty, draft.Event.EventId, status,
            clock.GetUtcNow(), status is FlowAiResultStatus.Queued or FlowAiResultStatus.Processing or FlowAiResultStatus.AwaitingApproval ? null : clock.GetUtcNow());
        if (existing is not null)
        {
            // Read the exact committed automated turn; later manual turns and Idle are not evidence.
            var terminal = await db.Set<AiInvestigationTurnResultRecord>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == draft.TenantId && x.InvestigationId == existing.Id && x.MessageId == draft.Event.EventId, ct);
            if (terminal is not null)
            {
                var result = JsonSerializer.Deserialize<FlowAiResultDto>(terminal.ResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (result is null || result.TenantId != draft.TenantId || result.FlowRunId != draft.RunId || result.FlowVersionId != draft.Event.FlowVersionId ||
                    result.ActionNodeId != draft.ActionNodeId || result.MessageId != draft.Event.EventId || result.InvestigationId != existing.Id)
                    return Result(Outcome(FlowAiResultStatus.Failed), "ai-correlation-invalid");
                if (draft.AiDeadlineAtUtc is { } limit && result.CompletedAtUtc > limit)
                    return Result(Outcome(FlowAiResultStatus.Timeout), "ai-investigation-timeout");
                return Result(result);
            }
            if (existing.State == InvestigationState.Archived)
                return Result(Outcome(FlowAiResultStatus.Cancelled), "ai-investigation-cancelled");
        }
        var target = await GetAsync(draft.TenantId, draft.ConnectorId, draft.Event.Authority, ct);
        if (target is not { CanExecute: true } || target.Revision != draft.ConnectorRevision)
            return Result(Outcome(FlowAiResultStatus.Failed), "netclaw-connection-revision-unavailable");
        if (draft.AiDeadlineAtUtc is { } deadline && clock.GetUtcNow() >= deadline)
            return Result(Outcome(FlowAiResultStatus.Timeout), "ai-investigation-timeout");
        // An action retry identifies the same local investigation and automated message. It never resends a remote turn.
        var agentIds = draft.Event.Data.AgentId != Guid.Empty ? new[] { draft.Event.Data.AgentId } : Array.Empty<Guid>();
        if (agentIds.Length == 0 && draft.Event.Data.OfflineSummary is not null)
        {
            var stateJson = await db.MonitoringSeries.AsNoTracking().Where(x => x.TenantId == draft.TenantId &&
                x.RuleId == draft.Event.Data.RuleId && x.ActiveOccurrenceId == draft.Event.OccurrenceId && x.AgentId == Guid.Empty)
                .Select(x => x.StateJson).SingleOrDefaultAsync(ct);
            var state = stateJson is null ? null : JsonSerializer.Deserialize<MonitoringSeriesState>(stateJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (state is not null && !state.OfflineAgentIds.IsDefault) agentIds = state.OfflineAgentIds.Take(20).ToArray();
        }
        if (agentIds.Length == 0) return Result(Outcome(FlowAiResultStatus.Failed), "netclaw-no-client-context");
        var row = existing ?? await investigations.CreateAsync(draft.TenantId,
            new InvestigationCreateRequest(Guid.NewGuid(), draft.Fields.Title, agentIds, draft.Event.OccurrenceId)
                { SelectedPrompts = draft.AiSettings?.OptionalPrompts }, draft.Event.Authority.PrincipalId, ct,
            sourceActionKey: draft.IdempotencyKey, flowRunId: draft.RunId, pinnedPrompts: draft.AiSettings?.PinnedPrompts);
        await investigations.AcceptMessageAsync(draft.TenantId, row.Id,
            new(draft.Event.EventId, "Investigate the linked monitoring alert for the selected systems. Gather evidence with permitted NetRatel tools, explain the likely cause, and propose remediation. Request operator approval before changing systems.\n\nAlert description:\n" + draft.Fields.Description),
            draft.Event.Authority.PrincipalId, draft.Event.Authority.IntegrationCredentialId, true, ct);
        // Admission commits a waiting checkpoint, never a successful discovery result.
        return Result(Outcome(row.State switch { InvestigationState.AwaitingApproval => FlowAiResultStatus.AwaitingApproval,
            InvestigationState.Processing => FlowAiResultStatus.Processing, InvestigationState.DeliveryUnknown => FlowAiResultStatus.DeliveryUnknown,
            _ => FlowAiResultStatus.Queued }, row.Id));

    }
    private static FlowIncidentActionResult Result(FlowAiResultDto result, string? code = null) => new(result.Status switch
    {
        FlowAiResultStatus.Completed => FlowIncidentActionResultKind.Succeeded,
        FlowAiResultStatus.Queued or FlowAiResultStatus.Processing or FlowAiResultStatus.AwaitingApproval => FlowIncidentActionResultKind.Waiting,
        FlowAiResultStatus.DeliveryUnknown => FlowIncidentActionResultKind.DeliveryUnknown,
        _ => FlowIncidentActionResultKind.Failed
    }, code ?? "ai-investigation-" + result.Status.ToString().ToLowerInvariant(),
        result.InvestigationId == Guid.Empty ? null : new(result.InvestigationId.ToString("D"))) { AiResult = result };
}

public sealed class NetclawConnectorCatalog(NotificationAndIncidentConnectorCatalog existing, NetclawFlowConnector netclaw) : IFlowConnectorCatalog
{
    public async Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid connectorId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) =>
        await existing.GetAsync(tenantId, connectorId, authority, cancellationToken) ?? await netclaw.GetAsync(tenantId, connectorId, authority, cancellationToken);
    public async Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) =>
        (await existing.ListAsync(tenantId, authority, cancellationToken)).Concat(await netclaw.ListAsync(tenantId, authority, cancellationToken)).ToArray();
}
