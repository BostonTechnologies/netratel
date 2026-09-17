using System.Threading.Channels;
using FluentAssertions;
using NetRatel.API.Gateway;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RemoteSupportV2PreparationRegistryTests
{
    [Fact]
    public async Task PrepareAsync_accepts_only_the_pending_request_nonce_and_exact_target()
    {
        var registry = new RemoteSupportV2PreparationRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());
        using var registration = registry.Register(client, Guid.NewGuid(), 1, "1.0");
        registry.TryReceiveInventory(client, Inventory(1)).Should().BeTrue();
        var target = new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.InteractiveUser, 4, "sid-four", 1);

        var pending = registry.PrepareAsync(client, new RemoteSupportOperatorBinding("operator"), target, CancellationToken.None);
        var frame = await registration.Reader.ReadAsync();
        var command = frame.PrepareTarget;

        registry.TryCompletePreparation(client, Prepared(command, target, Guid.NewGuid())).Should().BeFalse();
        registry.TryCompletePreparation(client, Prepared(command, target, Guid.Parse(command.RouteNonce))).Should().BeTrue();

        var result = await pending;
        result.ProviderReady.Should().BeTrue();
        result.HelperRoute!.WindowsSessionId.Should().Be(4);
        result.Target.Should().Be(target);
    }

    [Fact]
    public async Task PrepareMediaAsync_binds_the_pending_preparation_to_its_logical_session()
    {
        var registry = new RemoteSupportV2PreparationRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());
        var logicalSession = new RemoteSupportSessionKey(client.TenantId, client.AgentId, Guid.NewGuid());
        using var registration = registry.Register(client, Guid.NewGuid(), 1, "1.0");
        registry.TryReceiveInventory(client, Inventory(1)).Should().BeTrue();
        var target = new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.InteractiveUser, 4, "sid-four", 1);

        var pending = registry.PrepareMediaAsync(logicalSession, new RemoteSupportOperatorBinding("operator"), target, CancellationToken.None);
        var command = (await registration.Reader.ReadAsync()).PrepareTarget;
        command.HasSession.Should().BeTrue();
        command.Session.RemoteSupportSessionId.Should().Be(logicalSession.RemoteSupportSessionId.ToString("D"));

        var prepared = Prepared(command, target, Guid.Parse(command.RouteNonce));
        prepared.HasSession = true;
        prepared.Session = command.Session;
        registry.TryCompletePreparation(client, prepared).Should().BeTrue();

        (await pending).Session.Should().Be(logicalSession);
    }

    [Fact]
    public void Inventory_rejects_duplicate_or_out_of_order_sequences()
    {
        var registry = new RemoteSupportV2PreparationRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());

        registry.TryReceiveInventory(client, Inventory(2)).Should().BeTrue();
        registry.TryReceiveInventory(client, Inventory(2)).Should().BeFalse();
        registry.TryReceiveInventory(client, Inventory(1)).Should().BeFalse();
        registry.GetInventory(client)!.Snapshot.InventorySequence.Should().Be(2);
    }

    [Fact]
    public void Register_resets_the_inventory_sequence_for_a_reconnected_preparation_stream()
    {
        var registry = new RemoteSupportV2PreparationRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());

        using (registry.Register(client, Guid.NewGuid(), 1, "1.0"))
        {
            registry.TryReceiveInventory(client, Inventory(31)).Should().BeTrue();
        }

        using (registry.Register(client, Guid.NewGuid(), 2, "1.0"))
        {
            registry.GetInventory(client).Should().BeNull();
            registry.TryReceiveInventory(client, Inventory(1)).Should().BeTrue();
        }

        registry.GetInventory(client)!.Snapshot.InventorySequence.Should().Be(1);
    }

    [Fact]
    public async Task RequestInventoryRefreshAsync_is_delivered_only_to_the_registered_agent()
    {
        var registry = new RemoteSupportV2PreparationRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());
        using var registration = registry.Register(client, Guid.NewGuid(), 1, "1.0");

        await registry.RequestInventoryRefreshAsync(client, CancellationToken.None);

        var frame = await registration.Reader.ReadAsync();
        frame.PayloadCase.Should().Be(GatewayRemoteSupportPreparationFrame.PayloadOneofCase.InventoryRefresh);
        frame.InventoryRefresh.RequestId.Should().NotBeNullOrWhiteSpace();
        frame.TenantId.Should().Be(client.TenantId);
        frame.ClientId.Should().Be(client.AgentId.ToString("D"));
    }

    [Fact]
    public async Task PrepareAsync_requires_the_current_inventory_sequence()
    {
        var registry = new RemoteSupportV2PreparationRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());
        using var registration = registry.Register(client, Guid.NewGuid(), 1, "1.0");
        registry.TryReceiveInventory(client, Inventory(2)).Should().BeTrue();

        var act = () => registry.PrepareAsync(
            client,
            new RemoteSupportOperatorBinding("operator"),
            new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.InteractiveUser, 4, "sid-four", 1),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Register_projects_only_bounded_current_capabilities_and_removes_them_on_disconnect()
    {
        var registry = new RemoteSupportV2PreparationRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());
        var connectionId = Guid.NewGuid();

        using (registry.Register(client, connectionId, 4, "1.0",
                   [" remote_support_v2 ", "REMOTE_SUPPORT_V2", "interactive_assist", new string('x', 97)]))
        {
            var capabilities = registry.GetCapabilities(client)!;
            capabilities.ConnectionId.Should().Be(connectionId);
            capabilities.ConnectionEpoch.Should().Be(4);
            capabilities.Capabilities.Should().Equal("interactive_assist", "remote_support_v2");
            capabilities.IsFresh(DateTimeOffset.UtcNow).Should().BeTrue();
        }

        registry.GetCapabilities(client).Should().BeNull();
    }

    [Fact]
    public void Accepted_inventory_renews_the_capability_lease_for_the_active_preparation_stream()
    {
        var time = new AdjustableTimeProvider(new DateTimeOffset(2026, 8, 21, 18, 0, 0, TimeSpan.Zero));
        var registry = new RemoteSupportV2PreparationRegistry(time);
        var client = new ClientKey(7, Guid.NewGuid());

        using var registration = registry.Register(client, Guid.NewGuid(), 1, "1.0", ["remote_support_v2"]);
        var initial = registry.GetCapabilities(client)!;
        time.Advance(TimeSpan.FromMinutes(1));

        registry.TryReceiveInventory(client, Inventory(1)).Should().BeTrue();

        var renewed = registry.GetCapabilities(client)!;
        renewed.ObservedAtUtc.Should().Be(time.GetUtcNow());
        renewed.ExpiresAtUtc.Should().Be(time.GetUtcNow().AddSeconds(90));
        renewed.ConnectionId.Should().Be(initial.ConnectionId);
        renewed.ConnectionEpoch.Should().Be(initial.ConnectionEpoch);
        renewed.Capabilities.Should().Equal(initial.Capabilities);
    }

    private static RemoteSupportV2InventorySnapshot Inventory(ulong sequence) =>
        new()
        {
            ContractVersion = RemoteSupportV2ContractVersions.Current,
            InventorySequence = sequence,
            ObservedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ExpiresUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            Entries =
            {
                new RemoteSupportV2WindowsSession
                {
                    WindowsSessionId = 4,
                    State = "active",
                    UserSidHash = "sid-four",
                    IsConnected = true,
                    HelperConnected = true,
                    HelperVersionMatches = true,
                    HelperVersion = "1.2.3"
                }
            }
        };

    private static RemoteSupportV2PreparedTarget Prepared(
        RemoteSupportV2PrepareTarget command,
        RemoteSupportTargetDescriptor target,
        Guid routeNonce) =>
        new()
        {
            ContractVersion = RemoteSupportV2ContractVersions.Current,
            RequestId = command.RequestId,
            RouteNonce = routeNonce.ToString("D"),
            Target = new RemoteSupportV2Target
            {
                Kind = target.Kind,
                WindowsSessionId = target.WindowsSessionId!.Value,
                UserSidHash = target.UserSidHash,
                InventorySequence = target.InventorySequence!.Value,
                HasWindowsSessionId = true,
                HasInventorySequence = true
            },
            InventorySequence = 2,
            TargetValid = true,
            ProviderReady = true,
            Code = "target_helper_ready",
            Message = "ready",
            HasHelperRoute = true,
            HelperRoute = new RemoteSupportV2HelperRoute
            {
                HelperRouteId = Guid.NewGuid().ToString("D"),
                WindowsSessionId = target.WindowsSessionId.Value,
                UserSidHash = target.UserSidHash,
                HelperVersion = "1.2.3"
            },
            ObservedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

    [Fact]
    public async Task ConcurrentRefreshes_PreserveSequenceWhileFirstEnqueueIsSuspended()
    {
        var channel = new PausedFirstWriteChannel();
        var session = new PreparationTransportSession(new ClientKey(7, Guid.NewGuid()), Guid.NewGuid(), 4, "1.0", channel);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = session.RequestInventoryRefreshAsync(deadline.Token);
        await channel.FirstWriteEntered.Task.WaitAsync(deadline.Token);
        var second = session.RequestInventoryRefreshAsync(deadline.Token);
        try
        {
            channel.WriteCount.Should().Be(1);
            second.IsCompleted.Should().BeFalse();
        }
        finally
        {
            channel.ReleaseFirstWrite.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(deadline.Token);
            session.Complete("test_finished");
        }
        (await session.Reader.ReadAsync(deadline.Token)).Sequence.Should().Be(1);
        (await session.Reader.ReadAsync(deadline.Token)).Sequence.Should().Be(2);
    }

    [Fact]
    public async Task CancelledRefreshWaitingForGate_DoesNotReserveSequenceOrBlockLaterRefresh()
    {
        var session = CreateSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await FillChannelAsync(session, deadline.Token);
        var blocked = session.RequestInventoryRefreshAsync(deadline.Token);
        using var cancelled = new CancellationTokenSource();
        var waiting = session.RequestInventoryRefreshAsync(cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(deadline.Token));
        blocked.IsCompleted.Should().BeFalse();
        await session.Reader.ReadAsync(deadline.Token);
        await blocked.WaitAsync(deadline.Token);
        await session.Reader.ReadAsync(deadline.Token);
        await session.RequestInventoryRefreshAsync(deadline.Token);
        session.Complete("test_finished");
        var frames = await DrainAsync(session, deadline.Token);
        frames.Should().HaveCount(32);
        frames.Select(frame => frame.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        frames[^1].Sequence.Should().Be(34);
    }

    [Fact]
    public async Task CancelledRefreshBlockedOnCapacity_ReleasesGateWithoutPublishingItsFrame()
    {
        var session = CreateSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await FillChannelAsync(session, deadline.Token);
        using var cancelled = new CancellationTokenSource();
        var blocked = session.RequestInventoryRefreshAsync(cancelled.Token);
        var following = session.RequestInventoryRefreshAsync(deadline.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(deadline.Token));
        await session.Reader.ReadAsync(deadline.Token);
        await following.WaitAsync(deadline.Token);
        session.Complete("test_finished");
        var frames = await DrainAsync(session, deadline.Token);
        frames.Should().HaveCount(32);
        frames.Select(frame => frame.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        frames.Should().NotContain(frame => frame.Sequence == 33);
        frames[^1].Sequence.Should().Be(34);
    }

    [Fact]
    public async Task Replacement_ClosesBlockedAndGateWaitingRefreshesWithoutLeakingFrames()
    {
        var registry = new RemoteSupportV2PreparationRegistry(TimeProvider.System);
        var client = new ClientKey(7, Guid.NewGuid());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var previous = registry.Register(client, Guid.NewGuid(), 4, "1.0");
        for (var i = 0; i < 32; i++)
            await registry.RequestInventoryRefreshAsync(client, deadline.Token);
        var blocked = registry.RequestInventoryRefreshAsync(client, deadline.Token);
        var waiting = registry.RequestInventoryRefreshAsync(client, deadline.Token);
        using var current = registry.Register(client, Guid.NewGuid(), 5, "1.0");
        await Assert.ThrowsAsync<ChannelClosedException>(() => blocked.WaitAsync(deadline.Token));
        await Assert.ThrowsAsync<ChannelClosedException>(() => waiting.WaitAsync(deadline.Token));
        current.Reader.TryRead(out _).Should().BeFalse();
        await registry.RequestInventoryRefreshAsync(client, deadline.Token);
        (await current.Reader.ReadAsync(deadline.Token)).Sequence.Should().Be(1);
    }

    private static PreparationTransportSession CreateSession() =>
        new(new ClientKey(7, Guid.NewGuid()), Guid.NewGuid(), 4, "1.0");

    private static async Task FillChannelAsync(PreparationTransportSession session, CancellationToken token)
    {
        for (var i = 0; i < 32; i++)
            await session.RequestInventoryRefreshAsync(token);
    }

    private static async Task<List<GatewayRemoteSupportPreparationFrame>> DrainAsync(PreparationTransportSession session, CancellationToken token)
    {
        var frames = new List<GatewayRemoteSupportPreparationFrame>();
        await foreach (var frame in session.Reader.ReadAllAsync(token))
            frames.Add(frame);
        return frames;
    }

    private sealed class PausedFirstWriteChannel : Channel<GatewayRemoteSupportPreparationFrame>
    {
        public TaskCompletionSource FirstWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;
        public int WriteCount => Volatile.Read(ref _writeCount);

        public PausedFirstWriteChannel()
        {
            var inner = Channel.CreateBounded<GatewayRemoteSupportPreparationFrame>(32);
            Reader = inner.Reader;
            Writer = new PausedWriter(this, inner.Writer);
        }

        private sealed class PausedWriter(PausedFirstWriteChannel owner, ChannelWriter<GatewayRemoteSupportPreparationFrame> inner)
            : ChannelWriter<GatewayRemoteSupportPreparationFrame>
        {
            public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);
            public override bool TryWrite(GatewayRemoteSupportPreparationFrame item) => inner.TryWrite(item);
            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
                inner.WaitToWriteAsync(cancellationToken);

            public override async ValueTask WriteAsync(GatewayRemoteSupportPreparationFrame item, CancellationToken cancellationToken = default)
            {
                if (Interlocked.Increment(ref owner._writeCount) == 1)
                {
                    owner.FirstWriteEntered.TrySetResult();
                    await owner.ReleaseFirstWrite.Task.WaitAsync(cancellationToken);
                }
                await inner.WriteAsync(item, cancellationToken);
            }
        }
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
