using NetRatel.Application.RatelDesk;
using NetRatel.Shared.SystemPairing;
namespace NetRatel.Infrastructure.RatelDesk;
public sealed class RatelDeskConnectorReadiness(IRatelDeskConnectorAuthorization authorization,
    IRatelDeskConnectorBindingStore bindings, IRatelDeskProducerContinuity source,
    IRatelDeskOutboundBindingResolver currentBindings, RatelDeskReceiverNetworkPolicy network, TimeProvider clock) : IRatelDeskConnectorReadiness
{
    public static readonly TimeSpan MaximumObservationAge = TimeSpan.FromMinutes(2);
    public async Task<(bool Available, string Code)> CurrentAsync(RatelDeskConnectorState state, CancellationToken ct)
    {
        if (!state.Configuration.Enabled || !await authorization.CanExecuteAsync(state.OwnerPrincipalId, null, state.TenantId, ct)) return (false, "connector-disabled-or-owner-denied");
        var observed = state.Readiness;
        if (observed?.Peer is null || observed.Capability is null || observed.Capability.Endpoints is null || observed.Peer.CategoryIds is null || observed.ConnectorRevision != state.Revision) return (false, "receiver-readiness-required");
        var now = clock.GetUtcNow();
        if (observed.TargetValidatedAtUtc > now || observed.Capability.ObservedAtUtc > observed.TargetValidatedAtUtc || observed.TargetValidatedAtUtc - observed.Capability.ObservedAtUtc > TimeSpan.FromSeconds(20)) return (false, "receiver-readiness-expired");
        try
        {
            var authentication = await bindings.GetAuthenticationAsync(state.TenantId, state.Id, ct); var peer = observed.Peer;
            if (authentication.Mode != RatelDeskAuthenticationMode.PairedSystem || authentication.ManagedLinkId != peer.LinkId || network.ValidateApprovedApiBase(peer.Mode, state.Configuration.Origin) != peer.ApiBaseUrl) return (false, "receiver-semantic-target-changed");
            await source.RequireCurrentAsync(peer.SourceInstanceId, ct);
            var current = await currentBindings.CaptureAsync(state, authentication, peer.SourceInstanceId, ct);
            if (!PairingRatelDeskBindingResolver.Same(current, peer)) return (false, "receiver-paired-grant-changed");
            if (!ReceiverWireValidation.ValidCapability(observed.Capability, peer)) return (false, "receiver-readiness-invalid");
            return (true, "receiver-ready");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or PairingException or ArgumentException or InvalidOperationException) { return (false, "receiver-current-authority-unavailable"); }
    }
}
