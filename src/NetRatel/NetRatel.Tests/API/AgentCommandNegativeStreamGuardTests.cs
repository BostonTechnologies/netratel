using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using Xunit;
using Xunit.Sdk;

namespace NetRatel.Tests.API;

public sealed partial class AgentCommandJobControlGatewayServiceTests
{
    [Theory]
    [InlineData(StatusCode.Cancelled)]
    [InlineData(StatusCode.FailedPrecondition)]
    public async Task Negative_write_failure_still_requires_terminal_response_confirmation(StatusCode writeStatus)
    {
        var writer = new RejectionWriter(_ => Task.FromException(WriteRejection(writeStatus)));
        var reads = 0;
        var reader = new RejectionReader(_ => ++reads == 1 ? Task.FromResult(true) :
            Task.FromException<bool>(new RpcException(new Status(StatusCode.FailedPrecondition, "sequence rejected"))));

        await AssertLifecycleRejectionAsync(writer, reader, new(), StatusCode.FailedPrecondition, CancellationToken.None);

        Assert.Equal(1, writer.Calls);
        Assert.Equal(2, reader.Calls);
    }

    [Theory]
    [InlineData(StatusCode.Cancelled)]
    [InlineData(StatusCode.DeadlineExceeded)]
    [InlineData(StatusCode.InvalidArgument)]
    public async Task Write_flush_cancellation_cannot_accept_a_wrong_final_status(StatusCode finalStatus)
    {
        var writer = new RejectionWriter(_ => Task.FromException(WriteRejection(StatusCode.Cancelled)));
        var reader = new RejectionReader(_ => Task.FromException<bool>(new RpcException(new Status(finalStatus, "wrong final status"))));

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            AssertLifecycleRejectionAsync(writer, reader, new(), StatusCode.FailedPrecondition, CancellationToken.None));

        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task Write_flush_cancellation_cannot_accept_a_successful_eof()
    {
        var writer = new RejectionWriter(_ => Task.FromException(WriteRejection(StatusCode.Cancelled)));
        var reader = new RejectionReader(_ => Task.FromResult(false));

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            AssertLifecycleRejectionAsync(writer, reader, new(), StatusCode.FailedPrecondition, CancellationToken.None));

        Assert.Equal(1, reader.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_write_failures_cannot_be_hidden_by_a_later_response(bool rpcFailure)
    {
        Exception failure = rpcFailure ? new RpcException(new Status(StatusCode.Unavailable, "transport failure")) : new IOException("write failure");
        var writer = new RejectionWriter(_ => Task.FromException(failure));
        var reader = new RejectionReader(_ => Task.FromException<bool>(new RpcException(new Status(StatusCode.FailedPrecondition, "sequence rejected"))));

        await Assert.ThrowsAnyAsync<XunitException>(() =>
            AssertLifecycleRejectionAsync(writer, reader, new(), StatusCode.FailedPrecondition, CancellationToken.None));

        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requested_cancellation_is_propagated_during_invalid_write_or_response(bool duringResponse)
    {
        using var cancellation = new CancellationTokenSource();
        var writer = new RejectionWriter(_ =>
        {
            if (duringResponse) return Task.CompletedTask;
            cancellation.Cancel();
            return Task.FromException(WriteRejection(StatusCode.Cancelled));
        });
        var reader = new RejectionReader(_ =>
        {
            cancellation.Cancel();
            return Task.FromException<bool>(new RpcException(new Status(StatusCode.FailedPrecondition, "sequence rejected")));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AssertLifecycleRejectionAsync(writer, reader, new(), StatusCode.FailedPrecondition, cancellation.Token));

        Assert.Equal(duringResponse ? 1 : 0, reader.Calls);
    }

    private static RpcException WriteRejection(StatusCode status) => new(new Status(status, "write closed",
        status == StatusCode.Cancelled ? new OperationCanceledException("Flush was canceled on underlying PipeWriter.") : null));

    private sealed class RejectionWriter(Func<CancellationToken, Task> write) : IClientStreamWriter<AgentCommandFrame>
    {
        public int Calls { get; private set; }
        public WriteOptions? WriteOptions { get; set; }
        public Task CompleteAsync() => Task.CompletedTask;
        public Task WriteAsync(AgentCommandFrame message) => WriteAsync(message, CancellationToken.None);
        public Task WriteAsync(AgentCommandFrame message, CancellationToken cancellationToken) { Calls++; return write(cancellationToken); }
    }

    private sealed class RejectionReader(Func<CancellationToken, Task<bool>> read) : IAsyncStreamReader<GatewayCommandFrame>
    {
        public int Calls { get; private set; }
        public GatewayCommandFrame Current { get; } = new();
        public Task<bool> MoveNext(CancellationToken cancellationToken) { Calls++; return read(cancellationToken); }
    }
}
