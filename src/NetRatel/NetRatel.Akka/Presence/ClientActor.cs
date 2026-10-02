using Akka.Actor;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Presence;

namespace NetRatel.Akka.Presence;

/// <summary>
/// Per-client aggregate. The current router delegates only
/// presence messages to its PresenceActor child.
/// </summary>
public sealed class ClientActor : ReceiveActor
{
    private readonly IActorRef _presence;

    public ClientActor(
        ClientKey client,
        NetRatelAkkaOptions options,
        IActorRef presenceReadModel,
        IClientConnectionEpochStore? epochStore = null)
    {
        _presence = Context.ActorOf(PresenceActor.Props(client, options, presenceReadModel, epochStore), "presence");
        Receive<IClientPresenceMessage>(message => _presence.Forward(message));
    }

    public static Props Props(
        ClientKey client,
        NetRatelAkkaOptions options,
        IActorRef presenceReadModel,
        IClientConnectionEpochStore? epochStore = null) =>
        global::Akka.Actor.Props.Create(() => new ClientActor(client, options, presenceReadModel, epochStore));

    public static Props Props(ClientKey client, NetRatelAkkaOptions options) =>
        Props(client, options, ActorRefs.Nobody);
}
