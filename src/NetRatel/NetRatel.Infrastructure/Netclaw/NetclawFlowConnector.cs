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
    NetclawProfileService profiles, InvestigationStore investigations) : IFlowConnectorCatalog
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
        var target = await GetAsync(draft.TenantId, draft.ConnectorId, draft.Event.Authority, ct);
        if (target is not { CanExecute: true } || target.Revision != draft.ConnectorRevision)
            return new(FlowIncidentActionResultKind.Unavailable, "netclaw-connection-revision-unavailable");
        // An action retry identifies the same local investigation; it never resends a previous remote turn.
        var existing = await db.Set<AiInvestigationRecord>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == draft.TenantId && x.SourceActionKey == draft.IdempotencyKey, ct);
        var agentIds = draft.Event.Data.AgentId != Guid.Empty ? new[] { draft.Event.Data.AgentId } : Array.Empty<Guid>();
        if (agentIds.Length == 0 && draft.Event.Data.OfflineSummary is not null)
        {
            var stateJson = await db.MonitoringSeries.AsNoTracking().Where(x => x.TenantId == draft.TenantId &&
                x.RuleId == draft.Event.Data.RuleId && x.ActiveOccurrenceId == draft.Event.OccurrenceId && x.AgentId == Guid.Empty)
                .Select(x => x.StateJson).SingleOrDefaultAsync(ct);
            var state = stateJson is null ? null : JsonSerializer.Deserialize<MonitoringSeriesState>(stateJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (state is not null && !state.OfflineAgentIds.IsDefault) agentIds = state.OfflineAgentIds.Take(20).ToArray();
        }
        if (agentIds.Length == 0) return new(FlowIncidentActionResultKind.Failed, "netclaw-no-client-context");
        var row = existing ?? await investigations.CreateAsync(draft.TenantId,
            new(Guid.NewGuid(), draft.Fields.Title, agentIds, draft.Event.OccurrenceId), draft.Event.Authority.PrincipalId, ct,
            sourceActionKey: draft.IdempotencyKey, flowRunId: draft.RunId);
        // Request identity is stable for this occurrence and action. Recovery after a crash between create and admission is safe.
        await investigations.AcceptMessageAsync(draft.TenantId, row.Id,
            new(draft.Event.EventId, "Investigate the linked monitoring alert for the selected systems. Gather evidence with permitted NetRatel tools, explain the likely cause, and propose remediation. Request operator approval before changing systems.\n\nAlert description:\n" + draft.Fields.Description),
            draft.Event.Authority.PrincipalId, draft.Event.Authority.IntegrationCredentialId, true, ct);
        return new(FlowIncidentActionResultKind.Succeeded, "ai-investigation-queued", new(row.Id.ToString("D")));
    }
}

public sealed class NetclawConnectorCatalog(NotificationAndIncidentConnectorCatalog existing, NetclawFlowConnector netclaw) : IFlowConnectorCatalog
{
    public async Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid connectorId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) =>
        await existing.GetAsync(tenantId, connectorId, authority, cancellationToken) ?? await netclaw.GetAsync(tenantId, connectorId, authority, cancellationToken);
    public async Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) =>
        (await existing.ListAsync(tenantId, authority, cancellationToken)).Concat(await netclaw.ListAsync(tenantId, authority, cancellationToken)).ToArray();
}
