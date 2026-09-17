using System.Threading.Channels;
using Akka.Actor;
using NetRatel.Application.RemoteSupport;

namespace NetRatel.Akka.RemoteSupport;

/// <summary>
/// A local, ephemeral browser edge. It owns a bounded channel that is drained
/// by one SSE response on this replica; it deliberately has no durable state.
/// </summary>
internal sealed class RemoteSupportBrowserEdgeActor : ReceiveActor
{
    private readonly Channel<RemoteSupportBrowserLifecycleEvent> _events;

    public RemoteSupportBrowserEdgeActor(Channel<RemoteSupportBrowserLifecycleEvent> events)
    {
        _events = events;
        Receive<RemoteSupportBrowserEdgeEvent>(message => _events.Writer.TryWrite(message.Event));
        Receive<GetRemoteSupportBrowserEdgeReader>(_ => Sender.Tell(_events.Reader));
        Receive<StopRemoteSupportBrowserEdge>(_ => Context.Stop(Self));
    }

    protected override void PostStop()
    {
        _events.Writer.TryComplete();
        base.PostStop();
    }

    public static Props Props(Channel<RemoteSupportBrowserLifecycleEvent> events) =>
        global::Akka.Actor.Props.Create(() => new RemoteSupportBrowserEdgeActor(events));
}

internal sealed record RemoteSupportBrowserEdgeEvent(RemoteSupportBrowserLifecycleEvent Event);
internal sealed record GetRemoteSupportBrowserEdgeReader;
internal sealed record StopRemoteSupportBrowserEdge;
