using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Auth;

/// <summary>Locally validated operational recovery settings. All durations are seconds.</summary>
public sealed class OperationalRecoveryOptions
{
    public int AttemptTimeoutSeconds { get; set; } = 30;
    public int FastPhaseSeconds { get; set; } = 300;
    public int ExtendedPhaseSeconds { get; set; } = 1800;
    public int HeartbeatStabilitySeconds { get; set; } = 120;
    public int MaximumScheduledWaitSeconds { get; set; } = 600;

    public void Validate()
    {
        if (AttemptTimeoutSeconds is < 1 or > 300 ||
            FastPhaseSeconds is < 1 or > 86400 ||
            ExtendedPhaseSeconds <= FastPhaseSeconds || ExtendedPhaseSeconds > 604800 ||
            HeartbeatStabilitySeconds is < 1 or > 3600 ||
            MaximumScheduledWaitSeconds is < 30 or > 600)
            throw new ArgumentException("Client:OutageRecovery durations are invalid: attempt 1–300, fast 1–86400, extended greater than fast and at most 604800, stability 1–3600, maximum wait 30–600 seconds.");
    }
}

internal enum OperationalRetryPhase { Fast, Medium, Extended }

/// <summary>Owned only by operational presence/auth; child streams keep their own policies.</summary>
internal sealed class OperationalRecoveryPolicy
{
    private readonly TimeProvider _clock;
    private readonly Func<double> _nextRandom;
    private readonly OperationalRecoveryOptions _options;
    private readonly OperationalRecoveryStateStore? _stateStore;
    private long? _outageStarted;
    private TimeSpan _inheritedElapsed;
    private int _failures;
    private long _attempts;
    private long? _healthySince;
    private long? _lastHeartbeat;
    private long _waitStarted;
    private TimeSpan _waitDuration;
    private TimeSpan _retryAfterFloor;
    private Guid? _consumedUpdateAttempt;
    private bool _inheritedDeadline;
    private int _waiting;
    private TaskCompletionSource? _wakeHint;

    internal OperationalRecoveryPolicy(TimeProvider timeProvider, Func<double> nextRandom,
        OperationalRecoveryOptions? options = null, OperationalRecoveryStateStore? stateStore = null)
    {
        _clock = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _nextRandom = nextRandom ?? throw new ArgumentNullException(nameof(nextRandom));
        _options = options ?? new();
        _options.Validate();
        _stateStore = stateStore;
        Restore(stateStore?.Read());
    }

    internal TimeSpan OutageDuration => _outageStarted is { } started
        ? _inheritedElapsed + _clock.GetElapsedTime(started) : TimeSpan.Zero;
    internal OperationalRetryPhase Phase => OutageDuration.TotalSeconds < _options.FastPhaseSeconds
        ? OperationalRetryPhase.Fast : OutageDuration.TotalSeconds < _options.ExtendedPhaseSeconds
            ? OperationalRetryPhase.Medium : OperationalRetryPhase.Extended;
    internal long AttemptCount => _attempts;
    internal DateTimeOffset? LastAuthoritativeHeartbeatUtc { get; private set; }
    internal bool RetryAfterCapped { get; private set; }
    internal TimeSpan NextDelay => Remaining(_waitDuration);
    internal TimeSpan AttemptTimeout => TimeSpan.FromSeconds(_options.AttemptTimeoutSeconds);

    // Called before finite I/O so an attempt spanning a phase boundary counts in elapsed time.
    internal void BeginAttempt()
    {
        if (NextDelay > TimeSpan.Zero)
            throw new InvalidOperationException("An operational retry cannot start before its scheduled deadline.");
        _outageStarted ??= _clock.GetTimestamp();
        if (_attempts < long.MaxValue) _attempts++;
        _healthySince = _lastHeartbeat = null;
        _waitDuration = _retryAfterFloor = TimeSpan.Zero;
        _inheritedDeadline = false;
    }

    internal TimeSpan FailureDelay(TimeSpan? retryAfter = null, TimeSpan? minimumDelay = null)
    {
        _outageStarted ??= _clock.GetTimestamp();
        _healthySince = _lastHeartbeat = null;
        var random = Random();
        var seconds = Phase switch
        {
            OperationalRetryPhase.Fast => Math.Min(Math.Pow(2, Math.Min(_failures + 1, 5)), 30) * (0.5 + random * 0.5),
            OperationalRetryPhase.Medium => 60 + random * 60,
            _ => 300 + random * 300
        };
        _failures = Math.Min(_failures + 1, 5);
        var ceiling = _options.MaximumScheduledWaitSeconds;
        RetryAfterCapped = retryAfter?.TotalSeconds > ceiling;
        var serverFloor = Math.Clamp(retryAfter?.TotalSeconds ?? 0, 0, ceiling);
        var attentionFloor = Math.Clamp(minimumDelay?.TotalSeconds ?? 0, 0, ceiling);
        var floor = Math.Max(serverFloor, attentionFloor);
        if (floor > 0)
        {
            // A fresh draw also spreads clients when the accepted hint dominates local jitter.
            var aboveFloor = Math.Min(30, Math.Min(floor * 0.1, ceiling - floor));
            seconds = Math.Max(seconds, floor + Random() * aboveFloor);
        }
        _waitStarted = _clock.GetTimestamp();
        _waitDuration = TimeSpan.FromSeconds(Math.Min(seconds, ceiling));
        _retryAfterFloor = TimeSpan.FromSeconds(serverFloor);
        Save();
        return _waitDuration;
    }

    // The caller supplies only validated heartbeat progress from the current authoritative owner.
    // Renewal acknowledgements and admission never call this method.
    internal bool AuthoritativeHeartbeat(TimeSpan? maximumHeartbeatGap = null)
    {
        var now = _clock.GetTimestamp();
        if (_lastHeartbeat is { } previous && maximumHeartbeatGap is { } gap &&
            _clock.GetElapsedTime(previous, now) >= gap)
            _healthySince = null;
        _healthySince ??= now;
        _lastHeartbeat = now;
        LastAuthoritativeHeartbeatUtc = _clock.GetUtcNow();
        if (_outageStarted is null || _clock.GetElapsedTime(_healthySince.Value, now).TotalSeconds < _options.HeartbeatStabilitySeconds)
            return false;
        _outageStarted = null;
        _inheritedElapsed = TimeSpan.Zero;
        _failures = 0;
        _attempts = 0;
        _waitDuration = _retryAfterFloor = TimeSpan.Zero;
        _inheritedDeadline = false;
        Save();
        return true;
    }

    internal async Task WaitForNextAttemptAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _waiting, 1) != 0)
            throw new InvalidOperationException("Only the operational owner may wait for a retry.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // One monotonic deadline, no recurring ticks or replay after a suspend/time jump.
            while (NextDelay is var remaining && remaining > TimeSpan.Zero)
            {
                using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var hint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _wakeHint, hint);
                var timer = Task.Delay(remaining, _clock, timerCancellation.Token);
                if (await Task.WhenAny(timer, hint.Task).ConfigureAwait(false) == hint.Task)
                {
                    timerCancellation.Cancel();
                    try { await timer.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    { cancellationToken.ThrowIfCancellationRequested(); }
                }
                else await timer.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally { Volatile.Write(ref _wakeHint, null); Volatile.Write(ref _waiting, 0); }
    }

    // A suspend/resume or network hint can wake an eligible deadline once, never advance it.
    internal void NotifyRecoveryHint()
    {
        if (NextDelay <= TimeSpan.Zero) Volatile.Read(ref _wakeHint)?.TrySetResult();
    }

    // The caller verifies the protected updater request/result and that it targets this candidate.
    internal bool TryConsumeUpdateAttempt(Guid attemptId)
    {
        if (attemptId == Guid.Empty || !_inheritedDeadline || Phase == OperationalRetryPhase.Fast ||
            NextDelay <= TimeSpan.Zero || Remaining(_retryAfterFloor) > TimeSpan.Zero ||
            _consumedUpdateAttempt == attemptId || _stateStore is null)
            return false;
        var previous = _consumedUpdateAttempt;
        _consumedUpdateAttempt = attemptId;
        // Persist before advancing the deadline; an unwritable advisory file cannot grant repeated exceptions.
        if (!Save())
        {
            _consumedUpdateAttempt = previous;
            return false;
        }
        _waitDuration = TimeSpan.Zero;
        _inheritedDeadline = false;
        return true;
    }

    internal static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return seconds == 0 ? null : TimeSpan.FromSeconds(Math.Min(seconds, 601UL));
        // An overflowing delta is still a syntactically valid excessive hint.
        var digitsOnly = true;
        foreach (var character in trimmed)
            if (character is < '0' or > '9') { digitsOnly = false; break; }
        if (digitsOnly) return TimeSpan.FromSeconds(601);
        if (!DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) || date <= utcNow)
            return null;
        // Date hints become a duration once, before entering the monotonic scheduler.
        return date - utcNow;
    }

    private double Random()
    {
        var random = _nextRandom();
        if (!double.IsFinite(random) || random < 0 || random > 1)
            throw new InvalidOperationException("Operational retry randomness must be between zero and one.");
        return random;
    }

    private TimeSpan Remaining(TimeSpan duration) => duration <= TimeSpan.Zero
        ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Max(0, duration.Ticks - _clock.GetElapsedTime(_waitStarted).Ticks));

    private void Restore(OperationalRecoveryState? state)
    {
        if (state is null) return;
        _consumedUpdateAttempt = state.ConsumedUpdateAttemptId;
        if (!state.HasOutage) return;
        var now = _clock.GetUtcNow();
        if (state.RecordedAtUtc > now.AddMinutes(5)) return;
        var age = now > state.RecordedAtUtc ? now - state.RecordedAtUtc : TimeSpan.Zero;
        // Only the terminal phase matters after its boundary; never inherit unbounded timestamps.
        _inheritedElapsed = TimeSpan.FromSeconds(Math.Min(_options.ExtendedPhaseSeconds, state.OutageElapsedSeconds + Math.Min(age.TotalSeconds, 604800)));
        _outageStarted = _waitStarted = _clock.GetTimestamp();
        _failures = state.FailureCount;
        _attempts = state.AttemptCount;
        _waitDuration = BoundedUntil(state.NextAttemptUtc, now);
        _retryAfterFloor = BoundedUntil(state.RetryAfterUntilUtc, now);
        _waitDuration = _waitDuration > _retryAfterFloor ? _waitDuration : _retryAfterFloor;
        _inheritedDeadline = _waitDuration > TimeSpan.Zero;
    }

    private TimeSpan BoundedUntil(DateTimeOffset? deadline, DateTimeOffset now) => deadline is { } until && until > now
        ? TimeSpan.FromSeconds(Math.Min(_options.MaximumScheduledWaitSeconds, (until - now).TotalSeconds)) : TimeSpan.Zero;

    private bool Save()
    {
        if (_stateStore is null) return true;
        var now = _clock.GetUtcNow();
        return _stateStore.Write(new OperationalRecoveryState(1, now, _outageStarted is not null,
            Math.Min(OutageDuration.TotalSeconds, _options.ExtendedPhaseSeconds), _failures, _attempts,
            NextDelay > TimeSpan.Zero ? now + NextDelay : null,
            Remaining(_retryAfterFloor) is var floor && floor > TimeSpan.Zero ? now + floor : null,
            _consumedUpdateAttempt));
    }
}
