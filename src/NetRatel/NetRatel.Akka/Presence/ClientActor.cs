using Akka.Actor;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Presence;

namespace NetRatel.Akka.Presence;

/// <summary>
/// Foundation for the future per-client aggregate. Phase 1 delegates only
/// presence messages to its PresenceActor child.
/// </summary>
public sealed class ClientActor : ReceiveActor
{
    private readonly IActorRef _presence;

    public ClientActor(
        ClientKey client,
        NetRatelAkkaMigrationOptions options,
        IActorRef presenceReadModel)
    {
        _presence = Context.ActorOf(PresenceActor.Props(client, options, presenceReadModel), "presence");
        Receive<IClientPresenceMessage>(message => _presence.Forward(message));
    }

    public static Props Props(
        ClientKey client,
        NetRatelAkkaMigrationOptions options,
        IActorRef presenceReadModel) =>
        global::Akka.Actor.Props.Create(() => new ClientActor(client, options, presenceReadModel));

    public static Props Props(ClientKey client, NetRatelAkkaMigrationOptions options) =>
        Props(client, options, ActorRefs.Nobody);
}
