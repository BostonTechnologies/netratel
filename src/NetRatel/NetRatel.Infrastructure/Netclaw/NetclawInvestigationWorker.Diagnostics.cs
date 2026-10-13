using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed partial class NetclawInvestigationWorker
{
    // Reading the existing owner never negotiates a session. Another API replica cannot observe this owner.
    public NetclawConnectionDiagnosticsDto ConnectionDiagnostics(NetclawConnectionDto connection)
    {
        var observed = clock.GetUtcNow();
        var available = ready;
        var sessions = available && connection.Enabled
            ? owners.Where(pair => !pair.Value.Retired && pair.Value.Profile.TenantId == connection.TenantId &&
                    pair.Value.Profile.Id == connection.Id && pair.Value.Profile.Revision == connection.Revision)
                .Select(pair => new NetclawSessionObservationDto(pair.Key, pair.Value.Client.ConnectionState,
                    pair.Value.AuthenticatedAtUtc,
                    new DateTimeOffset(Interlocked.Read(ref pair.Value.LastObservedTicks), TimeSpan.Zero)))
                .OrderByDescending(session => session.LastActivityUtc).ToArray()
            : [];
        return new(connection.TenantId, connection.Id, connection.Revision, available, observed, sessions);
    }
}
