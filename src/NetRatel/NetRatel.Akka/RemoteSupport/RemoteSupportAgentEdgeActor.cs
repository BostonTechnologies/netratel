using System.Threading.Channels;
using Akka.Actor;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Akka.RemoteSupport;

/// <summary>
/// Local agent-connection edge. It is intentionally a bounded, ephemeral
/// transport adapter: the shard owns route fencing, while the gRPC service
/// drains the channel to its live stream.
/// </summary>
public sealed class RemoteSupportAgentEdgeActor : ReceiveActor
{
    private readonly Channel<RemoteSupportAgentRouteEnvelope> _outbound;

    public RemoteSupportAgentEdgeActor(Channel<RemoteSupportAgentRouteEnvelope> outbound)
    {
        _outbound = outbound;
        Receive<RemoteSupportAgentRouteEnvelope>(message => _outbound.Writer.TryWrite(message));
        Receive<StopRemoteSupportAgentEdge>(_ => Context.Stop(Self));
    }

    protected override void PostStop()
    {
        _outbound.Writer.TryComplete();
        base.PostStop();
    }

    public static Props Props(Channel<RemoteSupportAgentRouteEnvelope> outbound) =>
        global::Akka.Actor.Props.Create(() => new RemoteSupportAgentEdgeActor(outbound));
}

/// <summary>
/// Trusted, transient signalling/control envelope. It is never passed to the
/// lifecycle store, audit log, browser SSE stream, or public DTOs.
/// </summary>
public sealed record RemoteSupportAgentRouteEnvelope(
    RemoteSupportSessionKey Session,
    Guid EdgeRouteId,
    long RouteGeneration,
    string Kind,
    byte[] Payload,
    RemoteSupportV2NegotiationEnvelope? Negotiation = null);

public sealed record StopRemoteSupportAgentEdge;
