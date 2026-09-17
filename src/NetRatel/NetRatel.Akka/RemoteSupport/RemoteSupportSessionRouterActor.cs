using Akka.Actor;
using Akka.Cluster.Sharding;
using System.Threading.Channels;
using NetRatel.Application.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Akka.RemoteSupport;

/// <summary>
/// Public application router for the V2 authority. In replica-safe mode it is
/// only a local ingress façade; all session commands are delivered through the
/// Cluster Sharding region and never through a process-local child actor.
/// </summary>
public sealed class RemoteSupportSessionRouterActor : ReceiveActor
{
    private readonly IRemoteSupportLifecycleStore _store;
    private readonly IActorRef? _shardRegion;

    public RemoteSupportSessionRouterActor(IRemoteSupportLifecycleStore store, IActorRef? shardRegion = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _shardRegion = shardRegion;
        ReceiveAsync<OpenRemoteSupportSession>(async message =>
        {
            var replyTo = Sender;
            try
            {
                if (_shardRegion is not null)
                {
                    var proposed = new RemoteSupportSessionKey(
                        message.Command.TenantId,
                        message.Command.AgentId,
                        Guid.NewGuid());
                    var shardedOpened = await _shardRegion.Ask<RemoteSupportSessionSnapshot>(
                            new OpenRemoteSupportSessionByKey(proposed, message.Command),
                            message.Timeout)
                        .ConfigureAwait(false);
                    if (shardedOpened.Session != proposed)
                    {
                        shardedOpened = await _shardRegion.Ask<RemoteSupportSessionSnapshot>(
                                new GetRemoteSupportSessionByKey(shardedOpened.Session, message.Command.InitiatingOperator),
                                message.Timeout)
                            .ConfigureAwait(false);
                    }

                    replyTo.Tell(shardedOpened);
                    return;
                }

                var opened = await _store.OpenAsync(
                        message.Command,
                        Guid.NewGuid(),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                var actor = GetOrCreate(opened.Snapshot.Session);
                var snapshot = await actor.Ask<RemoteSupportSessionSnapshot?>(
                        _shardRegion is null
                            ? new GetRemoteSupportSession(message.Command.InitiatingOperator)
                            : new GetRemoteSupportSessionByKey(opened.Snapshot.Session, message.Command.InitiatingOperator),
                        message.Timeout)
                    .ConfigureAwait(false);
                replyTo.Tell(snapshot ?? opened.Snapshot);
            }
            catch (Exception exception)
            {
                replyTo.Tell(new Status.Failure(exception));
            }
        });
        Receive<GetRemoteSupportSessionByKey>(message => Route(message.Session, message, new GetRemoteSupportSession(message.Operator)));
        Receive<ResumeRemoteSupportSessionByKey>(message => Route(message.Session, message, new ResumeRemoteSupportSession(message.Request)));
        Receive<ControlRemoteSupportSessionByKey>(message => Route(message.Session, message, new ControlRemoteSupportSession(message.Command)));
        Receive<AdvanceRemoteSupportSessionByKey>(message => Route(message.Session, message, new AdvanceRemoteSupportSession(message.Command)));
        Receive<PrepareRemoteSupportMediaByKey>(message => Route(message.Session, message, message.Command));
        Receive<ReceiveRemoteSupportNegotiationByKey>(message => Route(message.Session, message, message.Ingress));
        Receive<StartRemoteSupportTargetTransition>(message => Route(message.Session, message, message));
        Receive<ObserveRemoteSupportTransitionInventory>(message => Route(message.Session, message, message));
        Receive<SelectRemoteSupportTransitionTarget>(message => Route(message.Session, message, message));
        Receive<GetRemoteSupportTransitionSelectionByKey>(message => Route(message.Session, message, new GetRemoteSupportTransitionSelection(message.Operator)));
        Receive<CompleteRemoteSupportReplacementPreparation>(message => Route(message.Session, message, message));
        Receive<CompleteRemoteSupportReplacementNegotiation>(message => Route(message.Session, message, message));
        Receive<ReceiveRemoteSupportTransitionEvidence>(message => Route(message.Session, message, message));
        Receive<RegisterRemoteSupportAgentEdge>(message => Route(message.Session, message, message));
        Receive<UnregisterRemoteSupportAgentEdge>(message => Route(message.Session, message, message));
        Receive<RouteRemoteSupportAgentEnvelope>(message => Route(message.Session, message, message));
        ReceiveAsync<SubscribeRemoteSupportBrowserEdge>(async message =>
        {
            var replyTo = Sender;
            var routeId = Guid.NewGuid();
            var events = Channel.CreateBounded<RemoteSupportBrowserLifecycleEvent>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
            var edge = Context.ActorOf(RemoteSupportBrowserEdgeActor.Props(events), $"remote-support-browser-edge-{routeId:N}");
            var registration = await GetOrCreate(message.Session).Ask<RemoteSupportBrowserEdgeRegistration>(
                    new RegisterRemoteSupportBrowserEdge(
                        message.Session,
                        message.Operator,
                        routeId,
                        message.AfterAuditSequence,
                        edge),
                    message.Timeout)
                .ConfigureAwait(false);
            if (!registration.Accepted)
            {
                Context.Stop(edge);
                replyTo.Tell(null);
                return;
            }

            replyTo.Tell(new RemoteSupportBrowserEdgeLocalRegistration(routeId, message.Session, edge, events.Reader));
        });
        Receive<UnsubscribeRemoteSupportBrowserEdge>(message =>
        {
            GetOrCreate(message.Session).Tell(new UnregisterRemoteSupportBrowserEdge(message.Session, message.EdgeRouteId));
            Context.Stop(message.Edge);
        });
        ReceiveAsync<SubscribeRemoteSupportBrowserNegotiationEdge>(async message =>
        {
            var replyTo = Sender;
            var routeId = Guid.NewGuid();
            var events = Channel.CreateBounded<RemoteSupportV2NegotiationEnvelope>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
            var edge = Context.ActorOf(RemoteSupportBrowserNegotiationEdgeActor.Props(events), $"remote-support-browser-negotiation-edge-{routeId:N}");
            var registration = await GetOrCreate(message.Session).Ask<RemoteSupportBrowserEdgeRegistration>(
                    new RegisterRemoteSupportBrowserNegotiationEdge(message.Session, message.Operator, routeId, edge),
                    message.Timeout)
                .ConfigureAwait(false);
            if (!registration.Accepted)
            {
                Context.Stop(edge);
                replyTo.Tell(null);
                return;
            }

            replyTo.Tell(new RemoteSupportBrowserNegotiationEdgeLocalRegistration(routeId, message.Session, edge, events.Reader));
        });
        Receive<UnsubscribeRemoteSupportBrowserNegotiationEdge>(message =>
        {
            GetOrCreate(message.Session).Tell(new UnregisterRemoteSupportBrowserNegotiationEdge(message.Session, message.EdgeRouteId));
            Context.Stop(message.Edge);
        });
    }

    public static Props Props(IRemoteSupportLifecycleStore store) =>
        global::Akka.Actor.Props.Create(() => new RemoteSupportSessionRouterActor(store));

    public static Props Props(IRemoteSupportLifecycleStore store, IActorRef shardRegion) =>
        global::Akka.Actor.Props.Create(() => new RemoteSupportSessionRouterActor(store, shardRegion));

    private IActorRef GetOrCreate(RemoteSupportSessionKey session)
    {
        if (_shardRegion is not null)
        {
            return _shardRegion;
        }

        var actorName = CreateActorName(session);
        var existing = Context.Child(actorName);
        return existing.IsNobody()
            ? Context.ActorOf(RemoteSupportSessionActor.Props(session, _store), actorName)
            : existing;
    }

    private void Route(RemoteSupportSessionKey session, object clusteredMessage, object localMessage)
    {
        if (_shardRegion is not null)
        {
            _shardRegion.Forward(clusteredMessage);
            return;
        }

        GetOrCreate(session).Forward(localMessage);
    }

    private static string CreateActorName(RemoteSupportSessionKey session) =>
        $"remote-support-{session.TenantId}-{session.AgentId:N}-{session.RemoteSupportSessionId:N}";
}

/// <summary>Cluster-sharding routing rules; the entity key is a stable V2 session identity.</summary>
public sealed class RemoteSupportSessionMessageExtractor : IMessageExtractor
{
    public const string RegionName = "remote-support-v2-session";
    private const int ShardCount = 128;

    public string EntityId(object message) => message is IRemoteSupportSessionEnvelope envelope
        ? $"{envelope.Session.TenantId}:{envelope.Session.AgentId:N}:{envelope.Session.RemoteSupportSessionId:N}"
        : string.Empty;

    public string ShardId(string entityId, object? message) =>
        Math.Abs(entityId.GetHashCode(StringComparison.Ordinal) % ShardCount)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string ShardId(object message) => ShardId(EntityId(message), message);

    public object EntityMessage(object message) => message;
}

public interface IRemoteSupportSessionEnvelope
{
    RemoteSupportSessionKey Session { get; }
}

public sealed record OpenRemoteSupportSession(RemoteSupportOpenSessionCommand Command, TimeSpan Timeout);
public sealed record OpenRemoteSupportSessionByKey(RemoteSupportSessionKey Session, RemoteSupportOpenSessionCommand Command) : IRemoteSupportSessionEnvelope;
public sealed record GetRemoteSupportSessionByKey(RemoteSupportSessionKey Session, RemoteSupportOperatorBinding Operator) : IRemoteSupportSessionEnvelope;
public sealed record ResumeRemoteSupportSessionByKey(RemoteSupportResumeRequest Request) : IRemoteSupportSessionEnvelope
{
    public RemoteSupportSessionKey Session => Request.Session;
}
public sealed record ControlRemoteSupportSessionByKey(RemoteSupportControlCommand Command) : IRemoteSupportSessionEnvelope
{
    public RemoteSupportSessionKey Session => Command.Session;
}
public sealed record AdvanceRemoteSupportSessionByKey(AdvanceRemoteSupportSessionLifecycle Command) : IRemoteSupportSessionEnvelope
{
    public RemoteSupportSessionKey Session => Command.Session;
}
public sealed record PrepareRemoteSupportMediaByKey(PrepareRemoteSupportMedia Command) : IRemoteSupportSessionEnvelope
{
    public RemoteSupportSessionKey Session => Command.Session;
}
public sealed record ReceiveRemoteSupportNegotiationByKey(RemoteSupportNegotiationIngress Ingress) : IRemoteSupportSessionEnvelope
{
    public RemoteSupportSessionKey Session => Ingress.Envelope.Session;
}
public sealed record SubscribeRemoteSupportBrowserEdge(
    RemoteSupportSessionKey Session,
    RemoteSupportOperatorBinding Operator,
    long AfterAuditSequence,
    TimeSpan Timeout);
public sealed record UnsubscribeRemoteSupportBrowserEdge(
    RemoteSupportSessionKey Session,
    Guid EdgeRouteId,
    IActorRef Edge);
public sealed record RemoteSupportBrowserEdgeLocalRegistration(
    Guid EdgeRouteId,
    RemoteSupportSessionKey Session,
    IActorRef Edge,
    ChannelReader<RemoteSupportBrowserLifecycleEvent> Reader);
public sealed record SubscribeRemoteSupportBrowserNegotiationEdge(
    RemoteSupportSessionKey Session,
    RemoteSupportOperatorBinding Operator,
    TimeSpan Timeout);
public sealed record UnsubscribeRemoteSupportBrowserNegotiationEdge(
    RemoteSupportSessionKey Session,
    Guid EdgeRouteId,
    IActorRef Edge);
public sealed record RemoteSupportBrowserNegotiationEdgeLocalRegistration(
    Guid EdgeRouteId,
    RemoteSupportSessionKey Session,
    IActorRef Edge,
    ChannelReader<RemoteSupportV2NegotiationEnvelope> Reader);
