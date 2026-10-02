using System.Collections.Concurrent;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Services;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.Client;

/// <summary>Exercises the actual duplex publisher against a gRPC TestServer.</summary>
public sealed class AgentGatewayServicesPublisherTests
{
    [Fact]
    public async Task Older_gateway_without_services_capability_keeps_metrics_and_never_collects_services()
    {
        var gateway = new RecordingTelemetryGateway(negotiateServices: false);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var collector = new RecordingCollector();
        var session = Session();
        var run = Publisher(host, collector, fastIntervalSeconds: 1).RunForPresenceSessionAsync(session, "test-token", stopping.Token);
        try
        {
            await gateway.SecondMetric.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, collector.InventoryCalls);
            Assert.Equal(0, collector.WatchCalls);
            var frames = gateway.Frames.ToArray();
            Assert.Contains(ClientServicesLimits.Capability, frames[0].Frame.Hello.Capabilities);
            Assert.All(frames.Skip(1), frame => Assert.Equal(AgentTelemetryFrame.PayloadOneofCase.Snapshot, frame.Frame.PayloadCase));
            Assert.All(frames, frame => Assert.Equal(session.ConnectionId.ToString("D"), frame.Frame.ConnectionId));
        }
        finally { await StopAsync(stopping, run); }
    }

    [Fact]
    public async Task Negotiated_inventory_chunks_wait_for_shared_ack_and_use_consecutive_metric_sequence()
    {
        var gateway = new RecordingTelemetryGateway(holdFirstAcknowledgement: true);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var collector = new RecordingCollector(entryCount: 60);
        var session = Session();
        var run = Publisher(host, collector).RunForPresenceSessionAsync(session, "test-token", stopping.Token);
        try
        {
            var nextRead = await gateway.ReadBeforeAcknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The server is already reading the next request. No inventory chunk may
            // arrive until the pending metrics frame receives its shared ACK credit.
            await Assert.ThrowsAsync<TimeoutException>(() => nextRead.WaitAsync(TimeSpan.FromMilliseconds(150)));
            Assert.Equal(1, collector.InventoryCalls);
            gateway.ReleaseFirstAcknowledgement.TrySetResult();
            await gateway.CompleteInventory.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var frames = gateway.Frames.Select(record => record.Frame).ToArray();
            var data = frames.Skip(1).ToArray();
            Assert.Equal(AgentTelemetryFrame.PayloadOneofCase.Hello, frames[0].PayloadCase);
            Assert.Equal((ulong)0, frames[0].Sequence);
            Assert.Equal(AgentTelemetryFrame.PayloadOneofCase.Snapshot, data[0].PayloadCase);
            Assert.Equal(Enumerable.Range(1, data.Length).Select(index => (ulong)index), data.Select(frame => frame.Sequence));
            Assert.Equal(data.Select(frame => frame.Sequence), gateway.AcknowledgedSequences.Select(record => record.Sequence));
            var chunks = data.Where(frame => frame.ServicesChunk is not null).Select(frame => frame.ServicesChunk).ToArray();
            Assert.True(chunks.Length > 1);
            Assert.Equal(60, chunks.Sum(chunk => chunk.Services.Count));
            Assert.Equal(Enumerable.Range(0, chunks.Length).Select(index => (uint)index), chunks.Select(chunk => chunk.ChunkIndex));
            Assert.All(chunks, chunk => Assert.Equal((ServiceCollectionCompleteness)(int)ServiceCollectionStatus.Complete, chunk.Status));
            Assert.All(chunks[..^1], chunk => Assert.False(chunk.IsFinal));
            Assert.True(chunks[^1].IsFinal);
            Assert.Single(chunks.Select(chunk => chunk.CollectionId).Distinct());
            Assert.All(data, frame =>
            {
                Assert.Equal(session.TenantId, frame.TenantId);
                Assert.Equal(session.AgentId.ToString("D"), frame.ClientId);
                Assert.Equal(session.ConnectionId.ToString("D"), frame.ConnectionId);
                Assert.Equal(session.ConnectionEpoch, frame.ConnectionEpoch);
            });
            Assert.All(data.Where(frame => frame.ServicesChunk is not null), frame =>
                Assert.True(frame.CalculateSize() <= ClientServicesLimits.MaximumChunkPayloadBytes));
            Assert.Equal("Bearer test-token", Assert.Single(gateway.AuthorizationHeaders));
        }
        finally
        {
            gateway.ReleaseFirstAcknowledgement.TrySetResult();
            await StopAsync(stopping, run);
        }
    }

    [Fact]
    public async Task Invalid_ack_retries_telemetry_stream_while_same_presence_session_remains_live()
    {
        // The first server connection sends a wrong ACK and keeps its reader alive.
        // Client failure must cancel that reader before retrying the same presence fence.
        var gateway = new RecordingTelemetryGateway(invalidFirstAcknowledgement: true);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var collector = new RecordingCollector();
        var logs = new ConcurrentQueue<string>();
        var session = Session();
        var run = Publisher(host, collector, logs: logs).RunForPresenceSessionAsync(session, "test-token", stopping.Token);
        try
        {
            await gateway.CompleteInventory.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(gateway.ConnectionCount >= 2);
            Assert.True(collector.InventoryCalls >= 2);
            Assert.Contains(logs, log => log.Contains("invalid acknowledgement", StringComparison.Ordinal));
            var frames = gateway.Frames.ToArray();
            Assert.All(frames, frame =>
            {
                Assert.Equal(session.ConnectionId.ToString("D"), frame.Frame.ConnectionId);
                Assert.Equal(session.ConnectionEpoch, frame.Frame.ConnectionEpoch);
            });
            var admissions = frames.Where(frame => frame.Frame.Hello is not null).ToArray();
            Assert.Equal(2, admissions.Length);
            Assert.All(admissions, frame => Assert.Equal((ulong)0, frame.Frame.Sequence));
            var second = frames.Where(frame => frame.Connection == 2 && frame.Frame.Hello is null).ToArray();
            Assert.Equal(Enumerable.Range(2, second.Length).Select(index => (ulong)index), second.Select(frame => frame.Frame.Sequence));
            var sentCollection = Assert.Single(second.Where(frame => frame.Frame.ServicesChunk is not null)).Frame.ServicesChunk.CollectionId;
            Assert.Equal(collector.CollectionIds.Last().ToString("D"), sentCollection);
            Assert.False(run.IsCompleted);
        }
        finally { await StopAsync(stopping, run); }
    }

    [Fact]
    public async Task Lost_inventory_ack_reconnects_with_strictly_newer_sequences_and_complete_rediscovery()
    {
        // The gateway commits the first full inventory, then returns an invalid ACK.
        // Its durable cursor survives the stream; retrying sequence 1 would never recover.
        var gateway = new RecordingTelemetryGateway(invalidFirstInventoryAcknowledgement: true, enforceDurableSequence: true);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var collector = new RecordingCollector(entryCount: 60);
        var session = Session();
        var run = Publisher(host, collector).RunForPresenceSessionAsync(session, "test-token", stopping.Token);
        try
        {
            await gateway.SecondCompleteInventory.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var frames = gateway.Frames.ToArray();
            var first = frames.Where(record => record.Connection == 1 && record.Frame.Hello is null).Select(record => record.Frame).ToArray();
            var second = frames.Where(record => record.Connection == 2 && record.Frame.Hello is null).Select(record => record.Frame).ToArray();
            Assert.True(first.Length > 2);
            Assert.Equal(60, first.Where(frame => frame.ServicesChunk is not null).Sum(frame => frame.ServicesChunk.Services.Count));
            Assert.Equal(60, second.Where(frame => frame.ServicesChunk is not null).Sum(frame => frame.ServicesChunk.Services.Count));
            Assert.True(first.Last().ServicesChunk.IsFinal);
            Assert.True(second.Last().ServicesChunk.IsFinal);
            var lastCommittedSequence = first.Max(frame => frame.Sequence);
            Assert.All(second, frame => Assert.True(frame.Sequence > lastCommittedSequence));
            Assert.Equal(Enumerable.Range(1, first.Length + second.Length).Select(index => (ulong)index), first.Concat(second).Select(frame => frame.Sequence));
            Assert.NotEqual(first.Last().ServicesChunk.CollectionId, second.Last().ServicesChunk.CollectionId);
            Assert.All(frames, record =>
            {
                Assert.Equal(session.ConnectionId.ToString("D"), record.Frame.ConnectionId);
                Assert.Equal(session.ConnectionEpoch, record.Frame.ConnectionEpoch);
            });
            Assert.Equal(2, collector.InventoryCalls);
            Assert.Equal(0, gateway.RejectedDurableSequences);
            Assert.False(run.IsCompleted);
        }
        finally { await StopAsync(stopping, run); }
    }

    [Fact]
    public async Task Presence_cancellation_cancels_in_progress_service_query_and_joins_reader()
    {
        var gateway = new RecordingTelemetryGateway();
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var collector = new RecordingCollector(blockUntilCancelled: true);
        var run = Publisher(host, collector).RunForPresenceSessionAsync(Session(), "test-token", stopping.Token);
        try
        {
            await collector.CollectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await StopAsync(stopping, run);
            await collector.CollectionCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, collector.InventoryCalls);
            Assert.DoesNotContain(gateway.Frames, record => record.Frame.ServicesChunk is not null);
        }
        finally { await StopAsync(stopping, run); }
    }

    [Fact]
    public async Task Explicit_refresh_with_same_policy_revision_collects_again_after_admission()
    {
        var time = new MutableTimeProvider();
        var initial = new ServiceWatchPolicy
        {
            Revision = 7,
            WatchIntervalSeconds = 30,
            InventoryIntervalSeconds = 900,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(time.GetUtcNow().AddMinutes(30))
        };
        var gateway = new RecordingTelemetryGateway
        {
            InitialServicePolicy = initial,
            RefreshAfterFirstInventory = true
        };
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var collector = new RecordingCollector();
        var run = Publisher(host, collector, timeProvider: time).RunForPresenceSessionAsync(Session(), "test-token", stopping.Token);
        try
        {
            await gateway.CompleteInventory.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, collector.InventoryCalls);
            time.Advance(ClientServicesLimits.MinimumRefreshInterval.Add(TimeSpan.FromSeconds(1)));
            var refresh = initial.Clone();
            refresh.RefreshRequestId = Guid.NewGuid().ToString("D");
            refresh.ExpiresAtUtc = Timestamp.FromDateTimeOffset(time.GetUtcNow().AddMinutes(30));
            gateway.RequestedRefreshPolicy.TrySetResult(refresh);
            await gateway.SecondCompleteInventory.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, collector.InventoryCalls);
            Assert.Equal(0, collector.WatchCalls);
            Assert.Equal([7UL, 7UL], gateway.SentServicePolicies.Select(policy => policy.Revision));
            Assert.Equal(refresh.RefreshRequestId, gateway.SentServicePolicies.Last().RefreshRequestId);
            var collections = gateway.Frames.Where(frame => frame.Frame.ServicesChunk is { IsFinal: true })
                .Select(frame => frame.Frame.ServicesChunk.CollectionId).ToArray();
            Assert.Equal(2, collections.Distinct().Count());
        }
        finally { await StopAsync(stopping, run); }
    }

    private static GatewayPresenceSession Session() => new(29, Guid.NewGuid(), 8, Guid.NewGuid());

    private static AgentGatewayTelemetryPublisher Publisher(IHost host, RecordingCollector collector,
        int fastIntervalSeconds = 60, ConcurrentQueue<string>? logs = null, TimeProvider? timeProvider = null) => new(
        new GatewayClientOptions { Endpoint = "https://gateway.test", TelemetryFastIntervalSeconds = fastIntervalSeconds },
        "services-integration", message => logs?.Enqueue(message), timeProvider: timeProvider, serviceInventoryCollector: collector,
        channelFactory: _ => GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = host.GetTestServer().CreateHandler()
        }));

    private static async Task StopAsync(CancellationTokenSource stopping, Task run)
    {
        stopping.Cancel();
        try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private static Task<IHost> BuildHostAsync(RecordingTelemetryGateway gateway) => Host.CreateDefaultBuilder()
        .ConfigureWebHost(web =>
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
                app.UseEndpoints(endpoints => endpoints.MapGrpcService<RecordingTelemetryGateway>());
            });
        }).StartAsync();

    private sealed class RecordingCollector(int entryCount = 1, bool blockUntilCancelled = false) : IServiceInventoryCollector
    {
        private int _inventoryCalls;
        private int _watchCalls;
        public int InventoryCalls => Volatile.Read(ref _inventoryCalls);
        public int WatchCalls => Volatile.Read(ref _watchCalls);
        public ConcurrentQueue<Guid> CollectionIds { get; } = new();
        public TaskCompletionSource CollectionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CollectionCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ServiceCollectionResult> CollectInventoryAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _inventoryCalls);
            CollectionStarted.TrySetResult();
            if (blockUntilCancelled)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException)
                {
                    CollectionCancelled.TrySetResult();
                    throw;
                }
            }
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            CollectionIds.Enqueue(id);
            var rows = Enumerable.Range(0, entryCount).Select(index => new ClientServiceObservation(
                $"service-{index}", new string('d', ClientServicesLimits.MaximumDisplayNameLength), ClientServicePlatform.Windows,
                ClientServiceState.Running, "SERVICE_RUNNING", "Automatic", null, null, null, null, now)).ToArray();
            return new(id, ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Complete, now, rows);
        }

        public Task<ServiceCollectionResult> CollectWatchAsync(IReadOnlyList<string> names, ulong policyRevision, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _watchCalls);
            return Task.FromResult(new ServiceCollectionResult(Guid.NewGuid(), ServiceSnapshotKind.Watch,
                ServiceCollectionStatus.Complete, DateTimeOffset.UtcNow, [], WatchPolicyRevision: policyRevision));
        }
    }

    private sealed class RecordingTelemetryGateway(bool negotiateServices = true, bool holdFirstAcknowledgement = false,
        bool invalidFirstAcknowledgement = false, bool invalidFirstInventoryAcknowledgement = false,
        bool enforceDurableSequence = false) : global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Base
    {
        private int _connectionCount;
        private int _inventoryCompletions;
        private ulong _durableLastAcceptedSequence;
        private int _rejectedDurableSequences;
        private readonly object _durableCursorGate = new();
        public int ConnectionCount => Volatile.Read(ref _connectionCount);
        public int RejectedDurableSequences => Volatile.Read(ref _rejectedDurableSequences);
        public ConcurrentQueue<(int Connection, AgentTelemetryFrame Frame)> Frames { get; } = new();
        public ConcurrentQueue<(int Connection, ulong Sequence)> AcknowledgedSequences { get; } = new();
        public ConcurrentQueue<string> AuthorizationHeaders { get; } = new();
        public ConcurrentQueue<ServiceWatchPolicy> SentServicePolicies { get; } = new();
        public ServiceWatchPolicy? InitialServicePolicy { get; init; }
        public bool RefreshAfterFirstInventory { get; init; }
        public TaskCompletionSource SecondMetric { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteInventory { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCompleteInventory { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ServiceWatchPolicy> RequestedRefreshPolicy { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Task<bool>> ReadBeforeAcknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstAcknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task Connect(IAsyncStreamReader<AgentTelemetryFrame> requestStream,
            IServerStreamWriter<GatewayTelemetryFrame> responseStream, ServerCallContext context)
        {
            var connection = Interlocked.Increment(ref _connectionCount);
            AuthorizationHeaders.Enqueue(context.RequestHeaders.GetValue("authorization") ?? string.Empty);
            var metrics = 0;
            Task<bool>? pendingRead = null;
            try
            {
                while (await (pendingRead ?? requestStream.MoveNext(context.CancellationToken)))
                {
                    pendingRead = null;
                    var frame = requestStream.Current.Clone();
                    Frames.Enqueue((connection, frame));
                    if (frame.Hello is not null)
                    {
                        var admission = new TelemetryConnectAccepted { TelemetryAuthority = "akka", MaximumInFlightFrames = 1 };
                        if (negotiateServices) admission.AcceptedCapabilities.Add(ClientServicesLimits.Capability);
                        await responseStream.WriteAsync(new GatewayTelemetryFrame { Accepted = admission });
                        if (InitialServicePolicy is { } initial)
                        {
                            SentServicePolicies.Enqueue(initial.Clone());
                            await responseStream.WriteAsync(new GatewayTelemetryFrame { ServiceWatchPolicy = initial });
                        }
                        continue;
                    }
                    if (frame.Snapshot is not null)
                    {
                        metrics++;
                        if (metrics == 2) SecondMetric.TrySetResult();
                        if (holdFirstAcknowledgement && connection == 1 && metrics == 1)
                        {
                            pendingRead = requestStream.MoveNext(context.CancellationToken);
                            ReadBeforeAcknowledgement.TrySetResult(pendingRead);
                            await ReleaseFirstAcknowledgement.Task.WaitAsync(context.CancellationToken);
                        }
                    }
                    if (enforceDurableSequence)
                    {
                        lock (_durableCursorGate)
                        {
                            if (frame.Sequence <= _durableLastAcceptedSequence)
                            {
                                Interlocked.Increment(ref _rejectedDurableSequences);
                                throw new RpcException(new Status(StatusCode.Aborted, "Durable sequence cursor rejected replay."));
                            }
                            _durableLastAcceptedSequence = frame.Sequence;
                        }
                    }
                    var invalidAck = connection == 1 && (invalidFirstAcknowledgement ||
                        (invalidFirstInventoryAcknowledgement && frame.ServicesChunk is { IsFinal: true }));
                    var ack = invalidAck ? frame.Sequence + 99 : frame.Sequence;
                    // Commit completion before ACK; loss of the response cannot undo accepted inventory.
                    var inventoryCommitted = frame.ServicesChunk is { IsFinal: true } committedChunk &&
                        committedChunk.Kind == (ServiceSnapshotType)(int)ServiceSnapshotKind.Inventory;
                    var inventories = inventoryCommitted ? Interlocked.Increment(ref _inventoryCompletions) : 0;
                    await responseStream.WriteAsync(new GatewayTelemetryFrame
                    {
                        SnapshotAccepted = new TelemetrySnapshotAccepted { AcceptedSequence = ack, AvailableCredits = 1 }
                    });
                    AcknowledgedSequences.Enqueue((connection, ack));
                    if (inventoryCommitted)
                    {
                        CompleteInventory.TrySetResult();
                        if (inventories == 2) SecondCompleteInventory.TrySetResult();
                        if (RefreshAfterFirstInventory && inventories == 1)
                        {
                            var refresh = await RequestedRefreshPolicy.Task.WaitAsync(context.CancellationToken);
                            SentServicePolicies.Enqueue(refresh.Clone());
                            await responseStream.WriteAsync(new GatewayTelemetryFrame { ServiceWatchPolicy = refresh });
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
    }
}
