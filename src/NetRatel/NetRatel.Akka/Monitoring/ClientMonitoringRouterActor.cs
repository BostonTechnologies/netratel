using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;

namespace NetRatel.Akka.Monitoring;

/// <summary>Bounded local region. Authoritative arrivals exceeding capacity receive an explicit retryable rejection.</summary>
public sealed class ClientMonitoringRouterActor : ReceiveActor
{
    public const int MaximumOutstandingPerClient = 16;
    public const int DefaultMaximumActiveClients = 1024;
    private readonly IMonitoringStore _store;
    private readonly IMonitoringConfigurationStore _configurations;
    private readonly IMonitoringClientDirectory _directory;
    private readonly TimeProvider _time;
    private readonly int _maximumClients;
    private readonly TimeSpan _idleTimeout;
    private readonly Dictionary<ClientKey, Route> _routes = [];
    public ClientMonitoringRouterActor(IMonitoringStore store, IMonitoringConfigurationStore configurations, IMonitoringClientDirectory directory,
        TimeProvider time, int maximumClients, TimeSpan idleTimeout)
    {
        if (maximumClients <= 0 || idleTimeout <= TimeSpan.Zero) throw new ArgumentException("invalid_monitoring_region_capacity");
        _store = store; _configurations = configurations; _directory = directory; _time = time; _maximumClients = maximumClients; _idleTimeout = idleTimeout;
        Receive<IMonitoringClientMessage>(message => RouteMessage(message, Sender));
        Receive<RoutedMonitoringReply>(reply =>
        {
            if (_routes.TryGetValue(reply.Client, out var route) && route.Actor.Equals(Sender)) route.Outstanding--;
            reply.ReplyTo.Tell(reply.Result);
        });
        Receive<RequestMonitoringPassivation>(request =>
        {
            var route = _routes.Values.SingleOrDefault(route => route.Actor.Equals(request.Entity));
            if (route is null || route.Passivating || route.Generation != request.Generation || route.Outstanding != 0) return;
            route.Passivating = true; Context.Stop(route.Actor);
        });
        Receive<Terminated>(terminated =>
        {
            var entry = _routes.FirstOrDefault(entry => entry.Value.Actor.Equals(terminated.ActorRef));
            if (entry.Value is null) return;
            _routes.Remove(entry.Key);
            foreach (var pending in entry.Value.Pending) RouteMessage(pending.Message, pending.ReplyTo);
        });
        Receive<GetMonitoringRouteDiagnostics>(_ => Sender.Tell(new MonitoringRouteDiagnostics(_routes.Count, _routes.Values.Sum(route => route.Outstanding + route.Pending.Count))));
    }
    public static Props Props(IMonitoringStore store, IMonitoringConfigurationStore configurations, IMonitoringClientDirectory directory,
        TimeProvider? time = null, int maximumClients = DefaultMaximumActiveClients, TimeSpan? idleTimeout = null) =>
        global::Akka.Actor.Props.Create(() => new ClientMonitoringRouterActor(store, configurations, directory, time ?? TimeProvider.System, maximumClients, idleTimeout ?? TimeSpan.FromMinutes(15)));
    private void RouteMessage(IMonitoringClientMessage message, IActorRef replyTo)
    {
        if (!message.Client.IsValid) { Reject(message, replyTo, false); return; }
        if (!_routes.TryGetValue(message.Client, out var route))
        {
            if (_routes.Count >= _maximumClients) { Reject(message, replyTo, true); return; }
            var name = "monitoring-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Client.EntityId))).ToLowerInvariant();
            var actor = Context.ActorOf(ClientMonitoringActor.Props(message.Client, _store, _configurations, _directory, _time, _idleTimeout), name);
            Context.Watch(actor); _routes.Add(message.Client, route = new(actor));
        }
        if (route.Outstanding + route.Pending.Count >= MaximumOutstandingPerClient) { Reject(message, replyTo, true); return; }
        if (route.Passivating) { route.Pending.Enqueue(new(message, replyTo)); return; }
        route.Outstanding++; route.Generation = checked(route.Generation + 1);
        route.Actor.Tell(new RoutedMonitoringMessage(message, replyTo, route.Generation));
    }
    private static void Reject(IMonitoringClientMessage message, IActorRef replyTo, bool capacity) => replyTo.Tell(
        message is BeginMonitoringStream or EndMonitoringStream or RecordMonitoringTelemetry or RecordMonitoringServices
            ? new MonitoringInputResult(capacity ? MonitoringInputDisposition.CapacityExceeded : MonitoringInputDisposition.StaleEvidence, 0, 0)
            : new Status.Failure(new InvalidOperationException(capacity ? "monitoring_region_capacity_exceeded" : "invalid_client")));
    private sealed class Route(IActorRef actor)
    {
        public IActorRef Actor { get; } = actor;
        public int Outstanding { get; set; }
        public long Generation { get; set; }
        public bool Passivating { get; set; }
        public Queue<Pending> Pending { get; } = new();
    }
    private sealed record Pending(IMonitoringClientMessage Message, IActorRef ReplyTo);
}
