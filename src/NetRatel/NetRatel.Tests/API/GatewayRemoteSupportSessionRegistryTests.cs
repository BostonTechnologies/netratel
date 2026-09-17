using FluentAssertions;
using NetRatel.API.Gateway;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GatewayRemoteSupportSessionRegistryTests
{
    [Fact]
    public async Task OpenAndSignal_RoutesOnlyToTheRegisteredFencedAgent()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new GatewayRemoteSupportSessionRegistry(new CurrentPresenceRouter(client, connectionId, 5), TimeProvider.System);
        using var registration = registry.Register(client, connectionId, 5);

        var opened = await registry.OpenAsync(client, new OpenRemoteSupportRequest(), CancellationToken.None);
        var open = await registration.Reader.ReadAsync();
        open.Signal.SessionId.Should().Be(opened.SessionId);
        open.Signal.SignalType.Should().Be("open");

        await registry.SendBrowserSignalAsync(opened.SessionId, new RemoteSupportSignalRequest(RemoteSupportSignalTypes.Offer, "{}"), CancellationToken.None);
        var offer = await registration.Reader.ReadAsync();
        offer.Signal.SessionId.Should().Be(opened.SessionId);
        offer.Signal.SignalType.Should().Be(RemoteSupportSignalTypes.Offer);

        using var subscription = registry.Subscribe(opened.SessionId);
        registry.TryReceiveAgentSignal(client, new RemoteSupportSignal
        {
            SessionId = opened.SessionId,
            MessageId = Guid.NewGuid().ToString("N"),
            SignalType = RemoteSupportSignalTypes.Ready,
            SessionSequence = 1,
            Payload = Google.Protobuf.ByteString.CopyFromUtf8("{}")
        }).Should().BeTrue();
        var observed = await subscription.Reader.ReadAsync();
        observed.Direction.Should().Be("agent");
        observed.SignalType.Should().Be(RemoteSupportSignalTypes.Ready);
    }

    [Fact]
    public async Task OpenAsync_Rejects_WhenPresenceLeaseHasChanged()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new GatewayRemoteSupportSessionRegistry(new CurrentPresenceRouter(client, Guid.NewGuid(), 9), TimeProvider.System);
        using var registration = registry.Register(client, Guid.NewGuid(), 9);

        var action = () => registry.OpenAsync(client, new OpenRemoteSupportRequest(), CancellationToken.None);

        await action.Should().ThrowAsync<GatewayRemoteSupportSessionUnavailableException>();
    }

    [Fact]
    public async Task Subscribe_Replays_Bounded_Agent_Signals_Without_Reordering()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new GatewayRemoteSupportSessionRegistry(new CurrentPresenceRouter(client, connectionId, 5), TimeProvider.System);
        using var registration = registry.Register(client, connectionId, 5);
        var opened = await registry.OpenAsync(client, new OpenRemoteSupportRequest(), CancellationToken.None);
        await registration.Reader.ReadAsync();

        for (ulong sequence = 1; sequence <= 2; sequence++)
        {
            registry.TryReceiveAgentSignal(client, new RemoteSupportSignal
            {
                SessionId = opened.SessionId,
                MessageId = Guid.NewGuid().ToString("N"),
                SignalType = RemoteSupportSignalTypes.Ice,
                SessionSequence = sequence,
                Payload = Google.Protobuf.ByteString.CopyFromUtf8($"{{\"candidate\":{sequence}}}")
            }).Should().BeTrue();
        }

        using var subscription = registry.Subscribe(opened.SessionId);
        var first = await subscription.Reader.ReadAsync();
        var second = await subscription.Reader.ReadAsync();

        first.Sequence.Should().Be(1);
        second.Sequence.Should().Be(2);
    }

    [Fact]
    public async Task Reconnect_ReplaysTheOpenRequestAndPreservesTheAuthoritySession()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new GatewayRemoteSupportSessionRegistry(new CurrentPresenceRouter(client, connectionId, 5), TimeProvider.System);
        var firstRegistration = registry.Register(client, connectionId, 5);
        var opened = await registry.OpenAsync(client, new OpenRemoteSupportRequest(), CancellationToken.None);
        await firstRegistration.Reader.ReadAsync();
        using var subscription = registry.Subscribe(opened.SessionId);

        registry.TryReceiveAgentSignal(client, new RemoteSupportSignal
        {
            SessionId = opened.SessionId,
            MessageId = Guid.NewGuid().ToString("N"),
            SignalType = RemoteSupportSignalTypes.Ready,
            SessionSequence = 1,
            Payload = Google.Protobuf.ByteString.Empty
        }).Should().BeTrue();
        (await subscription.Reader.ReadAsync()).SignalType.Should().Be(RemoteSupportSignalTypes.Ready);

        firstRegistration.Dispose();
        using var reconnectedRegistration = registry.Register(client, connectionId, 5);

        var replayedOpen = await reconnectedRegistration.Reader.ReadAsync();
        replayedOpen.Signal.SessionId.Should().Be(opened.SessionId);
        replayedOpen.Signal.SignalType.Should().Be("open");
        var reconnect = await subscription.Reader.ReadAsync();
        reconnect.SignalType.Should().Be(RemoteSupportSignalTypes.Reconnect);
        registry.Get(opened.SessionId)!.State.Should().Be("reconnecting");

        registry.TryReceiveAgentSignal(client, new RemoteSupportSignal
        {
            SessionId = opened.SessionId,
            MessageId = Guid.NewGuid().ToString("N"),
            SignalType = RemoteSupportSignalTypes.Ready,
            SessionSequence = 1,
            Payload = Google.Protobuf.ByteString.Empty
        }).Should().BeTrue();

        (await subscription.Reader.ReadAsync()).SignalType.Should().Be(RemoteSupportSignalTypes.Ready);
        registry.Get(opened.SessionId)!.State.Should().Be("accepted");
    }

    [Fact]
    public async Task CloseWithCompletionReason_RecordsACompletedAuthoritySession()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new GatewayRemoteSupportSessionRegistry(new CurrentPresenceRouter(client, connectionId, 5), TimeProvider.System);
        using var registration = registry.Register(client, connectionId, 5);
        var opened = await registry.OpenAsync(client, new OpenRemoteSupportRequest(), CancellationToken.None);
        await registration.Reader.ReadAsync();

        await registry.CloseAsync(opened.SessionId, "operator_finished", CancellationToken.None);

        var close = await registration.Reader.ReadAsync();
        close.Closed.Reason.Should().Be("operator_finished");
        registry.Get(opened.SessionId)!.State.Should().Be("completed");
    }

    [Fact]
    public async Task AgentError_TransitionsTheGatewaySessionToFailedWithoutFallback()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new GatewayRemoteSupportSessionRegistry(new CurrentPresenceRouter(client, connectionId, 5), TimeProvider.System);
        using var registration = registry.Register(client, connectionId, 5);
        var opened = await registry.OpenAsync(client, new OpenRemoteSupportRequest(), CancellationToken.None);
        await registration.Reader.ReadAsync();
        using var subscription = registry.Subscribe(opened.SessionId);

        registry.TryReceiveAgentSignal(client, new RemoteSupportSignal
        {
            SessionId = opened.SessionId,
            MessageId = Guid.NewGuid().ToString("N"),
            SignalType = RemoteSupportSignalTypes.Error,
            SessionSequence = 1,
            Payload = Google.Protobuf.ByteString.CopyFromUtf8("{\"code\":\"rejected\"}")
        }).Should().BeTrue();

        (await subscription.Reader.ReadAsync()).SignalType.Should().Be(RemoteSupportSignalTypes.Error);
        registry.Get(opened.SessionId)!.State.Should().Be("failed");
    }

    [Fact]
    public async Task AgentReject_TransitionsTheGatewaySessionToRejectedWithoutCountingAnAuthorityFailure()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new GatewayRemoteSupportSessionRegistry(new CurrentPresenceRouter(client, connectionId, 5), TimeProvider.System);
        using var registration = registry.Register(client, connectionId, 5);
        var opened = await registry.OpenAsync(client, new OpenRemoteSupportRequest(), CancellationToken.None);
        await registration.Reader.ReadAsync();
        using var subscription = registry.Subscribe(opened.SessionId);

        registry.TryReceiveAgentSignal(client, new RemoteSupportSignal
        {
            SessionId = opened.SessionId,
            MessageId = Guid.NewGuid().ToString("N"),
            SignalType = RemoteSupportSignalTypes.Reject,
            SessionSequence = 1,
            Payload = Google.Protobuf.ByteString.CopyFromUtf8("{\"code\":\"native_webrtc_unsupported_os\"}")
        }).Should().BeTrue();

        (await subscription.Reader.ReadAsync()).SignalType.Should().Be(RemoteSupportSignalTypes.Reject);
        registry.Get(opened.SessionId)!.State.Should().Be("rejected");
    }

    [Fact]
    public async Task BrowserSignals_AreMonotonic_AndAgentSignalsRejectDuplicatesAndReordering()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new GatewayRemoteSupportSessionRegistry(new CurrentPresenceRouter(client, connectionId, 5), TimeProvider.System);
        using var registration = registry.Register(client, connectionId, 5);
        var opened = await registry.OpenAsync(client, new OpenRemoteSupportRequest(), CancellationToken.None);
        (await registration.Reader.ReadAsync()).Signal.SessionSequence.Should().Be(1);

        await registry.SendBrowserSignalAsync(opened.SessionId, new RemoteSupportSignalRequest(RemoteSupportSignalTypes.Offer, "{}"), CancellationToken.None);
        await registry.SendBrowserSignalAsync(opened.SessionId, new RemoteSupportSignalRequest(RemoteSupportSignalTypes.Ice, "{}"), CancellationToken.None);
        (await registration.Reader.ReadAsync()).Signal.SessionSequence.Should().Be(2);
        (await registration.Reader.ReadAsync()).Signal.SessionSequence.Should().Be(3);

        registry.TryReceiveAgentSignal(client, Signal(opened.SessionId, 2)).Should().BeTrue();
        registry.TryReceiveAgentSignal(client, Signal(opened.SessionId, 2)).Should().BeFalse();
        registry.TryReceiveAgentSignal(client, Signal(opened.SessionId, 1)).Should().BeFalse();
    }

    [Fact]
    public async Task FreshRegistryInstance_IllustratesCurrentProcessLocalStateLoss()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var presence = new CurrentPresenceRouter(client, connectionId, 5);
        var first = new GatewayRemoteSupportSessionRegistry(presence, TimeProvider.System);
        using var registration = first.Register(client, connectionId, 5);
        var opened = await first.OpenAsync(client, new OpenRemoteSupportRequest(), CancellationToken.None);
        await registration.Reader.ReadAsync();

        var afterProcessLoss = new GatewayRemoteSupportSessionRegistry(presence, TimeProvider.System);

        afterProcessLoss.Get(opened.SessionId).Should().BeNull();
        Action subscribe = () => afterProcessLoss.Subscribe(opened.SessionId);
        subscribe.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public async Task SignalHistory_IsBounded_AndSlowBrowserIsDisconnectedInsteadOfDroppingSignals()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new GatewayRemoteSupportSessionRegistry(new CurrentPresenceRouter(client, connectionId, 5), TimeProvider.System);
        using var registration = registry.Register(client, connectionId, 5);
        var opened = await registry.OpenAsync(client, new OpenRemoteSupportRequest(), CancellationToken.None);
        await registration.Reader.ReadAsync();

        using var slow = registry.Subscribe(opened.SessionId);
        for (ulong sequence = 1; sequence <= 65; sequence++)
        {
            registry.TryReceiveAgentSignal(client, Signal(opened.SessionId, sequence)).Should().BeTrue();
        }

        var observed = new List<ulong>();
        var consume = async () =>
        {
            await foreach (var signal in slow.Reader.ReadAllAsync())
            {
                observed.Add(signal.Sequence);
            }
        };

        await consume.Should().ThrowAsync<InvalidOperationException>();
        observed.Should().HaveCount(64);
        observed.Should().StartWith(Enumerable.Range(1, 64).Select(value => (ulong)value));

        using var replay = registry.Subscribe(opened.SessionId);
        var replayed = new List<ulong>();
        for (var index = 0; index < 64; index++)
        {
            replayed.Add((await replay.Reader.ReadAsync()).Sequence);
        }

        replayed.Should().Equal(Enumerable.Range(2, 64).Select(value => (ulong)value));
    }

    [Fact]
    public async Task AgentOutboundQueue_BackpressuresInsteadOfDiscardingTheNextSignal()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new GatewayRemoteSupportSessionRegistry(new CurrentPresenceRouter(client, connectionId, 5), TimeProvider.System);
        using var registration = registry.Register(client, connectionId, 5);
        var opened = await registry.OpenAsync(client, new OpenRemoteSupportRequest(), CancellationToken.None);
        await registration.Reader.ReadAsync();

        for (var index = 0; index < 64; index++)
        {
            await registry.SendBrowserSignalAsync(opened.SessionId, new RemoteSupportSignalRequest(RemoteSupportSignalTypes.Ice, "{}"), CancellationToken.None);
        }

        var blocked = registry.SendBrowserSignalAsync(opened.SessionId, new RemoteSupportSignalRequest(RemoteSupportSignalTypes.Ice, "{}"), CancellationToken.None);
        blocked.IsCompleted.Should().BeFalse();
        await registration.Reader.ReadAsync();
        await blocked;
    }

    private static RemoteSupportSignal Signal(string sessionId, ulong sequence) => new()
    {
        SessionId = sessionId,
        MessageId = Guid.NewGuid().ToString("N"),
        SignalType = RemoteSupportSignalTypes.Ice,
        SessionSequence = sequence,
        Payload = Google.Protobuf.ByteString.CopyFromUtf8("{}")
    };

    private sealed class CurrentPresenceRouter(ClientKey expectedClient, Guid connectionId, long epoch) : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) =>
            Task.FromResult(new ClientPresenceSnapshot(client,
                client == expectedClient ? ShadowPresenceStatus.Online : ShadowPresenceStatus.Offline,
                epoch, connectionId, 0, DateTimeOffset.UtcNow, "test", Array.Empty<string>(), null, "akka-dev-canary", true));
    }
}
