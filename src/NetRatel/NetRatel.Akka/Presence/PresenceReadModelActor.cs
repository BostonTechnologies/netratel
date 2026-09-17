using Akka.Actor;
using NetRatel.Application.Presence;

namespace NetRatel.Akka.Presence;

/// <summary>
/// Owns the process-local, queryable projection of gateway presence. It is
/// populated only by active client presence actors and intentionally does not
/// probe arbitrary client keys, which would create empty actors.
/// </summary>
public sealed class PresenceReadModelActor : ReceiveActor
{
    private readonly Dictionary<ClientKey, ClientPresenceSnapshot> _snapshots = new();
    private long _revision;

    public PresenceReadModelActor()
    {
        Receive<TrackClientPresenceSnapshot>(message =>
        {
            _snapshots[message.Snapshot.Client] = message.Snapshot;
            _revision = checked(_revision + 1);
        });
        Receive<GetClientPresenceReadModel>(_ => Sender.Tell(new ClientPresenceReadModelSnapshot(
            _revision,
            _snapshots.Values
                .OrderBy(snapshot => snapshot.Client.TenantId)
                .ThenBy(snapshot => snapshot.Client.AgentId)
                .ToArray())));
    }

    public static Props Props() => global::Akka.Actor.Props.Create(() => new PresenceReadModelActor());
}
