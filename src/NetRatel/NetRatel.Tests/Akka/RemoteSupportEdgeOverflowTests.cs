using System.Threading.Channels;
using Akka.Actor;
using FluentAssertions;
using NetRatel.Akka.RemoteSupport;
using NetRatel.Application.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class RemoteSupportEdgeOverflowTests
{
    [Fact]
    public async Task AgentEdge_ClosesItsStreamInsteadOfDroppingReliableEnvelopes()
    {
        var session = NewSession();
        var first = new RemoteSupportAgentRouteEnvelope(session, Guid.NewGuid(), 1, "offer", [1]);
        var second = first with { Payload = [2] };
        var channel = CreateBoundedChannel<RemoteSupportAgentRouteEnvelope>();
        await AssertOverflowAsync(
            RemoteSupportAgentEdgeActor.Props(channel),
            channel.Reader,
            first,
            second,
            first);
    }

    [Fact]
    public async Task BrowserLifecycleEdge_ClosesItsStreamInsteadOfDroppingAuditEvents()
    {
        var session = NewSession();
        var first = CreateLifecycleEvent(session, 1);
        var second = CreateLifecycleEvent(session, 2);
        var channel = CreateBoundedChannel<RemoteSupportBrowserLifecycleEvent>();
        await AssertOverflowAsync(
            RemoteSupportBrowserEdgeActor.Props(channel),
            channel.Reader,
            new RemoteSupportBrowserEdgeEvent(first),
            new RemoteSupportBrowserEdgeEvent(second),
            first);
    }

    [Fact]
    public async Task BrowserNegotiationEdge_ClosesItsStreamInsteadOfDroppingNegotiationEnvelopes()
    {
        var session = NewSession();
        var first = CreateNegotiationEnvelope(session, 1);
        var second = CreateNegotiationEnvelope(session, 2);
        var channel = CreateBoundedChannel<RemoteSupportV2NegotiationEnvelope>();
        await AssertOverflowAsync(
            RemoteSupportBrowserNegotiationEdgeActor.Props(channel),
            channel.Reader,
            new RemoteSupportBrowserNegotiationEdgeEvent(first),
            new RemoteSupportBrowserNegotiationEdgeEvent(second),
            first);
    }

    private static async Task AssertOverflowAsync<T>(
        Props props,
        ChannelReader<T> reader,
        object firstMessage,
        object overflowMessage,
        T expectedFirst)
    {
        var system = ActorSystem.Create($"remote-support-edge-overflow-{Guid.NewGuid():N}");
        try
        {
            var edge = system.ActorOf(props);
            var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watcher = system.ActorOf(Props.Create(() => new ActorTerminationWatcher(terminated)));
            watcher.Tell(edge);
            edge.Tell(firstMessage);
            edge.Tell(overflowMessage);

            await terminated.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var retained = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            retained.Should().Be(expectedFirst);

            var closed = await Assert.ThrowsAsync<ChannelClosedException>(async () =>
                await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            RemoteSupportEdgeBufferOverflowException.Is(closed).Should().BeTrue(
                "a saturated reliable edge must surface overflow after preserving the already queued item");
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static Channel<T> CreateBoundedChannel<T>() => Channel.CreateBounded<T>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false
    });

    private static RemoteSupportSessionKey NewSession() => new(17, Guid.NewGuid(), Guid.NewGuid());

    private static RemoteSupportBrowserLifecycleEvent CreateLifecycleEvent(RemoteSupportSessionKey session, long sequence)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RemoteSupportSessionSnapshot(
            RemoteSupportV2ContractVersions.Current,
            session,
            Guid.NewGuid(),
            new RemoteSupportOperatorBinding("operator"),
            new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.InteractiveUser, 1, "sid-hash", 1),
            Array.Empty<string>(),
            RemoteSupportV2SessionStates.Requested,
            sequence,
            now,
            now);
        return new RemoteSupportBrowserLifecycleEvent(sequence, snapshot, RemoteSupportV2AuditEventTypes.LifecycleChanged);
    }

    private static RemoteSupportV2NegotiationEnvelope CreateNegotiationEnvelope(RemoteSupportSessionKey session, long sequence) =>
        new(
            session,
            1,
            RemoteSupportV2NegotiationDirections.Browser,
            sequence,
            Guid.NewGuid(),
            RemoteSupportV2NegotiationSignalTypes.Ice,
            [1, 2, 3]);

    private sealed class ActorTerminationWatcher : ReceiveActor
    {
        public ActorTerminationWatcher(TaskCompletionSource terminated)
        {
            Receive<IActorRef>(actor => Context.Watch(actor));
            Receive<Terminated>(_ => terminated.TrySetResult());
        }
    }
}
