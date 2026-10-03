using FluentAssertions;
using NetRatel.Client.Service.RemoteDesktop;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class HelperTaskRegistrationStateTests
{
    [Fact]
    public async Task DuplicateCallersShareOneSuccessfulRegistrationAndChangedStateReregisters()
    {
        var state = new HelperTaskRegistrationState();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task Register()
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task;
        }

        var first = state.EnsureAsync("same", Register, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicates = Enumerable.Range(0, 12).Select(_ => state.EnsureAsync("same", Register, CancellationToken.None)).ToArray();
        calls.Should().Be(1);
        release.SetResult();
        await Task.WhenAll(duplicates.Append(first)).WaitAsync(TimeSpan.FromSeconds(5));
        calls.Should().Be(1);
        await state.EnsureAsync("changed", () => { calls++; return Task.CompletedTask; }, CancellationToken.None);
        await state.EnsureAsync("changed", () => { calls++; return Task.CompletedTask; }, CancellationToken.None, force: true);
        calls.Should().Be(3);
    }

    [Fact]
    public async Task FailedAndCanceledRegistrationRemainEligibleForRetry()
    {
        var state = new HelperTaskRegistrationState();
        Func<Task> fail = () => state.EnsureAsync("same", () => throw new IOException("fixture"), CancellationToken.None);
        await fail.Should().ThrowAsync<IOException>();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Func<Task> cancel = () => state.EnsureAsync("same", () => throw new InvalidOperationException("must not execute"), canceled.Token);
        await cancel.Should().ThrowAsync<OperationCanceledException>();
        var calls = 0;
        await state.EnsureAsync("same", () => { calls++; return Task.CompletedTask; }, CancellationToken.None);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task CancelingQueuedCallerDoesNotCancelRegistrationOwnerOrMarkWorkComplete()
    {
        var state = new HelperTaskRegistrationState();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = state.EnsureAsync("same", () => release.Task, CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        var queued = state.EnsureAsync("same", () => throw new InvalidOperationException("must not execute"), canceled.Token);
        canceled.Cancel();
        await ((Func<Task>)(() => queued)).Should().ThrowAsync<OperationCanceledException>();
        owner.IsCompleted.Should().BeFalse();
        release.SetResult();
        await owner;
        await state.EnsureAsync("same", () => throw new InvalidOperationException("must be cached"), CancellationToken.None);
    }
}
