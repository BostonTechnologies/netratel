using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Logging;
using Xunit;

namespace NetRatel.Tests.Client;

// These tests prove recovery with the process-wide runtime buffer quiet. Other
// collections must not inject runtime records while a reset is being observed.
[CollectionDefinition("Gateway log session ownership", DisableParallelization = true)]
public sealed class GatewayLogSessionOwnershipCollection;

[Collection("Gateway log session ownership")]
public sealed class AgentLogGatewayClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Quiet_stream_reconnects_after_response_reset_or_eof_and_joins_owned_follows(bool orderlyClose)
    {
        var source = new HeldLogSource();
        var gateway = new ResetLogGateway(ClientRuntimeLogBuffer.Snapshot().Records.Count, orderlyClose);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var presence = new GatewayPresenceSession(7, Guid.NewGuid(), 41, Guid.NewGuid());
        var agent = NewClient(host, source);
        var run = agent.RunForPresenceSessionAsync(presence, "test-token", stopping.Token);
        try
        {
            await gateway.InitialSnapshotDrained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.FollowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gateway.ResetRequested.TrySetResult();
            await source.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gateway.SecondAdmission.Task.IsCompleted.Should().BeFalse("the old follow still owns its writer resources");
            run.IsCompleted.Should().BeFalse();

            source.AllowCleanup.TrySetResult();
            await gateway.SecondAdmission.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.CleanupFinished.Task.IsCompletedSuccessfully.Should().BeTrue();
            gateway.Hellos.Should().HaveCount(2).And.OnlyContain(frame =>
                frame.ConnectionEpoch == presence.ConnectionEpoch && frame.ConnectionId == presence.ConnectionId.ToString("D") &&
                frame.TenantId == presence.TenantId && frame.ClientId == presence.AgentId.ToString("D"));
            gateway.Authorization.Should().Equal("Bearer test-token", "Bearer test-token");
        }
        finally
        {
            stopping.Cancel();
            source.AllowCleanup.TrySetResult();
            gateway.ResetRequested.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Presence_cancellation_joins_the_old_follow_without_retrying_its_fence()
    {
        var source = new HeldLogSource();
        var gateway = new ResetLogGateway(ClientRuntimeLogBuffer.Snapshot().Records.Count, orderlyClose: false);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var presence = new GatewayPresenceSession(7, Guid.NewGuid(), 41, Guid.NewGuid());
        var run = NewClient(host, source).RunForPresenceSessionAsync(presence, "test-token", stopping.Token);
        try
        {
            await source.FollowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping.Cancel();
            await source.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            run.IsCompleted.Should().BeFalse("the old follow must finish before its stream resources are disposed");
            source.AllowCleanup.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            source.CleanupFinished.Task.IsCompletedSuccessfully.Should().BeTrue();
            gateway.Hellos.Should().ContainSingle();
        }
        finally
        {
            stopping.Cancel();
            source.AllowCleanup.TrySetResult();
            gateway.ResetRequested.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopLive_before_the_scheduled_follow_starts_keeps_cancellation_owned_by_the_worker(bool replaceFollow)
    {
        var source = new HeldLogSource();
        source.AllowCleanup.TrySetResult();
        var gateway = new ResetLogGateway(ClientRuntimeLogBuffer.Snapshot().Records.Count, orderlyClose: false,
            stopBeforeFollowStarts: true, replaceFollow: replaceFollow);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var firstScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var schedules = 0;
        var presence = new GatewayPresenceSession(7, Guid.NewGuid(), 41, Guid.NewGuid());
        var run = NewClient(host, source, async work =>
        {
            var first = Interlocked.Increment(ref schedules) == 1;
            (first ? firstScheduled : secondScheduled).TrySetResult();
            await (first ? allowFirst : allowSecond).Task;
            await work();
        }).RunForPresenceSessionAsync(presence, "test-token", stopping.Token);
        try
        {
            await firstScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (replaceFollow)
            {
                await secondScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // Finish the replaced worker after the new source owns the key.
                // Its cleanup must leave the replacement available to StopLive.
                allowFirst.TrySetResult();
                await source.CleanupFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            gateway.StopRequested.TrySetResult();
            await gateway.StopAcknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.StartedTokens.Count.Should().Be(replaceFollow ? 1 : 0);

            (replaceFollow ? allowSecond : allowFirst).TrySetResult();
            await (replaceFollow ? source.SecondCleanupFinished : source.CleanupFinished).Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.StartedTokens.Should().HaveCount(replaceFollow ? 2 : 1).And.OnlyContain(canceled => canceled);
            run.IsCompleted.Should().BeFalse("stopping one source keeps the admitted log session alive");
            gateway.Hellos.Should().ContainSingle();
        }
        finally
        {
            stopping.Cancel();
            allowFirst.TrySetResult();
            allowSecond.TrySetResult();
            gateway.StopRequested.TrySetResult();
            gateway.ResetRequested.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static AgentLogGatewayClient NewClient(IHost host, HeldLogSource source,
        Func<Func<Task>, Task>? startFollow = null) => new(
        new GatewayClientOptions { Endpoint = "https://gateway.test" },
        _ => GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() }),
        () => source, startFollow);

    private static async Task<IHost> BuildHostAsync(ResetLogGateway gateway)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddGrpc();
                services.AddSingleton(gateway);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapGrpcService<ResetLogGateway>());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class ResetLogGateway(int initialRecordCount, bool orderlyClose, bool stopBeforeFollowStarts = false,
        bool replaceFollow = false)
        : AgentLogGateway.AgentLogGatewayBase
    {
        private int _attempts;
        private readonly string _stopRequestId = Guid.NewGuid().ToString("D");
        public TaskCompletionSource InitialSnapshotDrained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResetRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondAdmission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopAcknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<AgentLogFrame> Hellos { get; } = new();
        public ConcurrentQueue<string> Authorization { get; } = new();

        public override async Task Connect(IAsyncStreamReader<AgentLogFrame> requests,
            IServerStreamWriter<GatewayLogFrame> responses, ServerCallContext context)
        {
            if (!await requests.MoveNext(context.CancellationToken)) return;
            var hello = requests.Current.Clone();
            var attempt = Interlocked.Increment(ref _attempts);
            Hellos.Enqueue(hello);
            Authorization.Enqueue(context.RequestHeaders.GetValue("authorization") ?? string.Empty);
            await responses.WriteAsync(new GatewayLogFrame
            {
                ProtocolVersion = hello.ProtocolVersion,
                TenantId = hello.TenantId,
                ClientId = hello.ClientId,
                ConnectionEpoch = hello.ConnectionEpoch,
                ConnectionId = hello.ConnectionId,
                OperationId = hello.OperationId,
                Accepted = new LogConnectAccepted
                {
                    LogAuthority = "akka", MaximumRecordsPerBatch = 100, MaximumEncodedBatchBytes = 64 * 1024
                }
            });
            if (attempt > 1)
            {
                SecondAdmission.TrySetResult();
                var ownerStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try { await ownerStopped.Task.WaitAsync(context.CancellationToken); }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { return; }
                return;
            }

            await responses.WriteAsync(new GatewayLogFrame
            {
                ProtocolVersion = hello.ProtocolVersion,
                TenantId = hello.TenantId,
                ClientId = hello.ClientId,
                ConnectionEpoch = hello.ConnectionEpoch,
                ConnectionId = hello.ConnectionId,
                Sequence = 1,
                Query = new LogQueryRequest { RequestId = Guid.NewGuid().ToString("D"), SourceId = "test-source", Operation = LogQueryOperation.StartLive, PageSize = 1 }
            });
            using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var drain = DrainAsync();
            try
            {
                if (stopBeforeFollowStarts)
                {
                    if (replaceFollow)
                    {
                        await responses.WriteAsync(new GatewayLogFrame
                        {
                            ProtocolVersion = hello.ProtocolVersion,
                            TenantId = hello.TenantId,
                            ClientId = hello.ClientId,
                            ConnectionEpoch = hello.ConnectionEpoch,
                            ConnectionId = hello.ConnectionId,
                            Sequence = 2,
                            Query = new LogQueryRequest { RequestId = Guid.NewGuid().ToString("D"), SourceId = "test-source", Operation = LogQueryOperation.StartLive, PageSize = 1 }
                        });
                    }
                    await StopRequested.Task.WaitAsync(context.CancellationToken);
                    await responses.WriteAsync(new GatewayLogFrame
                    {
                        ProtocolVersion = hello.ProtocolVersion,
                        TenantId = hello.TenantId,
                        ClientId = hello.ClientId,
                        ConnectionEpoch = hello.ConnectionEpoch,
                        ConnectionId = hello.ConnectionId,
                        Sequence = replaceFollow ? 3UL : 2UL,
                        Query = new LogQueryRequest { RequestId = _stopRequestId, SourceId = "test-source", Operation = LogQueryOperation.StopLive, PageSize = 1 }
                    });
                }
                await ResetRequested.Task.WaitAsync(context.CancellationToken);
                if (!orderlyClose) throw new RpcException(new Status(StatusCode.Unavailable, "simulated remote reset"));
            }
            finally
            {
                drainCancellation.Cancel();
                try { await drain; }
                catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested)
                {
                    drain.IsCanceled.Should().BeTrue("the request reader was joined through its owner cancellation");
                }
            }

            async Task DrainAsync()
            {
                var remaining = initialRecordCount;
                if (remaining == 0) InitialSnapshotDrained.TrySetResult();
                while (await requests.MoveNext(drainCancellation.Token))
                {
                    if (requests.Current.QueryResult?.RequestId == _stopRequestId)
                        StopAcknowledged.TrySetResult();
                    if (requests.Current.Batch is not { } batch) continue;
                    remaining -= batch.Records.Count;
                    if (remaining <= 0) InitialSnapshotDrained.TrySetResult();
                }
            }
        }
    }

    private sealed class HeldLogSource : IClientLogSourceAdapter
    {
        public TaskCompletionSource FollowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCleanupFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<bool> StartedTokens { get; } = new();
        private int _cleanups;

        public Task<IReadOnlyList<ClientLogSourceDescriptor>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClientLogSourceDescriptor>>([]);

        public Task<ClientLogPage> ReadHistoryAsync(ClientLogQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new ClientLogPage([], null, null, false));

        public async IAsyncEnumerable<ClientLogFollowResult> FollowAsync(ClientLogQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StartedTokens.Enqueue(cancellationToken.IsCancellationRequested);
            FollowStarted.TrySetResult();
            var ownerStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try { await ownerStopped.Task.WaitAsync(cancellationToken); }
            finally
            {
                CleanupStarted.TrySetResult();
                await AllowCleanup.Task;
                (Interlocked.Increment(ref _cleanups) == 1 ? CleanupFinished : SecondCleanupFinished).TrySetResult();
            }
            yield break;
        }
    }
}
