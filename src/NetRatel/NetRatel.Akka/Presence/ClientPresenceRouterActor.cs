using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Observability;
using NetRatel.Application.Presence;

namespace NetRatel.Akka.Presence;

/// <summary>
/// Single-node equivalent of the future ClientActor shard region. The same
/// TenantId:AgentId entity identifier is retained when clustering is added.
/// </summary>
public sealed class ClientPresenceRouterActor : ReceiveActor
{
    private readonly NetRatelAkkaMigrationOptions _options;
    private readonly IActorRef _presenceReadModel;
    private readonly ClientPresenceMessageExtractor _extractor = new();
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    public ClientPresenceRouterActor(
        NetRatelAkkaMigrationOptions options,
        IActorRef presenceReadModel)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _presenceReadModel = presenceReadModel ?? throw new ArgumentNullException(nameof(presenceReadModel));

        Receive<IClientPresenceMessage>(message => GetClientActor(message).Forward(message));
        Receive<ProbeClientPresenceRoute>(_ =>
        {
            var clients = Context.GetChildren().Count();
            NetRatelAkkaTelemetry.SetPresenceActiveClients(clients);
            Sender.Tell(new ClientPresenceRouteStatus(
                clients,
                _startedAtUtc,
                _options.IsPresenceAuthorityActive ? "akka" : "unavailable"));
        });
    }

    public static Props Props(
        NetRatelAkkaMigrationOptions options,
        IActorRef presenceReadModel) =>
        global::Akka.Actor.Props.Create(() => new ClientPresenceRouterActor(options, presenceReadModel));

    private IActorRef GetClientActor(IClientPresenceMessage message)
    {
        var entityId = _extractor.EntityId(message);
        var actorName = CreateActorName(entityId);
        var existing = Context.Child(actorName);
        if (!existing.IsNobody())
        {
            return existing;
        }

        using var activity = NetRatelAkkaTelemetry.StartActivity("akka.actor.create", "presence");
        var actor = Context.ActorOf(ClientActor.Props(message.Client, _options, _presenceReadModel), actorName);
        NetRatelAkkaTelemetry.SetPresenceActiveClients(Context.GetChildren().Count());
        return actor;
    }

    private static string CreateActorName(string entityId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(entityId));
        return $"client-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
