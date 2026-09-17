using System.Collections.Concurrent;
using FluentAssertions;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class GatewayPresenceExtensionSupervisorTests
{
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
