using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.ServiceLinks;

namespace NetRatel.Infrastructure.RatelDesk;

/// <summary>A bounded authenticated read observation plus current durable authority; never performs HTTP on catalog reads.</summary>
public sealed class RatelDeskConnectorReadiness(IRatelDeskConnectorAuthorization authorization,
    IRatelDeskConnectorBindingStore bindings, IRatelDeskProducerContinuity source,
    ServiceLinkProfileService profiles, RatelDeskReceiverNetworkPolicy network, TimeProvider clock)
    : IRatelDeskConnectorReadiness
{
    public static readonly TimeSpan MaximumObservationAge = TimeSpan.FromMinutes(2);
    public async Task<(bool Available, string Code)> CurrentAsync(RatelDeskConnectorState state, CancellationToken ct)
    {
        if (!state.Configuration.Enabled || !await authorization.CanExecuteAsync(state.OwnerPrincipalId, null, state.TenantId, ct))
            return (false, "connector-disabled-or-owner-denied");
        var observed = state.Readiness;
        if (observed is null || observed.Peer is null || observed.Capability is null ||
            observed.Capability.Endpoints is null || observed.Peer.CategoryIds is null || observed.ConnectorRevision != state.Revision)
            return (false, "receiver-readiness-required");
        var now = clock.GetUtcNow();
        if (observed.TargetValidatedAtUtc > now || now - observed.TargetValidatedAtUtc > MaximumObservationAge ||
            observed.Capability.ObservedAtUtc > observed.TargetValidatedAtUtc ||
            observed.TargetValidatedAtUtc - observed.Capability.ObservedAtUtc > TimeSpan.FromSeconds(20))
            return (false, "receiver-readiness-expired");
        try
        {
            var authentication = await bindings.GetAuthenticationAsync(state.TenantId, state.Id, ct);
            var peer = observed.Peer;
            if (authentication.Mode != peer.Mode || authentication.ManagedLinkId != peer.LinkId ||
                peer.ConnectorId != state.Id || peer.LocalTenantId != state.TenantId ||
                network.ValidateApprovedApiBase(peer.Mode, state.Configuration.Origin) != peer.ApiBaseUrl ||
                peer.OrganizationId != state.Configuration.OrganizationId || peer.CustomerId != state.Configuration.CustomerId ||
                peer.AssignedToId != state.Configuration.AssignedToId ||
                !state.Configuration.CategoryIds.Select(x => x.ToString("D")).Order(StringComparer.Ordinal)
                    .SequenceEqual(peer.CategoryIds, StringComparer.Ordinal))
                return (false, "receiver-semantic-target-changed");
            await source.RequireCurrentAsync(peer.SourceInstanceId, ct);
            if (peer.Mode == RatelDeskAuthenticationMode.ManagedServiceLink)
            {
                foreach (var scope in new[] { "rateldesk.incidents.create", "rateldesk.incident-receipts.read", "rateldesk.incident-targets.read" })
                {
                    var profile = await profiles.ResolveAsync(state.TenantId, peer.LinkId!, scope, ct);
                    if (profile.LinkRevision != peer.LinkRevision || profile.GrantHash != peer.GrantHash ||
                        profile.PeerInstanceId != peer.ReceiverInstanceId || profile.PeerTenantId != peer.PeerTenantId ||
                        profile.Peer.ApiBaseUrl != peer.ApiBaseUrl || profile.SourceInstanceId != peer.SourceInstanceId.ToString("D") ||
                        profile.SourceNamespaceId != peer.SourceNamespaceId.ToString("D") ||
                        profile.Credential.Issuer != peer.Issuer || profile.Credential.Audience != peer.Audience ||
                        profile.Credential.TokenEndpoint != peer.TokenEndpoint || profile.Credential.ClientId != peer.ClientId ||
                        profile.Grant.DirectionId != peer.DirectionId)
                        return (false, "receiver-managed-grant-changed");
                }
            }
            else if (state.ProtectedCredential is null) return (false, "manual-api-bearer-required");
            if (!ReceiverWireValidation.ValidCapability(observed.Capability, peer) || observed.Capability.ContractVersion != ReceiverWireValidation.Contract ||
                observed.Capability.ReceiverInstanceId != peer.ReceiverInstanceId ||
                observed.Capability.SourceInstanceId != peer.SourceInstanceId || observed.Capability.SourceNamespaceId != peer.SourceNamespaceId)
                return (false, "receiver-readiness-invalid");
            return (true, "receiver-ready");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or ServiceLinkProtocolException or ArgumentException or InvalidOperationException)
        { return (false, "receiver-current-authority-unavailable"); }
    }
}
