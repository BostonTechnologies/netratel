using FluentAssertions;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportGatewaySignallingRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignallingCloseRetriesWhilePreparationRemainsHealthyAndOwnerCancellationStopsBoth(bool orderlyClose)
    {
        using var stopping = new CancellationTokenSource();
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationStopped = false;
        var attempts = 0;
        var logs = new List<string>();
        async Task Signalling(CancellationToken ct)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                if (orderlyClose) return;
                throw new IOException("injected signalling transport reset");
            }

            retryStarted.TrySetResult();
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(ct);
        }

        async Task Preparation()
        {
            try { await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(stopping.Token); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { preparationStopped = true; }
        }

        var preparation = Preparation();
        var signalling = AgentRemoteSupportGatewayClient.RunSignallingWithRetryAsync(
            Signalling, logs.Add, stopping.Token, TimeSpan.FromMilliseconds(1));
        var owner = Task.WhenAll(signalling, preparation);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        attempts.Should().Be(2);
        if (orderlyClose) logs.Should().BeEmpty();
        else logs.Should().ContainSingle(message => message.Contains("injected signalling transport reset", StringComparison.Ordinal));
        preparation.IsCompleted.Should().BeFalse();
        owner.IsCompleted.Should().BeFalse();

        stopping.Cancel();
        await owner.WaitAsync(TimeSpan.FromSeconds(5));
        preparationStopped.Should().BeTrue();
        attempts.Should().Be(2);
    }
}
