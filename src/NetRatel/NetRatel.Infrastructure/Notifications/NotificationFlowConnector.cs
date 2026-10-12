using System.Security.Cryptography;
using NetRatel.Application.Flows;
using NetRatel.Application.Notifications;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Notifications;

namespace NetRatel.Infrastructure.Notifications;

public sealed class NotificationFlowConnector(INotificationConnectorStore store, IRatelDeskConnectorAuthorization authorization,
    INotificationCredentialProtector protector, INotificationWebhookTransport transport) : IFlowConnectorCatalog, IFlowNotificationActionDispatcher
{
    public async Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid connectorId, FlowExecutionAuthorityDto authority,
        CancellationToken cancellationToken = default)
    {
        if (!await AllowedAsync(authority, tenantId, cancellationToken).ConfigureAwait(false)) return null;
        var state = await store.GetAsync(tenantId, connectorId, cancellationToken).ConfigureAwait(false);
        return state is null || state.DeletedAtUtc is not null ? null : await ReferenceAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto authority,
        CancellationToken cancellationToken = default)
    {
        if (!await AllowedAsync(authority, tenantId, cancellationToken).ConfigureAwait(false)) return [];
        var references = new List<FlowConnectorReferenceDto>();
        foreach (var state in await store.ListAsync(tenantId, cancellationToken).ConfigureAwait(false))
            references.Add(await ReferenceAsync(state, cancellationToken).ConfigureAwait(false));
        return references;
    }

    public async Task<FlowIncidentActionResult> DispatchAsync(FlowIncidentActionDraft draft, CancellationToken cancellationToken = default)
    {
        if (draft is null || draft.Event is null || draft.Fields is null ||
            draft.SourceInstanceId == Guid.Empty || draft.RunId == Guid.Empty || draft.ActionNodeId == Guid.Empty || draft.ConnectorId == Guid.Empty ||
            draft.IdempotencyKey is not { Length: > 0 and <= 256 } || draft.IdempotencyKey.Any(char.IsControl) ||
            draft.Kind is not (FlowActionKind.Discord or FlowActionKind.Slack) || draft.TenantId != draft.Event.TenantId ||
            !FlowContractValidation.ValidEnvelope(draft.Event) || draft.ConnectorRevision <= 0 ||
            draft.Fields.Title is not { Length: > 0 and <= FlowLimits.MaximumTitleLength } ||
            draft.Fields.Description is not { Length: > 0 and <= FlowLimits.MaximumDescriptionLength })
            return new(FlowIncidentActionResultKind.Failed, "invalid-notification-action");
        if (!await AllowedAsync(draft.Event.Authority, draft.TenantId, cancellationToken).ConfigureAwait(false))
            return new(FlowIncidentActionResultKind.Failed, "notification-current-authority-denied");
        var state = await store.GetAsync(draft.TenantId, draft.ConnectorId, cancellationToken).ConfigureAwait(false);
        if (state is null || state.DeletedAtUtc is not null || state.Revision != draft.ConnectorRevision || Kind(state.Configuration.Provider) != draft.Kind)
            return new(FlowIncidentActionResultKind.Unavailable, "notification-connector-revision-unavailable");
        if (!await UsableAsync(state, cancellationToken).ConfigureAwait(false))
            return new(FlowIncidentActionResultKind.Unavailable, "notification-connector-disabled-or-owner-denied");
        NotificationDeliveryResult result;
        try
        {
            result = await transport.SendAsync(state.Configuration.Provider,
                protector.Unprotect(state.TenantId, state.Id, state.ProtectedWebhookUrl), NotificationMessageFormatter.Format(draft),
                state.Configuration.DiscordUsername, state.Configuration.DiscordThreadId, cancellationToken).ConfigureAwait(false);
        }
        catch (CryptographicException) { return new(FlowIncidentActionResultKind.Unavailable, "notification-credential-unavailable"); }
        return result.Status switch
        {
            NotificationDeliveryStatus.Succeeded => new(FlowIncidentActionResultKind.Succeeded, result.Code,
                new(result.MessageId ?? "webhook-accepted")),
            NotificationDeliveryStatus.RateLimited => new(FlowIncidentActionResultKind.RetryableSafe, result.Code, RetryAfter: result.RetryAfter),
            NotificationDeliveryStatus.DeliveryUnknown => new(FlowIncidentActionResultKind.DeliveryUnknown, result.Code),
            NotificationDeliveryStatus.Unavailable => new(FlowIncidentActionResultKind.Unavailable, result.Code),
            _ => new(FlowIncidentActionResultKind.Failed, result.Code)
        };
    }

    private Task<bool> AllowedAsync(FlowExecutionAuthorityDto authority, int tenantId, CancellationToken ct) =>
        authorization.CanExecuteAsync(authority.PrincipalId, authority.IntegrationCredentialId, tenantId, ct);
    private Task<bool> UsableAsync(NotificationConnectorState state, CancellationToken ct) =>
        state.DeletedAtUtc is null && state.Configuration.Enabled && !string.IsNullOrWhiteSpace(state.ProtectedWebhookUrl)
            ? authorization.CanExecuteAsync(state.OwnerPrincipalId, null, state.TenantId, ct) : Task.FromResult(false);
    private async Task<FlowConnectorReferenceDto> ReferenceAsync(NotificationConnectorState state, CancellationToken ct)
    {
        var usable = await UsableAsync(state, ct).ConfigureAwait(false);
        return new(state.Id, state.TenantId, state.Configuration.Name, state.Configuration.Enabled, usable,
            usable ? null : "notification-connector-disabled-or-owner-denied", state.Revision) { Kind = Kind(state.Configuration.Provider) };
    }
    private static FlowActionKind Kind(NotificationProvider provider) => provider switch
    {
        NotificationProvider.Discord => FlowActionKind.Discord,
        NotificationProvider.Slack => FlowActionKind.Slack,
        _ => throw new InvalidOperationException("invalid-notification-provider")
    };
}

/// <summary>Combines native provider catalogs without routing webhook actions through the incident receiver.</summary>
public sealed class NotificationAndIncidentConnectorCatalog(RatelDeskFlowConnector incidents, NotificationFlowConnector notifications)
    : IFlowConnectorCatalog
{
    public async Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid connectorId, FlowExecutionAuthorityDto authority,
        CancellationToken cancellationToken = default) =>
        await incidents.GetAsync(tenantId, connectorId, authority, cancellationToken).ConfigureAwait(false) ??
        await notifications.GetAsync(tenantId, connectorId, authority, cancellationToken).ConfigureAwait(false);
    public async Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto authority,
        CancellationToken cancellationToken = default)
    {
        var incidentTargets = await incidents.ListAsync(tenantId, authority, cancellationToken).ConfigureAwait(false);
        var notificationTargets = await notifications.ListAsync(tenantId, authority, cancellationToken).ConfigureAwait(false);
        return incidentTargets.Concat(notificationTargets).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
