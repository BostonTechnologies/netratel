using System.Collections.Immutable;
using NetRatel.API.Gateway;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;

namespace NetRatel.API.Services.Monitoring;

/// <summary>Combines scoped eligible-agent storage with the actual active authenticated telemetry registration.</summary>
public sealed class MonitoringClientDirectory(IMonitoringAgentEligibility eligibility, IClientPresenceReadModel presence,
    IAgentTelemetryGatewaySessionRegistry sessions) : IMonitoringClientDirectory
{
    public async Task<ImmutableArray<Guid>> GetEligibleAgentsAsync(int tenantId, CancellationToken cancellationToken)
    {
        return await eligibility.GetEligibleAgentsAsync(tenantId, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> IsEligibleAsync(ClientKey client, CancellationToken cancellationToken) =>
        eligibility.IsEligibleAsync(client, cancellationToken);

    public async Task<MonitoringEvidenceFence?> GetCurrentEvidenceAsync(ClientKey client, CancellationToken cancellationToken)
    {
        var session = sessions.GetStatus(client);
        if (!session.Connected || session.ConnectionId is not Guid connectionId ||
            session.ConnectionEpoch is not long epoch || session.RegistrationId is not Guid registrationId) return null;
        var active = await presence.GetClientSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (active is null || active.Client != client || !active.IsAuthoritative || active.Status != ClientPresenceStatus.Online ||
            active.ConnectionId != connectionId || active.ConnectionEpoch != epoch ||
            sessions.GetStatus(client).RegistrationId != registrationId) return null;
        return new(client, connectionId, epoch, registrationId);
    }
}
