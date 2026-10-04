namespace NetRatel.Tests.Client;

/// <summary>Timer and elapsed-time control shared by presence lifecycle regressions.</summary>
internal sealed class GatewayPresenceTestClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _utcNow; }
    public override long GetTimestamp() { lock (_gate) return _timestamp; }
    internal int ActiveTimerCount { get { lock (_gate) return _timers.Count; } }
    internal void AdjustUtc(TimeSpan adjustment) { lock (_gate) _utcNow += adjustment; }
    internal bool HasTimer(TimeSpan delay)
    {
        lock (_gate) return _timers.Any(timer => timer.DueAt == _timestamp + delay.Ticks);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
    }

    internal void Advance(TimeSpan elapsed)
    {
        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_gate)
        {
            _utcNow += elapsed;
            _timestamp += elapsed.Ticks;
            foreach (var timer in _timers.ToArray())
            {
                if (timer.DueAt is { } dueAt && dueAt <= _timestamp)
                {
                    callbacks.Add((timer.Callback, timer.State));
                    timer.DueAt = timer.Period > TimeSpan.Zero ? _timestamp + timer.Period.Ticks : null;
                }
            }
        }
        foreach (var callback in callbacks) callback.Callback(callback.State);
    }

    private sealed class ManualTimer(GatewayPresenceTestClock owner, TimerCallback callback, object? state) : ITimer
    {
        internal TimerCallback Callback { get; } = callback;
        internal object? State { get; } = state;
        internal long? DueAt { get; set; }
        internal TimeSpan Period { get; private set; }
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed) return false;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._timestamp + dueTime.Ticks;
                Period = period;
                return true;
            }
        }
        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                owner._timers.Remove(this);
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
