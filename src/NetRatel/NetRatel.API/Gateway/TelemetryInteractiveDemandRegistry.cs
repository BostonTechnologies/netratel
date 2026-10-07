using Microsoft.Extensions.Hosting;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

public sealed class TelemetryInteractiveOptions
{
    public const string SectionName = "TelemetryInteractive";
    public int BaselineFastIntervalMilliseconds { get; set; } = 5000;
    public int BaselineSlowIntervalSeconds { get; set; } = 30;
    public int MinimumIntervalMilliseconds { get; set; } = 1000;
    public int LeaseLifetimeSeconds { get; set; } = 45;
    public int PolicyLifetimeSeconds { get; set; } = 60;
    public int PolicyRenewalLeadSeconds { get; set; } = 20;
}

public sealed record TelemetrySamplingPolicyState(
    long Revision,
    int FastIntervalMilliseconds,
    int SlowIntervalSeconds,
    bool Interactive,
    DateTimeOffset ExpiresAtUtc,
    string Reason);

public sealed record TelemetryInteractiveLease(Guid Id, ClientKey Client, int RequestedPeriodMilliseconds);

public interface ITelemetryInteractiveDemandRegistry
{
    event Action<ClientKey, TelemetrySamplingPolicyState>? PolicyChanged;
    TelemetryInteractiveLease Acquire(ClientKey client, int requestedPeriodMilliseconds);
    bool Renew(TelemetryInteractiveLease lease);
    bool Release(TelemetryInteractiveLease lease);
    TelemetrySamplingPolicyState GetPolicy(ClientKey client);
}

/// <summary>Tracks bounded browser demand outside the Akka telemetry authority.</summary>
public sealed class TelemetryInteractiveDemandRegistry(
    TimeProvider timeProvider,
    IConfiguration configuration) : ITelemetryInteractiveDemandRegistry, IHostedService, IAsyncDisposable
{
    private readonly TelemetryInteractiveOptions _options = configuration.GetSection(TelemetryInteractiveOptions.SectionName).Get<TelemetryInteractiveOptions>() ?? new();
    private readonly Dictionary<ClientKey, DemandState> _states = [];
    private readonly object _gate = new();
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _stopping;
    private Task? _sweep;
    private int _disposeStarted;

    public event Action<ClientKey, TelemetrySamplingPolicyState>? PolicyChanged;

    public TelemetryInteractiveLease Acquire(ClientKey client, int requestedPeriodMilliseconds)
    {
        var lease = new TelemetryInteractiveLease(Guid.NewGuid(), client, Clamp(requestedPeriodMilliseconds));
        TelemetrySamplingPolicyState? changed;
        lock (_gate)
        {
            var state = GetOrCreate(client);
            var now = timeProvider.GetUtcNow();
            Prune(state, now);
            state.Leases[lease.Id] = new LeaseState(lease.RequestedPeriodMilliseconds, LeaseExpiresAt());
            changed = Recalculate(state, now);
        }
        Publish(client, changed);
        return lease;
    }

    public bool Renew(TelemetryInteractiveLease lease)
    {
        TelemetrySamplingPolicyState? changed = null;
        var renewed = false;
        lock (_gate)
        {
            if (!_states.TryGetValue(lease.Client, out var state)) return false;
            var now = timeProvider.GetUtcNow();
            Prune(state, now);
            if (state.Leases.TryGetValue(lease.Id, out var current))
            {
                state.Leases[lease.Id] = current with { ExpiresAtUtc = LeaseExpiresAt() };
                renewed = true;
            }
            changed = Recalculate(state, now);
        }
        Publish(lease.Client, changed);
        return renewed;
    }

    public bool Release(TelemetryInteractiveLease lease)
    {
        TelemetrySamplingPolicyState? changed = null;
        var removed = false;
        lock (_gate)
        {
            if (_states.TryGetValue(lease.Client, out var state))
            {
                var now = timeProvider.GetUtcNow();
                Prune(state, now);
                removed = state.Leases.Remove(lease.Id);
                changed = Recalculate(state, now);
            }
        }
        Publish(lease.Client, changed);
        return removed;
    }

    public TelemetrySamplingPolicyState GetPolicy(ClientKey client)
    {
        TelemetrySamplingPolicyState? changed;
        TelemetrySamplingPolicyState policy;
        lock (_gate)
        {
            var state = GetOrCreate(client);
            var now = timeProvider.GetUtcNow();
            Prune(state, now);
            changed = Recalculate(state, now);
            policy = state.Policy;
        }
        Publish(client, changed);
        return policy;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
            if (_stopping is not null) return Task.CompletedTask;
            _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _timer = new PeriodicTimer(TimeSpan.FromSeconds(5), timeProvider);
            _sweep = SweepAsync(_timer, _stopping.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? sweep;
        lock (_gate)
        {
            _stopping?.Cancel();
            sweep = _sweep;
        }
        if (sweep is not null) await sweep.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        CancellationTokenSource? stopping;
        Task? sweep;
        lock (_gate)
        {
            stopping = _stopping;
            stopping?.Cancel();
            _timer?.Dispose();
            sweep = _sweep;
            _stopping = null;
            _timer = null;
        }
        try
        {
            if (sweep is not null) await sweep.ConfigureAwait(false);
        }
        finally
        {
            stopping?.Dispose();
        }
    }

    private async Task SweepAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Sweep();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    internal void Sweep()
    {
        List<(ClientKey Client, TelemetrySamplingPolicyState Policy)>? changes = null;
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var (client, state) in _states)
            {
                Prune(state, now);
                if (Recalculate(state, now) is { } policy) (changes ??= []).Add((client, policy));
            }
        }
        if (changes is not null) foreach (var (client, policy) in changes) Publish(client, policy);
    }

    private DemandState GetOrCreate(ClientKey client) => _states.TryGetValue(client, out var state)
        ? state
        : _states[client] = new DemandState(CreatePolicy(0, false, _options.BaselineFastIntervalMilliseconds, "baseline"));

    private TelemetrySamplingPolicyState? Recalculate(DemandState state, DateTimeOffset now)
    {
        var fast = state.Leases.Count == 0 ? _options.BaselineFastIntervalMilliseconds : state.Leases.Values.Min(static lease => lease.PeriodMilliseconds);
        var interactive = state.Leases.Count > 0;
        var renewBefore = now.AddSeconds(Math.Clamp(_options.PolicyRenewalLeadSeconds, 5, Math.Max(5, _options.PolicyLifetimeSeconds - 1)));
        if (state.Policy.Interactive == interactive && state.Policy.FastIntervalMilliseconds == fast &&
            (!interactive || state.Policy.ExpiresAtUtc > renewBefore)) return null;
        state.Policy = CreatePolicy(state.Policy.Revision + 1, interactive, fast, interactive ? "interactive-viewer" : "baseline");
        return state.Policy;
    }

    private TelemetrySamplingPolicyState CreatePolicy(long revision, bool interactive, int fastIntervalMilliseconds, string reason) => new(
        revision,
        fastIntervalMilliseconds,
        Math.Clamp(_options.BaselineSlowIntervalSeconds, 5, 300),
        interactive,
        timeProvider.GetUtcNow().AddSeconds(Math.Clamp(_options.PolicyLifetimeSeconds, 30, 300)),
        reason);

    private DateTimeOffset LeaseExpiresAt() => timeProvider.GetUtcNow().AddSeconds(Math.Clamp(_options.LeaseLifetimeSeconds, 30, 120));
    private int Clamp(int value) => Math.Clamp(value <= 0 ? _options.MinimumIntervalMilliseconds : value, Math.Max(1000, _options.MinimumIntervalMilliseconds), 60_000);
    private static void Prune(DemandState state, DateTimeOffset now) => state.Leases.RemoveWhere(static (entry, stamp) => entry.Value.ExpiresAtUtc <= stamp, now);
    private void Publish(ClientKey client, TelemetrySamplingPolicyState? policy) { if (policy is not null) PolicyChanged?.Invoke(client, policy); }

    private sealed class DemandState(TelemetrySamplingPolicyState policy)
    {
        public Dictionary<Guid, LeaseState> Leases { get; } = [];
        public TelemetrySamplingPolicyState Policy { get; set; } = policy;
    }
    private sealed record LeaseState(int PeriodMilliseconds, DateTimeOffset ExpiresAtUtc);
}

internal static class TelemetryDemandDictionaryExtensions
{
    public static void RemoveWhere<TKey, TValue, TState>(this Dictionary<TKey, TValue> dictionary, Func<KeyValuePair<TKey, TValue>, TState, bool> predicate, TState state) where TKey : notnull
    {
        foreach (var key in dictionary.Where(entry => predicate(entry, state)).Select(static entry => entry.Key).ToArray()) dictionary.Remove(key);
    }
}
