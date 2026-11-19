using System.Data.Common;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Metrics;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Services;
using NetRatel.Application.Telemetry;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Infrastructure.Metrics;

public sealed class MetricHistoryWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<MetricHistoryWorker> logger)
    : BackgroundService, IMetricHistoryIngestion
{
    private const int MaximumFrames = 2048;
    private const int MaximumBytes = 32 * 1024 * 1024;
    private const int MaximumTenantFrames = 512;
    private const int MaximumTenantBytes = 8 * 1024 * 1024;
    private const int MaximumTrackedTenants = 4096;
    private const int BatchFrames = 192;
    private static readonly TimeSpan MaximumQueueAge = TimeSpan.FromSeconds(45);
    private readonly object _queueGate = new();
    private readonly Dictionary<int, TenantQueue> _queues = [];
    private readonly Channel<MetricIngress> _channel = Channel.CreateBounded<MetricIngress>(new BoundedChannelOptions(MaximumFrames)
    {
        SingleReader = true, AllowSynchronousContinuations = false
    });
    private long _queuedBytes;

    public void Offer(MonitoringEvidenceFence fence, TelemetrySnapshot snapshot, int payloadBytes)
    {
        if (snapshot.Client != fence.Client || snapshot.ConnectionEpoch != fence.ConnectionEpoch || !snapshot.IsAuthoritative) return;
        var charge = Math.Max(1024 + snapshot.Disks.Count * 400 + snapshot.Networks.Count * 450, 2 * payloadBytes);
        Offer(new(fence, snapshot.Sequence, snapshot.ReceivedAtUtc, snapshot, null, charge));
    }

    public void Offer(MonitoringEvidenceFence fence, ClientServicesState state, int payloadBytes)
    {
        if (state.Client != fence.Client || state.ConnectionId != fence.ConnectionId || state.ConnectionEpoch != fence.ConnectionEpoch ||
            state.LatestAttempt is not { IsFinal: true, Kind: ServiceSnapshotKind.Watch } attempt ||
            attempt.ConnectionEpoch != state.ConnectionEpoch || attempt.Sequence != state.LastAcceptedSequence ||
            attempt.WatchPolicyRevision == 0 || attempt.WatchPolicyRevision != state.WatchPolicyRevision ||
            attempt.RegistrationId is { } source && source != fence.EvidenceStreamId) return;
        var complete = attempt.Status == ServiceCollectionStatus.Complete;
        var watches = state.WatchedServices.Where(service => (!complete || service.ObservedAtUtc == attempt.ObservedAtUtc) &&
            ClientServiceContractValidator.TryValidateObservation(service, out _)).Take(ClientServicesLimits.MaximumWatchServices)
            .Select(service =>
            {
                var reading = MetricHistoryObservations.Service(service, attempt.WatchPolicyRevision);
                return complete ? reading : reading with { At = attempt.ObservedAtUtc, State = null, Quality = MetricHistoryQuality.Missing };
            }).ToArray();
        if (watches.Length == 0) return;
        Offer(new(fence, attempt.Sequence, attempt.ReceivedAtUtc, null, watches, Math.Max(1024 + 400 * watches.Length, 2 * payloadBytes)));
    }

    private void Offer(MetricIngress input)
    {
        lock (_queueGate)
        {
            var queue = GetQueue(input.Fence.Client.TenantId);
            if (queue is null) return;
            queue.LastObserved = MetricMinuteAccumulator.Max(queue.LastObserved.GetValueOrDefault(), input.ReceivedAt);
            if (_queuedBytes + input.Bytes > MaximumBytes || queue.Times.Count == MaximumTenantFrames ||
                queue.Bytes + input.Bytes > MaximumTenantBytes || !_channel.Writer.TryWrite(input))
            {
                queue.Dropped++;
                queue.Disposition = "queue_capacity";
                return;
            }
            queue.Times.Enqueue(input.ReceivedAt);
            queue.Bytes += input.Bytes;
            _queuedBytes += input.Bytes;
        }
    }

    public MetricCollectionStatus IncludeQueue(int tenant, MetricCollectionStatus status)
    {
        var queue = Status(tenant);
        var lastAccepted = status.Enabled && queue.LastObserved > status.LastAcceptedAtUtc.GetValueOrDefault()
            ? queue.LastObserved : status.LastAcceptedAtUtc;
        return status with { LastAcceptedAtUtc = lastAccepted, QueueFrames = queue.Frames, QueueBytes = queue.Bytes, QueueAgeSeconds = queue.Age,
            DroppedFrames = queue.Dropped, Disposition = queue.Disposition ?? status.Disposition };
    }

    private MetricQueueStatus Status(int tenant)
    {
        lock (_queueGate)
        {
            if (!_queues.TryGetValue(tenant, out var queue)) return new(0, 0, null, 0, null, null);
            var age = queue.Times.TryPeek(out var oldest) ? Math.Max(0, (time.GetUtcNow() - oldest).TotalSeconds) : (double?)null;
            return new(queue.Times.Count, queue.Bytes, age, queue.Dropped, queue.Disposition, queue.LastObserved);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(3500, 5001)), time, stoppingToken).ConfigureAwait(false);
                var batch = TakeBatch();
                foreach (var tenant in batch.GroupBy(input => input.Fence.Client.TenantId))
                    await FlushAsync(tenant.Key, tenant, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Historical metrics ingestion stopped.");
        }
    }

    private List<MetricIngress> TakeBatch()
    {
        var batch = new List<MetricIngress>(BatchFrames);
        lock (_queueGate)
        {
            for (var count = 0; count < BatchFrames && _channel.Reader.TryRead(out var input); count++)
            {
                var queue = _queues[input.Fence.Client.TenantId];
                queue.Times.Dequeue();
                queue.Bytes -= input.Bytes;
                _queuedBytes -= input.Bytes;
                batch.Add(input);
            }
        }
        return batch;
    }

    private async Task FlushAsync(int tenant, IEnumerable<MetricIngress> frames, CancellationToken ct)
    {
        var cutoff = time.GetUtcNow() - MaximumQueueAge;
        var batch = frames.Where(frame => frame.ReceivedAt >= cutoff).ToArray();
        var expired = frames.Count() - batch.Length;
        if (expired > 0) Report(tenant, expired, "queue_age");
        if (batch.Length == 0) return;
        var batchId = Guid.NewGuid();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8), time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                await scope.ServiceProvider.GetRequiredService<MetricHistoryStore>()
                    .PersistAsync(tenant, batchId, batch, linked.Token).ConfigureAwait(false);
                if (expired == 0) Report(tenant, 0, null);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (MetricHistoryCapacityException exception)
            {
                Report(tenant, batch.Length, exception.Message);
                return;
            }
            catch (Exception exception) when (attempt < 2 && exception is DbException or TimeoutException or
                MetricHistoryBusyException or OperationCanceledException or Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                logger.LogDebug(exception, "Retrying a historical metrics batch for tenant {TenantId}.", tenant);
                await Task.Delay(TimeSpan.FromMilliseconds((attempt + 1) * 100 + Random.Shared.Next(100)), time, ct).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Report(tenant, batch.Length, "persistence_unavailable");
                logger.LogWarning(exception, "Historical metrics batch dropped for tenant {TenantId}.", tenant);
                return;
            }
        }
    }

    private void Report(int tenant, int dropped, string? disposition)
    {
        lock (_queueGate)
        {
            var queue = GetQueue(tenant);
            if (queue is null) return;
            queue.Dropped += dropped;
            queue.Disposition = disposition;
        }
    }

    private TenantQueue? GetQueue(int tenant)
    {
        if (_queues.TryGetValue(tenant, out var queue)) return queue;
        if (_queues.Count == MaximumTrackedTenants)
        {
            var idle = _queues.FirstOrDefault(item => item.Value.Times.Count == 0);
            if (idle.Value is null) return null;
            _queues.Remove(idle.Key);
        }
        queue = new();
        _queues.Add(tenant, queue);
        return queue;
    }

    private sealed class TenantQueue
    {
        public Queue<DateTimeOffset> Times { get; } = new();
        public long Bytes { get; set; }
        public long Dropped { get; set; }
        public DateTimeOffset? LastObserved { get; set; }
        public string? Disposition { get; set; }
    }
}

internal sealed record MetricQueueStatus(int Frames, long Bytes, double? Age, long Dropped, string? Disposition, DateTimeOffset? LastObserved);
