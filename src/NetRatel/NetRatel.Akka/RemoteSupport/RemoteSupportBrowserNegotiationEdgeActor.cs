using System.Threading.Channels;
using Akka.Actor;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Akka.RemoteSupport;

/// <summary>
/// One local browser negotiation connection. Its bounded channel is live
/// transport only; browser reconnect always resumes lifecycle separately and
/// renegotiates instead of replaying SDP or ICE.
/// </summary>
internal sealed class RemoteSupportBrowserNegotiationEdgeActor : ReceiveActor
{
    private readonly Channel<RemoteSupportV2NegotiationEnvelope> _events;

    public RemoteSupportBrowserNegotiationEdgeActor(Channel<RemoteSupportV2NegotiationEnvelope> events)
    {
        _events = events;
        Receive<RemoteSupportBrowserNegotiationEdgeEvent>(message => _events.Writer.TryWrite(message.Envelope));
    }

    protected override void PostStop()
    {
        _events.Writer.TryComplete();
        base.PostStop();
    }

    public static Props Props(Channel<RemoteSupportV2NegotiationEnvelope> events) =>
        global::Akka.Actor.Props.Create(() => new RemoteSupportBrowserNegotiationEdgeActor(events));
}

internal sealed record RemoteSupportBrowserNegotiationEdgeEvent(RemoteSupportV2NegotiationEnvelope Envelope);
