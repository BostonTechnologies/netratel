using System.Threading.Channels;
using FluentAssertions;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentCommandGatewaySessionRegistryTests
{
    [Fact]
    public async Task DispatchAndCancel_AreFencedToTheRegisteredAgentSession()
    {
        var registry = new AgentCommandGatewaySessionRegistry();
        var client = new ClientKey(71, Guid.NewGuid());
        using var registration = registry.Register(client, Guid.NewGuid(), connectionEpoch: 4);
        var requestedAt = DateTimeOffset.UtcNow;

        await registry.DispatchAsync(client, new CommandGatewayDispatch(
            "cmd-1", "corr-1", requestedAt, 3, 3, "os-info", "{}", 1, client.TenantId), CancellationToken.None);
        await registry.CancelAsync(client, "cmd-1", "operator_cancelled", CancellationToken.None);

        var dispatch = await registration.Reader.ReadAsync();
        dispatch.Dispatch.CommandId.Should().Be("cmd-1");
        dispatch.Dispatch.NextVersion.Should().Be(3);
        dispatch.Dispatch.NextSequence.Should().Be(3);
        dispatch.TenantId.Should().Be(client.TenantId);
        dispatch.ClientId.Should().Be(client.AgentId.ToString("D"));

        var cancel = await registration.Reader.ReadAsync();
        cancel.Cancel.CommandId.Should().Be("cmd-1");
        cancel.Cancel.Reason.Should().Be("operator_cancelled");
        cancel.Sequence.Should().BeGreaterThan(dispatch.Sequence);
    }

    [Fact]
    public void DispatchWithoutAnAdmittedSession_IsExplicitlyUnavailable()
    {
        var registry = new AgentCommandGatewaySessionRegistry();
        var client = new ClientKey(72, Guid.NewGuid());

        registry.IsAvailable(client).Should().BeFalse();
        var action = () => registry.DispatchAsync(client, new CommandGatewayDispatch(
            "cmd-2", "corr-2", DateTimeOffset.UtcNow, 3, 3, "os-info", "{}", 1, client.TenantId), CancellationToken.None);

        action.Should().ThrowAsync<AgentCommandGatewaySessionUnavailableException>();
    }

    [Fact]
    public async Task Register_RejectsStaleFences_AndAnOldDisposeCannotRemoveTheCurrentSession()
    {
        var registry = new AgentCommandGatewaySessionRegistry();
        var client = new ClientKey(71, Guid.NewGuid());
        var first = registry.Register(client, Guid.NewGuid(), connectionEpoch: 4);
        using var current = registry.Register(client, Guid.NewGuid(), connectionEpoch: 5);

        var staleEpoch = () => registry.Register(client, Guid.NewGuid(), connectionEpoch: 4);
        staleEpoch.Should().Throw<AgentGatewayRegistrationFencedException>();
        var ambiguousFence = () => registry.Register(client, Guid.NewGuid(), connectionEpoch: 5);
        ambiguousFence.Should().Throw<AgentGatewayRegistrationFencedException>();

        first.Dispose();
        await registry.CancelAsync(client, "cmd-current", "test", CancellationToken.None);
        (await current.Reader.ReadAsync()).Cancel.CommandId.Should().Be("cmd-current");
    }
    [Fact]
    public async Task ExactReconnect_CompletesOldOwnership_AndLateDisposePreservesReplacement()
    {
        var registry = new AgentCommandGatewaySessionRegistry();
        var client = new ClientKey(71, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var old = registry.Register(client, connection, 5);
        using var current = registry.Register(client, connection, 5);

        old.IsCurrent.Should().BeFalse();
        old.CompletionToken.IsCancellationRequested.Should().BeTrue();
        current.RegistrationId.Should().NotBe(old.RegistrationId);
        old.Activate().Should().BeFalse();
        old.Dispose();
        current.IsCurrent.Should().BeTrue();
        await registry.CancelAsync(client, "cmd-current", "test", CancellationToken.None);
        (await current.Reader.ReadAsync()).Cancel.Should().NotBeNull();
    }

    [Fact]
    public void ProvisionalRegistration_IsInvisibleUntilActivated_AndCannotActivateAfterReplacement()
    {
        var registry = new AgentCommandGatewaySessionRegistry();
        var client = new ClientKey(71, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var candidate = registry.Register(client, connection, 5, provisional: true);
        candidate.IsCurrent.Should().BeTrue();
        registry.IsAvailable(client).Should().BeFalse();
        candidate.Activate().Should().BeTrue();
        registry.IsAvailable(client).Should().BeTrue();

        using var replacement = registry.Register(client, connection, 5, provisional: true);
        candidate.Activate().Should().BeFalse();
        registry.IsAvailable(client).Should().BeFalse();
        replacement.Activate().Should().BeTrue();
        registry.IsAvailable(client).Should().BeTrue();
    }
    [Fact]
    public async Task ConcurrentDispatchAndCancel_PreserveSequenceWhileFirstWriteIsSuspended()
    {
        var channel = new PausedFirstWriteChannel();
        var client = new ClientKey(71, Guid.NewGuid());
        var session = new AgentCommandGatewaySession(client, Guid.NewGuid(), 4, channel);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var dispatch = session.DispatchAsync(new CommandGatewayDispatch(
            "cmd-1", "corr-1", DateTimeOffset.UtcNow, 3, 3, "os-info", "{}", 1, client.TenantId), deadline.Token);
        await channel.FirstWriteEntered.Task.WaitAsync(deadline.Token);
        var cancel = session.CancelAsync("cmd-1", "operator_cancelled", deadline.Token);
        try
        {
            // Both calls advance synchronously until their first incomplete await.
            // A second writer entering now can overtake the reserved first sequence.
            channel.WriteCount.Should().Be(1);
            cancel.IsCompleted.Should().BeFalse();
        }
        finally
        {
            channel.ReleaseFirstWrite.TrySetResult();
            await Task.WhenAll(dispatch, cancel).WaitAsync(deadline.Token);
            session.Complete();
        }

        var first = await session.Reader.ReadAsync(deadline.Token);
        var second = await session.Reader.ReadAsync(deadline.Token);
        first.Dispatch.CommandId.Should().Be("cmd-1");
        first.Sequence.Should().Be(1);
        second.Cancel.CommandId.Should().Be("cmd-1");
        second.Sequence.Should().Be(2);
    }

    [Fact]
    public async Task CancelledProducerWaitingForGate_DoesNotEnqueueOrBlockFollowingWrites()
    {
        var session = CreateSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await FillChannelAsync(session, deadline.Token);
        var blocked = session.CancelAsync("blocked", "test", deadline.Token);
        using var cancelled = new CancellationTokenSource();
        var waiting = session.CancelAsync("cancelled", "test", cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(deadline.Token));
        blocked.IsCompleted.Should().BeFalse();

        await session.Reader.ReadAsync(deadline.Token);
        await blocked.WaitAsync(deadline.Token);
        await session.Reader.ReadAsync(deadline.Token);
        await session.CancelAsync("following", "test", deadline.Token);
        session.Complete();
        var frames = await DrainAsync(session, deadline.Token);
        frames.Should().HaveCount(32);
        frames.Should().NotContain(frame => frame.Cancel.CommandId == "cancelled");
        frames.Select(frame => frame.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        frames[^1].Cancel.CommandId.Should().Be("following");
    }

    [Fact]
    public async Task CancelledBlockedWrite_ReleasesGateWithoutPublishingCancelledFrame()
    {
        var session = CreateSession();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await FillChannelAsync(session, deadline.Token);
        using var cancelled = new CancellationTokenSource();
        var blocked = session.CancelAsync("cancelled", "test", cancelled.Token);
        var following = session.CancelAsync("following", "test", deadline.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(deadline.Token));
        await session.Reader.ReadAsync(deadline.Token);
        await following.WaitAsync(deadline.Token);
        session.Complete();

        var frames = await DrainAsync(session, deadline.Token);
        frames.Should().HaveCount(32);
        frames.Should().NotContain(frame => frame.Cancel.CommandId == "cancelled");
        frames.Select(frame => frame.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        frames[^1].Cancel.CommandId.Should().Be("following");
    }

    [Fact]
    public async Task Replacement_ClosesBlockedAndGateWaitingProducers_WithoutLeakingFrames()
    {
        var registry = new AgentCommandGatewaySessionRegistry();
        var client = new ClientKey(71, Guid.NewGuid());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var old = registry.Register(client, Guid.NewGuid(), 4);
        for (var i = 0; i < 32; i++)
            await registry.CancelAsync(client, $"queued-{i}", "test", deadline.Token);
        var blocked = registry.CancelAsync(client, "blocked", "test", deadline.Token);
        var waiting = registry.CancelAsync(client, "waiting", "test", deadline.Token);
        using var current = registry.Register(client, Guid.NewGuid(), 5);

        await Assert.ThrowsAsync<ChannelClosedException>(() => blocked.WaitAsync(deadline.Token));
        await Assert.ThrowsAsync<ChannelClosedException>(() => waiting.WaitAsync(deadline.Token));
        current.Reader.TryRead(out _).Should().BeFalse();
        await registry.CancelAsync(client, "current", "test", deadline.Token);
        var frame = await current.Reader.ReadAsync(deadline.Token);
        frame.Cancel.CommandId.Should().Be("current");
        frame.Sequence.Should().Be(1);
    }

    private static AgentCommandGatewaySession CreateSession() =>
        new(new ClientKey(71, Guid.NewGuid()), Guid.NewGuid(), 4);

    private static async Task FillChannelAsync(AgentCommandGatewaySession session, CancellationToken token)
    {
        for (var i = 0; i < 32; i++)
            await session.CancelAsync($"queued-{i}", "test", token);
    }

    private static async Task<List<GatewayCommandFrame>> DrainAsync(AgentCommandGatewaySession session, CancellationToken token)
    {
        var frames = new List<GatewayCommandFrame>();
        await foreach (var frame in session.Reader.ReadAllAsync(token))
            frames.Add(frame);
        return frames;
    }

    private sealed class PausedFirstWriteChannel : Channel<GatewayCommandFrame>
    {
        public TaskCompletionSource FirstWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;
        public int WriteCount => Volatile.Read(ref _writeCount);

        public PausedFirstWriteChannel()
        {
            var inner = Channel.CreateBounded<GatewayCommandFrame>(32);
            Reader = inner.Reader;
            Writer = new PausedWriter(this, inner.Writer);
        }

        private sealed class PausedWriter(PausedFirstWriteChannel owner, ChannelWriter<GatewayCommandFrame> inner)
            : ChannelWriter<GatewayCommandFrame>
        {
            public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);
            public override bool TryWrite(GatewayCommandFrame item) => inner.TryWrite(item);
            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
                inner.WaitToWriteAsync(cancellationToken);

            public override async ValueTask WriteAsync(GatewayCommandFrame item, CancellationToken cancellationToken = default)
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

}
