using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Gateway;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GatewayDuplexSessionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HalfCompletion_CancelsAndJoinsItsSibling(bool readerCompletes)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingJoined = false;
        async Task Sibling(CancellationToken cancellationToken)
        {
            siblingStarted.SetResult();
            try
            {
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
            }
            finally
            {
                siblingJoined = true;
            }
        }
        Task Completing(CancellationToken _) => completed.Task;
        var running = GatewayDuplexSession.RunAsync(readerCompletes ? Completing : Sibling,
            readerCompletes ? Sibling : Completing, CancellationToken.None, CancellationToken.None, NullLogger.Instance);
        await siblingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        completed.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        siblingJoined.Should().BeTrue();
    }

    [Fact]
    public async Task Replacement_CancelsBothHalvesWithoutWaitingForClientEof()
    {
        using var replacement = new CancellationTokenSource();
        var starts = 0;
        var joins = 0;
        async Task Half(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref starts);
            try
            {
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Increment(ref joins);
            }
        }
        var running = GatewayDuplexSession.RunAsync(Half, Half,
            CancellationToken.None, replacement.Token, NullLogger.Instance);
        starts.Should().Be(2);
        replacement.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        joins.Should().Be(2);
    }

    [Fact]
    public async Task CancellationFault_DoesNotHideConcurrentSiblingProtocolFailure()
    {
        using var replacement = new CancellationTokenSource();
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new RpcException(new Status(StatusCode.InvalidArgument, "Invalid gateway sequence."));
        var running = GatewayDuplexSession.RunAsync(_ => read.Task, _ => write.Task,
            CancellationToken.None, replacement.Token, NullLogger.Instance);
        replacement.Cancel();
        read.SetException(new OperationCanceledException(replacement.Token));
        write.SetException(expected);
        var action = () => running.WaitAsync(TimeSpan.FromSeconds(5));
        (await action.Should().ThrowAsync<RpcException>()).Which.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task ProtocolFailure_RemainsFatalWhenSiblingIsCancelled()
    {
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new RpcException(new Status(StatusCode.InvalidArgument, "Invalid gateway sequence."));
        var running = GatewayDuplexSession.RunAsync(_ => fail.Task,
            cancellationToken => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken),
            CancellationToken.None, CancellationToken.None, NullLogger.Instance);
        fail.SetException(expected);
        var action = () => running.WaitAsync(TimeSpan.FromSeconds(5));
        (await action.Should().ThrowAsync<RpcException>()).Which.Should().BeSameAs(expected);
    }
}
