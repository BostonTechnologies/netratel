using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;

namespace NetRatel.Akka.Services;

/// <summary>Bounded single-node tenant/agent region. Passivation buffers arrivals until durable state can be reloaded.</summary>
public sealed class ClientServicesRouterActor : ReceiveActor
{
    public const int DefaultMaximumActiveClients = 1024;
    private const int MaximumPassivationQueue = 16;
    private readonly IClientServicesStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly int _maximumActiveClients;
    private readonly Dictionary<ClientKey, Route> _routes = [];

    public ClientServicesRouterActor(IClientServicesStore store, TimeProvider timeProvider,
        int maximumActiveClients, TimeSpan idleTimeout)
    {
        if (maximumActiveClients <= 0) throw new ArgumentOutOfRangeException(nameof(maximumActiveClients));
        if (idleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        _store = store;
        _timeProvider = timeProvider;
        _maximumActiveClients = maximumActiveClients;
        _idleTimeout = idleTimeout;
        Receive<IClientServicesMessage>(message => RouteMessage(message, Sender));
        Receive<RoutedServicesReply>(message => message.ReplyTo.Tell(message.Result));
        Receive<RequestServicesPassivation>(Passivate);
        Receive<Terminated>(HandleTerminated);
        Receive<GetClientServicesRouteDiagnostics>(_ => Sender.Tell(new ClientServicesRouteDiagnostics(
            _routes.Count, _routes.Values.Sum(route => route.Pending.Count))));
    }

    public static Props Props(IClientServicesStore store, TimeProvider? timeProvider = null,
        int maximumActiveClients = DefaultMaximumActiveClients, TimeSpan? idleTimeout = null) =>
        global::Akka.Actor.Props.Create(() => new ClientServicesRouterActor(store, timeProvider ?? TimeProvider.System,
            maximumActiveClients, idleTimeout ?? TimeSpan.FromMinutes(15)));

    private void RouteMessage(IClientServicesMessage message, IActorRef replyTo)
    {
        if (!message.Client.IsValid)
        {
            Reject(message, replyTo, ClientServicesMessageDisposition.Invalid);
            return;
        }
        if (!_routes.TryGetValue(message.Client, out var route))
        {
            if (_routes.Count >= _maximumActiveClients)
            {
                Reject(message, replyTo, ClientServicesMessageDisposition.CapacityExceeded);
                return;
            }
            var actor = Context.ActorOf(ClientServicesActor.Props(message.Client, _store, _timeProvider, _idleTimeout),
                CreateActorName(message.Client));
            Context.Watch(actor);
            _routes.Add(message.Client, route = new(actor));
        }
        if (route.Passivating)
        {
            if (route.Pending.Count >= MaximumPassivationQueue)
                Reject(message, replyTo, ClientServicesMessageDisposition.CapacityExceeded);
            else route.Pending.Enqueue(new(message, replyTo));
            return;
        }
        route.Generation = checked(route.Generation + 1);
        route.Actor.Tell(new RoutedServicesMessage(message, replyTo, route.Generation));
    }

    private void Passivate(RequestServicesPassivation request)
    {
        var route = _routes.Values.FirstOrDefault(candidate => candidate.Actor.Equals(request.Entity));
        if (route is null || route.Passivating || route.Generation != request.Generation) return;
        route.Passivating = true;
        Context.Stop(route.Actor);
    }

    private void HandleTerminated(Terminated message)
    {
        var entry = _routes.FirstOrDefault(candidate => candidate.Value.Actor.Equals(message.ActorRef));
        if (entry.Value is null) return;
        _routes.Remove(entry.Key);
        foreach (var pending in entry.Value.Pending) RouteMessage(pending.Message, pending.ReplyTo);
    }

    private static void Reject(IClientServicesMessage message, IActorRef replyTo, ClientServicesMessageDisposition disposition) =>
        replyTo.Tell(message is RecordClientServicesChunk
            ? new ClientServicesMessageResult(message.Client, disposition, 0)
            : new Status.Failure(new InvalidOperationException(disposition == ClientServicesMessageDisposition.CapacityExceeded
                ? "Services projection capacity is temporarily exhausted." : "Invalid authenticated client.")));

    private static string CreateActorName(ClientKey client) =>
        "services-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.EntityId))).ToLowerInvariant();

    private sealed class Route(IActorRef actor)
    {
        public IActorRef Actor { get; } = actor;
        public long Generation { get; set; }
        public bool Passivating { get; set; }
        public Queue<PendingRoute> Pending { get; } = new();
    }

    private sealed record PendingRoute(IClientServicesMessage Message, IActorRef ReplyTo);
}

public sealed record GetClientServicesRouteDiagnostics;
public sealed record ClientServicesRouteDiagnostics(int ActiveClientActors, int BufferedRequests);
