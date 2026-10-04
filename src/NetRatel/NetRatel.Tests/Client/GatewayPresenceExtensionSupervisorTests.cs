using System.Collections.Concurrent;
using AwesomeAssertions;
using Grpc.Core;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class GatewayPresenceExtensionSupervisorTests
{
    [Fact]
    public async Task ParentLossDuringChildFailure_CancelsRetryWithoutASecondOwner()
    {
        var clock = new GatewayPresenceTestClock();
        var supervisor = new GatewayPresenceExtensionSupervisor(_ => { }, timeProvider: clock, nextRandom: () => 0.5);
        using var stopping = new CancellationTokenSource();
        var attempts = 0;
        var owner = supervisor.RunForPresenceSessionAsync(
            new GatewayPresenceSession(3, Guid.NewGuid(), 2, Guid.NewGuid()), "token", stopping.Token,
            [new GatewayPresenceExtension("file", (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "injected")));
            })]);
        clock.HasTimer(TimeSpan.FromSeconds(1)).Should().BeTrue();
        stopping.Cancel();
        await owner.WaitAsync(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMinutes(1));
        Volatile.Read(ref attempts).Should().Be(1);
    }

    [Fact]
    public async Task TransientChildFailure_RecoversWithoutReplacingHealthyPresence()
    {
        var supervisor = new GatewayPresenceExtensionSupervisor(_ => { });
        using var stopping = new CancellationTokenSource();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var session = new GatewayPresenceSession(3, Guid.NewGuid(), 2, Guid.NewGuid());
        var owner = supervisor.RunForPresenceSessionAsync(session, "token", stopping.Token,
            [new GatewayPresenceExtension("file", async (actualSession, _, token) =>
            {
                actualSession.Should().BeSameAs(session);
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new RpcException(new Status(StatusCode.Unavailable, "injected"));
                recovered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            })]);
        try
        {
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            owner.IsCompleted.Should().BeFalse();
            Volatile.Read(ref attempts).Should().Be(2);
        }
        finally
        {
            stopping.Cancel();
            await owner.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task FaultedChild_DoesNotEndTheParentPresenceOwner()
    {
        var logs = new ConcurrentQueue<string>();
        var supervisor = new GatewayPresenceExtensionSupervisor(logs.Enqueue, TimeSpan.FromMilliseconds(50));
        using var stopping = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var owner = supervisor.RunForPresenceSessionAsync(
            new GatewayPresenceSession(3, Guid.NewGuid(), 2, Guid.NewGuid()),
            "test-token",
            stopping.Token,
            [
                new GatewayPresenceExtension("failed", (_, _, _) => Task.FromException(new InvalidOperationException("injected"))),
                new GatewayPresenceExtension("healthy", async (_, _, cancellationToken) =>
                {
                    started.TrySetResult();
                    var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var registration = cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken));
                    await cancelled.Task;
                })
            ]);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        logs.Should().Contain(message => message.Contains("failed without ending presence", StringComparison.Ordinal));
        owner.IsCompleted.Should().BeFalse();

        stopping.Cancel();
        await owner.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task LateChild_IsBoundedAfterItsPresenceOwnerIsCancelled()
    {
        var logs = new ConcurrentQueue<string>();
        var supervisor = new GatewayPresenceExtensionSupervisor(logs.Enqueue, TimeSpan.FromMilliseconds(25));
        using var stopping = new CancellationTokenSource();
        var neverStops = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var owner = supervisor.RunForPresenceSessionAsync(
            new GatewayPresenceSession(3, Guid.NewGuid(), 2, Guid.NewGuid()),
            "test-token",
            stopping.Token,
            [new GatewayPresenceExtension("late", (_, _, _) => neverStops.Task)]);

        stopping.Cancel();
        await owner.WaitAsync(TimeSpan.FromSeconds(1));
        logs.Should().Contain(message => message.Contains("did not stop within", StringComparison.Ordinal));
    }
}
