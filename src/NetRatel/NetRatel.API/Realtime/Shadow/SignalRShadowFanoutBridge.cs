using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using NetRatel.Akka.Observability;
using NetRatel.Application.Fanout;

namespace NetRatel.API.Realtime.Shadow;

public interface IShadowFanoutSink
{
    bool TryEnqueue(ShadowFanoutEnvelope envelope);

    SignalRShadowFanoutStatus GetStatus();
}

public interface IShadowFanoutSnapshotSource
{
    IReadOnlyList<ShadowFanoutEnvelope> GetSnapshots(ShadowFanoutTarget target);
}

internal static class ShadowFanoutSinkExtensions
{
    public static bool TryEnqueueBestEffort(
        this IShadowFanoutSink sink,
        ShadowFanoutEnvelope envelope)
    {
        try
        {
            return sink.TryEnqueue(envelope);
        }
        catch (Exception)
        {
            // Fanout is diagnostic-only. A third-party or test sink must not
            // rewrite the already-recorded shadow outcome of its producer.
            return false;
        }
    }
}

public sealed record SignalRShadowFanoutStatus(
    bool WorkerRunning,
    ulong Enqueued,
    ulong Coalesced,
    ulong Dropped,
    ulong Rejected,
    ulong Published,
    ulong PublishFailures,
    ulong PublishTimeouts,
    int CurrentDepth,
    int HighWaterMark,
    TimeSpan? OldestPendingAge,
    DateTimeOffset? LastEnqueuedAtUtc,
    DateTimeOffset? LastPublishedAtUtc,
    DateTimeOffset? LastDroppedAtUtc,
    DateTimeOffset? LastFailureAtUtc,
    int CurrentTelemetryKeys = 0,
    int InFlightPublishes = 0,
    int InFlightHighWaterMark = 0,
    TimeSpan? OldestInFlightAge = null);

public sealed class NullShadowFanoutSink : IShadowFanoutSink
{
    public static NullShadowFanoutSink Instance { get; } = new();

    private NullShadowFanoutSink()
    {
    }

    public bool TryEnqueue(ShadowFanoutEnvelope envelope) => false;

    public SignalRShadowFanoutStatus GetStatus() =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, null, null);
}

/// <summary>
/// A bounded, latest-state relay. Producers never wait for SignalR. One
/// pending value is retained per category/group key and publication is
/// serialized per key while a fixed worker pool prevents cross-group
/// head-of-line blocking.
/// </summary>
public sealed class SignalRShadowFanoutBridge : BackgroundService, IShadowFanoutSink, IShadowFanoutSnapshotSource
{
    internal const int Capacity = 2_048;
    internal const int TelemetryKeyCapacity = 1_024;
    internal const int SnapshotCapacity = 2_048;
    internal const int ResyncMarkerCapacity = 2_048;
    internal const int MaximumConcurrentPublishes = 8;
    internal static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(2);
    internal const string ClientMethod = "shadowUpdated";

    private readonly IHubContext<AkkaShadowHub> _hubContext;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _publishTimeout;
    private readonly int _maximumConcurrentPublishes;
    private readonly object _gate = new();
    private readonly Channel<FanoutKey> _readyKeys = Channel.CreateBounded<FanoutKey>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly Dictionary<FanoutKey, PendingFanout> _pending = new(Capacity);
    private readonly Dictionary<FanoutKey, InFlightFanout> _inFlight = new(MaximumConcurrentPublishes);
    private readonly HashSet<FanoutKey> _retainedKeys = new();
    private readonly Dictionary<FanoutKey, SnapshotState> _snapshots = new(SnapshotCapacity);
    private readonly Queue<FanoutKey> _snapshotOrder = new(SnapshotCapacity);
    private readonly Dictionary<FanoutKey, ResyncState> _resyncRequiredThrough = new(ResyncMarkerCapacity);
    private readonly LinkedList<FanoutKey> _resyncOrder = new();
    private int _retainedTelemetryKeys;
    private bool _workerRunning;
    private ulong _nextGeneration;
    private ulong _enqueued;
    private ulong _coalesced;
    private ulong _dropped;
    private ulong _rejected;
    private ulong _published;
    private ulong _publishFailures;
    private ulong _publishTimeouts;
    private int _highWaterMark;
    private int _inFlightHighWaterMark;
    private DateTimeOffset? _lastEnqueuedAtUtc;
    private DateTimeOffset? _lastPublishedAtUtc;
    private DateTimeOffset? _lastDroppedAtUtc;
    private DateTimeOffset? _lastFailureAtUtc;

    public SignalRShadowFanoutBridge(
        IHubContext<AkkaShadowHub> hubContext,
        TimeProvider timeProvider)
        : this(hubContext, timeProvider, PublishTimeout, MaximumConcurrentPublishes)
    {
    }

    internal SignalRShadowFanoutBridge(
        IHubContext<AkkaShadowHub> hubContext,
        TimeProvider timeProvider,
        TimeSpan publishTimeout,
        int maximumConcurrentPublishes)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(publishTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentPublishes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumConcurrentPublishes, Capacity);

        _hubContext = hubContext;
        _timeProvider = timeProvider;
        _publishTimeout = publishTimeout;
        _maximumConcurrentPublishes = maximumConcurrentPublishes;
    }

    public bool TryEnqueue(ShadowFanoutEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!envelope.IsValid || !SignalRShadowGroupName.TryCreate(envelope.Target, out var groupName))
        {
            lock (_gate)
            {
                _rejected = IncrementSaturating(_rejected);
                NetRatelAkkaTelemetry.RecordSignalRDropped();
            }

            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var key = new FanoutKey(envelope.Category, groupName);
        lock (_gate)
        {
            var generation = IncrementSaturating(_nextGeneration);
            _nextGeneration = generation;
            if (envelope.ResyncRequired)
            {
                MarkResyncRequired(key, generation);
            }

            UpdateSnapshot(key, envelope);

            if (_pending.TryGetValue(key, out var existing))
            {
                _pending[key] = existing with
                {
                    Envelope = envelope with
                    {
                        ResyncRequired = existing.Envelope.ResyncRequired ||
                            envelope.ResyncRequired || IsResyncRequired(key)
                    },
                    Generation = generation,
                };
                _coalesced = IncrementSaturating(_coalesced);
                _lastEnqueuedAtUtc = now;
                return true;
            }

            var isNewRetainedKey = !_retainedKeys.Contains(key);
            if ((isNewRetainedKey && _retainedKeys.Count >= Capacity) ||
                (isNewRetainedKey && envelope.Category == ShadowFanoutCategory.Telemetry &&
                 _retainedTelemetryKeys >= TelemetryKeyCapacity))
            {
                MarkResyncRequired(key, generation);
                _dropped = IncrementSaturating(_dropped);
                NetRatelAkkaTelemetry.RecordSignalRDropped();
                _lastDroppedAtUtc = now;
                return false;
            }

            if (isNewRetainedKey)
            {
                _retainedKeys.Add(key);
                if (envelope.Category == ShadowFanoutCategory.Telemetry)
                {
                    _retainedTelemetryKeys++;
                }
            }

            var pending = new PendingFanout(
                envelope with { ResyncRequired = envelope.ResyncRequired || IsResyncRequired(key) },
                generation,
                now);
            _pending.Add(key, pending);

            if (!_inFlight.ContainsKey(key) && !_readyKeys.Writer.TryWrite(key))
            {
                _pending.Remove(key);
                if (isNewRetainedKey)
                {
                    ReleaseRetainedKey(key);
                }

                MarkResyncRequired(key, generation);
                _dropped = IncrementSaturating(_dropped);
                NetRatelAkkaTelemetry.RecordSignalRDropped();
                _lastDroppedAtUtc = now;
                return false;
            }

            _enqueued = IncrementSaturating(_enqueued);
            _lastEnqueuedAtUtc = now;
            _highWaterMark = Math.Max(_highWaterMark, _retainedKeys.Count);
            RecordQueueStatusUnsafe();
            return true;
        }
    }

    public IReadOnlyList<ShadowFanoutEnvelope> GetSnapshots(ShadowFanoutTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!SignalRShadowGroupName.TryCreate(target, out var groupName))
        {
            return Array.Empty<ShadowFanoutEnvelope>();
        }

        lock (_gate)
        {
            return _snapshots
                .Where(entry => StringComparer.Ordinal.Equals(entry.Key.GroupName, groupName))
                .OrderBy(entry => entry.Key.Category)
                .Select(entry => entry.Value.Envelope with
                {
                    EventType = ShadowFanoutEventType.Snapshot,
                    ResyncRequired = entry.Value.Envelope.ResyncRequired || IsResyncRequired(entry.Key)
                })
                .ToArray();
        }
    }

    public SignalRShadowFanoutStatus GetStatus()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            TimeSpan? oldestPendingAge = _pending.Count == 0
                ? null
                : MaxZero(now - _pending.Values.Min(static pending => pending.EnqueuedAtUtc));
            TimeSpan? oldestInFlightAge = _inFlight.Count == 0
                ? null
                : MaxZero(now - _inFlight.Values.Min(static inFlight => inFlight.StartedAtUtc));

            var status = new SignalRShadowFanoutStatus(
                _workerRunning,
                _enqueued,
                _coalesced,
                _dropped,
                _rejected,
                _published,
                _publishFailures,
                _publishTimeouts,
                _retainedKeys.Count,
                _highWaterMark,
                oldestPendingAge,
                _lastEnqueuedAtUtc,
                _lastPublishedAtUtc,
                _lastDroppedAtUtc,
                _lastFailureAtUtc,
                _retainedTelemetryKeys,
                _inFlight.Count,
                _inFlightHighWaterMark,
                oldestInFlightAge);
            NetRatelAkkaTelemetry.SetSignalRQueue(status.CurrentDepth, status.HighWaterMark);
            return status;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SetWorkerRunning(true);
        try
        {
            var workers = Enumerable.Range(0, _maximumConcurrentPublishes)
                .Select(_ => RunWorkerAsync(stoppingToken))
                .ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            SetWorkerRunning(false);
        }
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        await foreach (var key in _readyKeys.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!TryBeginPublication(key, out var pending))
            {
                continue;
            }

            try
            {
                await PublishAsync(key, pending, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                CompletePublication(key);
            }
        }
    }

    private async Task PublishAsync(
        FanoutKey key,
        PendingFanout pending,
        CancellationToken stoppingToken)
    {
        using var activity = NetRatelAkkaTelemetry.StartActivity("akka.signalr.publish", "signalr");
        var envelope = PrepareEnvelopeForPublication(key, pending.Envelope);
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task sendTask;
        try
        {
            sendTask = _hubContext.Clients.Group(key.GroupName).SendCoreAsync(
                ClientMethod,
                [envelope],
                attemptCancellation.Token);
        }
        catch (Exception)
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                RecordPublishFailure(key, pending.Generation, timedOut: false);
            }

            return;
        }

        try
        {
            await sendTask.WaitAsync(_publishTimeout, _timeProvider, stoppingToken).ConfigureAwait(false);
            lock (_gate)
            {
                _published = IncrementSaturating(_published);
                NetRatelAkkaTelemetry.RecordSignalRPublished();
                _lastPublishedAtUtc = _timeProvider.GetUtcNow();
                ClearResyncAfterSuccessfulPublication(key, pending.Generation);
            }
        }
        catch (TimeoutException)
        {
            _ = TryCancelAttempt(attemptCancellation);
            ObserveLateFault(sendTask);
            RecordPublishFailure(key, pending.Generation, timedOut: true);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _ = TryCancelAttempt(attemptCancellation);
            ObserveLateFault(sendTask);
        }
        catch (Exception)
        {
            RecordPublishFailure(key, pending.Generation, timedOut: false);
        }
    }

    private bool TryBeginPublication(FanoutKey key, out PendingFanout pending)
    {
        lock (_gate)
        {
            if (!_pending.Remove(key, out pending!))
            {
                pending = default!;
                return false;
            }

            _inFlight.Add(key, new InFlightFanout(_timeProvider.GetUtcNow()));
            _inFlightHighWaterMark = Math.Max(_inFlightHighWaterMark, _inFlight.Count);
            return true;
        }
    }

    private void CompletePublication(FanoutKey key)
    {
        lock (_gate)
        {
            _inFlight.Remove(key);
            if (_pending.ContainsKey(key))
            {
                if (!_readyKeys.Writer.TryWrite(key))
                {
                    var pending = _pending[key];
                    _pending.Remove(key);
                    MarkResyncRequired(key, pending.Generation);
                    _dropped = IncrementSaturating(_dropped);
                    NetRatelAkkaTelemetry.RecordSignalRDropped();
                    _lastDroppedAtUtc = _timeProvider.GetUtcNow();
                    ReleaseRetainedKey(key);
                }

                return;
            }

            ReleaseRetainedKey(key);
        }
    }

    private ShadowFanoutEnvelope PrepareEnvelopeForPublication(
        FanoutKey key,
        ShadowFanoutEnvelope envelope)
    {
        lock (_gate)
        {
            return envelope.ResyncRequired || IsResyncRequired(key)
                ? envelope with { ResyncRequired = true }
                : envelope;
        }
    }

    private void RecordPublishFailure(FanoutKey key, ulong generation, bool timedOut)
    {
        lock (_gate)
        {
            if (timedOut)
            {
                _publishTimeouts = IncrementSaturating(_publishTimeouts);
                NetRatelAkkaTelemetry.RecordSignalRTimedOut();
            }
            else
            {
                _publishFailures = IncrementSaturating(_publishFailures);
                NetRatelAkkaTelemetry.RecordSignalRFailed();
            }

            _lastFailureAtUtc = _timeProvider.GetUtcNow();
            MarkResyncRequired(key, generation);
        }
    }

    private void RecordQueueStatusUnsafe() =>
        NetRatelAkkaTelemetry.SetSignalRQueue(_retainedKeys.Count, _highWaterMark);

    private void UpdateSnapshot(FanoutKey key, ShadowFanoutEnvelope envelope)
    {
        if (_snapshots.ContainsKey(key))
        {
            _snapshots[key] = new SnapshotState(envelope with { ResyncRequired = false });
            return;
        }

        if (_snapshots.Count >= SnapshotCapacity && !TryEvictInactiveSnapshot())
        {
            return;
        }

        _snapshots.Add(key, new SnapshotState(envelope with { ResyncRequired = false }));
        _snapshotOrder.Enqueue(key);
    }

    private bool TryEvictInactiveSnapshot()
    {
        var candidates = _snapshotOrder.Count;
        for (var index = 0; index < candidates; index++)
        {
            var candidate = _snapshotOrder.Dequeue();
            if (_retainedKeys.Contains(candidate))
            {
                _snapshotOrder.Enqueue(candidate);
                continue;
            }

            if (_snapshots.Remove(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private void MarkResyncRequired(FanoutKey key, ulong generation)
    {
        if (_resyncRequiredThrough.TryGetValue(key, out var existing))
        {
            _resyncRequiredThrough[key] = existing with
            {
                RequiredThroughGeneration = Math.Max(existing.RequiredThroughGeneration, generation)
            };
            return;
        }

        if (_resyncRequiredThrough.Count >= ResyncMarkerCapacity && _resyncOrder.First is { } oldest)
        {
            _resyncOrder.RemoveFirst();
            _resyncRequiredThrough.Remove(oldest.Value);
        }

        if (_resyncRequiredThrough.Count < ResyncMarkerCapacity)
        {
            var node = _resyncOrder.AddLast(key);
            _resyncRequiredThrough.Add(key, new ResyncState(generation, node));
        }
    }

    private bool IsResyncRequired(FanoutKey key) => _resyncRequiredThrough.ContainsKey(key);

    private void ClearResyncAfterSuccessfulPublication(FanoutKey key, ulong generation)
    {
        if (_resyncRequiredThrough.TryGetValue(key, out var state) &&
            generation >= state.RequiredThroughGeneration)
        {
            _resyncRequiredThrough.Remove(key);
            _resyncOrder.Remove(state.OrderNode);
        }
    }

    private void ReleaseRetainedKey(FanoutKey key)
    {
        if (!_retainedKeys.Remove(key))
        {
            return;
        }

        if (key.Category == ShadowFanoutCategory.Telemetry)
        {
            _retainedTelemetryKeys--;
        }
    }

    private void SetWorkerRunning(bool running)
    {
        lock (_gate)
        {
            _workerRunning = running;
        }
    }

    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool TryCancelAttempt(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (AggregateException)
        {
            // Third-party cancellation callbacks cannot be allowed to stop a
            // bounded best-effort fanout worker.
            return false;
        }
    }

    private static TimeSpan MaxZero(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static ulong IncrementSaturating(ulong value) =>
        value == ulong.MaxValue ? value : value + 1;

    private readonly record struct FanoutKey(ShadowFanoutCategory Category, string GroupName);

    private sealed record PendingFanout(
        ShadowFanoutEnvelope Envelope,
        ulong Generation,
        DateTimeOffset EnqueuedAtUtc);

    private sealed record InFlightFanout(DateTimeOffset StartedAtUtc);

    private sealed record SnapshotState(ShadowFanoutEnvelope Envelope);

    private sealed record ResyncState(
        ulong RequiredThroughGeneration,
        LinkedListNode<FanoutKey> OrderNode);
}
