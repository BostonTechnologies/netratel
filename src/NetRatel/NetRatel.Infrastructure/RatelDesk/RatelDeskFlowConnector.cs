using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

/// <summary>Preparation is read-only. The #145 flow store persists its exact result before dispatch acquires any side effect.</summary>
public sealed class RatelDeskFlowConnector(IRatelDeskConnectorStore store, IRatelDeskConnectorAuthorization authorization,
    IRatelDeskConnectorReadiness? readiness = null) : IFlowConnectorCatalog, IFlowIncidentActionDispatcher
{
    public async Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid connectorId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default)
    {
        if (!await AllowedAsync(authority, tenantId, cancellationToken).ConfigureAwait(false)) return null;
        var state = await store.GetAsync(tenantId, connectorId, cancellationToken).ConfigureAwait(false);
        return state is null ? null : await ReferenceAsync(state, cancellationToken).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default)
    {
        if (!await AllowedAsync(authority, tenantId, cancellationToken).ConfigureAwait(false)) return [];
        var results = new List<FlowConnectorReferenceDto>();
        foreach (var state in await store.ListAsync(tenantId, cancellationToken).ConfigureAwait(false))
            results.Add(await ReferenceAsync(state, cancellationToken).ConfigureAwait(false));
        return results;
    }
    public async Task<FlowIncidentPreparationResult> PrepareAsync(FlowIncidentActionDraft draft, CancellationToken cancellationToken = default)
    {
        if (!FlowContractValidation.ValidEnvelope(draft.Event) || draft.Event.TenantId != draft.TenantId || draft.SourceInstanceId == Guid.Empty || draft.RunId == Guid.Empty || draft.ActionNodeId == Guid.Empty ||
            draft.Event.EventId == Guid.Empty || draft.Event.OccurrenceId == Guid.Empty || draft.Event.FlowVersionId == Guid.Empty ||
            draft.IdempotencyKey is not { Length: > 0 and <= 256 } || draft.IdempotencyKey.Any(char.IsControl))
            return new(FlowIncidentPreparationStatus.Invalid, Code: "invalid-incident-action");
        if (!await AllowedAsync(draft.Event.Authority, draft.TenantId, cancellationToken).ConfigureAwait(false))
            return new(FlowIncidentPreparationStatus.Denied, Code: "connector-current-authority-denied");
        var state = await store.GetAsync(draft.TenantId, draft.ConnectorId, cancellationToken).ConfigureAwait(false);
        if (state is null || state.Revision != draft.ConnectorRevision) return new(FlowIncidentPreparationStatus.Unavailable, Code: "connector-revision-unavailable");
        if (!await IsUsableAsync(state, cancellationToken).ConfigureAwait(false)) return new(FlowIncidentPreparationStatus.Unavailable, Code: "connector-disabled-or-owner-denied");
        try
        {
            var priority = draft.Fields.Priority ?? MapPriority(state.Configuration.Priorities, draft.Event.Data.Severity);
            var description = draft.Fields.Description + "\n\nNetRatel monitoring occurrence\n" +
                $"Client: {draft.Event.Data.ClientName} ({draft.Event.Data.AgentId:D})\nResource: {draft.Event.Data.Resource}\n" +
                $"Rule: {draft.Event.Data.RuleName} ({draft.Event.Data.RuleId:D})\nSeverity: {draft.Event.Data.Severity}\n" +
                $"Metric: {draft.Event.Data.Metric}\nObserved value: {draft.Event.Data.NumericValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? draft.Event.Data.ServiceState ?? "Unknown"}\n" +
                $"Observed at: {draft.Event.Data.ObservedAtUtc:O}\nOccurred at: {draft.Event.OccurredAtUtc:O}\n" +
                $"Source: {draft.SourceInstanceId:D}/{draft.Event.OccurrenceId:D}/{draft.Event.EventId:D}";
            var payload = RatelDeskPayload.Create(state.Configuration, draft.Fields.Title, description, priority);
            var target = new FlowIncidentTargetDto(payload.OrganizationId, payload.CustomerId, payload.AssignedToId, payload.CategoryIds);
            var action = new FlowIncidentActionRequest(draft.TenantId, draft.RunId, draft.ActionNodeId, draft.ConnectorId,
                state.Revision, draft.SourceInstanceId, draft.IdempotencyKey, draft.Event,
                new(payload.Title, payload.Description, payload.Priority), target, false, "");
            action = action with { SemanticFingerprint = FlowContractValidation.Fingerprint(action) };
            if (!FlowContractValidation.ValidPrepared(action, draft)) return new(FlowIncidentPreparationStatus.Invalid, Code: "invalid-incident-payload");
            return new(FlowIncidentPreparationStatus.Ready, action, RatelDeskConnectorLimits.ReceiverUnavailableCode);
        }
        catch (ArgumentException) { return new(FlowIncidentPreparationStatus.Invalid, Code: "invalid-incident-payload"); }
    }
    public async Task<FlowIncidentActionResult> DispatchAsync(FlowIncidentActionRequest action, CancellationToken cancellationToken = default)
    {
        if (action.TenantId != action.Event.TenantId || !await AllowedAsync(action.Event.Authority, action.TenantId, cancellationToken).ConfigureAwait(false))
            return new(FlowIncidentActionResultKind.Failed, "connector-current-authority-denied");
        var current = await store.GetAsync(action.TenantId, action.ConnectorId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Revision != action.ConnectorRevision) return new(FlowIncidentActionResultKind.Unavailable, "connector-revision-unavailable");
        if (!await IsUsableAsync(current, cancellationToken).ConfigureAwait(false)) return new(FlowIncidentActionResultKind.Unavailable, "connector-disabled-or-owner-denied");
        var payload = new RatelDeskCreateIncidentDto(action.Fields.Title, action.Fields.Description, action.Fields.Priority ?? -1,
            action.Target.CustomerId, action.Target.OrganizationId, action.Target.AssignedToId, action.Target.CategoryIds);
        if (payload.Priority is < 0 or > 3 || payload.OrganizationId != current.Configuration.OrganizationId || payload.CustomerId != current.Configuration.CustomerId ||
            payload.AssignedToId != current.Configuration.AssignedToId || !payload.CategoryIds.SequenceEqual(current.Configuration.CategoryIds.Order()) ||
            FlowContractValidation.Fingerprint(action) != action.SemanticFingerprint)
            return new(FlowIncidentActionResultKind.Failed, "persisted-incident-payload-invalid");
        // No HTTP POST, invented header or invented capability route can turn the existing normal-create API into safe replay.
        return new(FlowIncidentActionResultKind.Unavailable, RatelDeskConnectorLimits.ReceiverUnavailableCode);
    }

    private async Task<FlowConnectorReferenceDto> ReferenceAsync(RatelDeskConnectorState state, CancellationToken cancellationToken)
    {
        var usable = await IsUsableAsync(state, cancellationToken).ConfigureAwait(false);
        var current = readiness is null ? (Available: false, Code: usable ? RatelDeskConnectorLimits.ReceiverUnavailableCode : "connector-disabled-or-owner-denied") :
            await readiness.CurrentAsync(state, cancellationToken).ConfigureAwait(false);
        return new(state.Id, state.TenantId, state.Configuration.Name, state.Configuration.Enabled, current.Available, current.Code, state.Revision);
    }
    private Task<bool> AllowedAsync(FlowExecutionAuthorityDto authority, int tenantId, CancellationToken cancellationToken) =>
        authorization.CanExecuteAsync(authority.PrincipalId, authority.IntegrationCredentialId, tenantId, cancellationToken);
    private async Task<bool> IsUsableAsync(RatelDeskConnectorState state, CancellationToken cancellationToken) =>
        state.Configuration.Enabled && state.Authentication?.Mode == RatelDeskAuthenticationMode.PairedSystem &&
        await authorization.CanExecuteAsync(state.OwnerPrincipalId, null, state.TenantId, cancellationToken).ConfigureAwait(false);
    private static int MapPriority(RatelDeskPriorityMapping mapping, string severity) => severity.ToLowerInvariant() switch
    { "critical" => mapping.Critical, "error" or "high" => mapping.Error, "warning" or "medium" => mapping.Warning, _ => mapping.Information };
}
