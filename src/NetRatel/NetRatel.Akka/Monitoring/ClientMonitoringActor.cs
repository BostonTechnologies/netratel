using System.Collections.Immutable;
using Akka.Actor;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Akka.Monitoring;

/// <summary>Mailbox-sequential decisions; only committed state is exposed. Continuity is reset on every durable reload after restart.</summary>
public sealed class ClientMonitoringActor : ReceiveActor, IWithTimers
{
    private readonly ClientKey _client;
    private readonly IMonitoringStore _store;
    private readonly IMonitoringConfigurationStore _configurations;
    private readonly IMonitoringClientDirectory _directory;
    private readonly TimeProvider _time;
    private readonly MonitoringSeriesEvaluator _evaluator;
    private readonly TimeSpan _idleTimeout;
    private bool _loaded;
    private long _generation;
    private DateTimeOffset _lastRequest;
    public ITimerScheduler Timers { get; set; } = null!;

    public ClientMonitoringActor(ClientKey client, IMonitoringStore store, IMonitoringConfigurationStore configurations,
        IMonitoringClientDirectory directory, TimeProvider time, TimeSpan idleTimeout)
    {
        _client = client; _store = store; _configurations = configurations; _directory = directory; _time = time;
        _idleTimeout = idleTimeout; _evaluator = new(time); _lastRequest = time.GetUtcNow();
        ReceiveAsync<RoutedMonitoringMessage>(async envelope =>
        {
            var parent = Context.Parent; var self = Self;
            _generation = envelope.Generation; _lastRequest = _time.GetUtcNow();
            object reply;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { reply = await ExecuteAsync(envelope.Message, timeout.Token); }
            catch (Exception exception)
            {
                reply = envelope.Message is BeginMonitoringStream or EndMonitoringStream or RecordMonitoringTelemetry or RecordMonitoringServices
                    ? new MonitoringInputResult(exception is InvalidOperationException { Message: "series_capacity_exceeded" or "monitoring_history_capacity_exceeded" or "monitoring_client_state_size_exceeded" } ?
                        MonitoringInputDisposition.CapacityExceeded : MonitoringInputDisposition.PersistenceUnavailable, 0, 0)
                    : new Status.Failure(exception);
            }
            parent.Tell(new RoutedMonitoringReply(_client, envelope.ReplyTo, reply), self);
        });
        ReceiveAsync<MonitoringRefreshTick>(async _ =>
        {
            var parent = Context.Parent; var self = Self;
            if (_time.GetUtcNow() - _lastRequest >= _idleTimeout)
            { parent.Tell(new RequestMonitoringPassivation(self, _generation), self); return; }
            if (!_loaded) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await RefreshAsync(timeout.Token); }
            catch { _loaded = false; } // A later authoritative input retries the durable reload; timer failure never acknowledges input.
        });
    }
    public static Props Props(ClientKey client, IMonitoringStore store, IMonitoringConfigurationStore configurations,
        IMonitoringClientDirectory directory, TimeProvider time, TimeSpan idleTimeout) =>
        global::Akka.Actor.Props.Create(() => new ClientMonitoringActor(client, store, configurations, directory, time, idleTimeout));
    protected override void PreStart() => Timers.StartPeriodicTimer("refresh", new MonitoringRefreshTick(), TimeSpan.FromSeconds(5));

    private async Task<object> ExecuteAsync(IMonitoringClientMessage message, CancellationToken ct)
    {
        if (message.Client != _client) throw new ArgumentException("wrong_monitoring_client");
        await EnsureLoadedAsync(ct);
        switch (message)
        {
            case BeginMonitoringStream begin:
                if (!await _store.BeginEvidenceStreamAsync(begin.Fence, ct)) return Input(MonitoringInputDisposition.StaleEvidence);
                return await ResetStreamAsync(begin.Fence, ct);
            case EndMonitoringStream end:
                if (!await _store.EndEvidenceStreamAsync(end.Fence, ct)) return Input(MonitoringInputDisposition.StaleEvidence);
                await RefreshAsync(ct); return Input(MonitoringInputDisposition.Accepted);
            case RecordMonitoringTelemetry telemetry: return await RecordAsync(telemetry.Input, null, ct);
            case RecordMonitoringServices services: return await RecordAsync(null, services.Input, ct);
            case GetClientMonitoring: return await _store.LoadClientAsync(_client, ct);
            case RefreshClientMonitoring: await RefreshAsync(ct); return true;
            case AcknowledgeMonitoringOccurrence acknowledge: return await OperateAsync(acknowledge.Command, false, ct);
            case ClearMonitoringOccurrence clear: return await OperateAsync(clear.Command, true, ct);
            default: throw new ArgumentException("unknown_monitoring_message");
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        var configuration = await _configurations.GetAsync(_client.TenantId, ct);
        foreach (var state in await _store.LoadClientAsync(_client, ct))
        {
            var rule = configuration.Rules.SingleOrDefault(rule => rule.RuleId == state.Series.RuleId);
            if (rule is null) continue;
            var write = await _store.CommitAsync(new(_evaluator.ResumeAfterRestart(state, rule), configuration.Revision), ct);
            if (write.Disposition != MonitoringStoreWriteDisposition.Stored) throw new InvalidOperationException("monitoring_reload_conflict");
        }
        _loaded = true;
    }
    private async Task<MonitoringInputResult> ResetStreamAsync(MonitoringEvidenceFence fence, CancellationToken ct)
    {
        var config = await _configurations.GetAsync(_client.TenantId, ct); var count = 0;
        foreach (var state in await _store.LoadClientAsync(_client, ct))
        {
            var rule = config.Rules.SingleOrDefault(rule => rule.RuleId == state.Series.RuleId);
            if (rule is null) continue;
            var write = await _store.CommitAsync(new(_evaluator.BeginEvidenceStream(state, rule, fence.EvidenceStreamId), config.Revision, fence), ct);
            if (write.Disposition != MonitoringStoreWriteDisposition.Stored) return Input(write.Disposition == MonitoringStoreWriteDisposition.StaleEvidence ? MonitoringInputDisposition.StaleEvidence : MonitoringInputDisposition.PersistenceUnavailable);
            count++;
        }
        return new(MonitoringInputDisposition.Accepted, count, 0);
    }

    private async Task<MonitoringInputResult> RecordAsync(MonitoringTelemetryInput? telemetry, MonitoringServicesInput? services, CancellationToken ct)
    {
        var fence = telemetry?.Fence ?? services!.Fence;
        if (await _directory.GetCurrentEvidenceAsync(_client, ct) != fence) return Input(MonitoringInputDisposition.StaleEvidence);
        var sequence = telemetry?.Snapshot.Sequence ?? services!.Services.LastAcceptedSequence;
        if (sequence == 0) return Input(MonitoringInputDisposition.StaleEvidence);
        for (var retry = 0; retry < 2; retry++)
        {
            var config = await _configurations.GetAsync(_client.TenantId, ct);
            var states = (await _store.LoadClientAsync(_client, ct)).ToDictionary(state => state.Series);
            ImmutableArray<Guid> eligible = await _directory.IsEligibleAsync(_client, ct) ? [_client.AgentId] : [];
            var applicable = config.Rules.Where(rule => IsApplicable(rule, config, eligible) &&
                (services is not null ? rule.Condition.Kind == MonitoringMetricKind.ServiceExpectedState : rule.Condition.Kind != MonitoringMetricKind.ServiceExpectedState)).ToArray();
            var work = new List<(MonitoringRuleDto Rule, MonitoringSeriesKey Key)>();
            foreach (var rule in applicable)
            {
                var keys = ResourceKeys(rule, telemetry, states.Keys.Where(key => key.RuleId == rule.RuleId));
                foreach (var resource in keys) work.Add((rule, new(_client.TenantId, rule.RuleId, _client.AgentId, resource)));
            }
            if (states.Count + work.Select(item => item.Key).Distinct().Count(key => !states.ContainsKey(key)) > MonitoringLimits.MaximumSeriesPerClient)
                return Input(MonitoringInputDisposition.CapacityExceeded);
            var count = 0; var accepted = false; var conflict = false;
            foreach (var (rule, key) in work)
            {
                var state = states.GetValueOrDefault(key) ?? _evaluator.CreateInitial(key, rule);
                var observation = telemetry is not null ? MonitoringObservationFactory.FromTelemetry(rule, key, telemetry) : MonitoringObservationFactory.FromServices(rule, key, services!);
                var result = _evaluator.Evaluate(state, rule, observation, fence.ConnectionEpoch, fence.EvidenceStreamId, config.Bypasses, groups: config.Groups);
                if (result.Disposition == MonitoringEvaluationDisposition.DuplicateOrStaleCursor) continue;
                var write = await _store.CommitAsync(new(result, config.Revision, fence), ct);
                if (write.Disposition == MonitoringStoreWriteDisposition.StaleEvidence) return new(MonitoringInputDisposition.StaleEvidence, count, sequence);
                if (write.Disposition == MonitoringStoreWriteDisposition.Conflict) { conflict = true; break; }
                accepted = true; count++;
            }
            if (!conflict) return new(accepted || work.Count == 0 ? MonitoringInputDisposition.Accepted : MonitoringInputDisposition.Duplicate, count, sequence);
        }
        return Input(MonitoringInputDisposition.PersistenceUnavailable);
    }
    private IEnumerable<string> ResourceKeys(MonitoringRuleDto rule, MonitoringTelemetryInput? telemetry, IEnumerable<MonitoringSeriesKey> existing)
    {
        if (rule.Condition.Kind == MonitoringMetricKind.CpuUsagePercent) return ["cpu"];
        if (rule.Condition.Kind == MonitoringMetricKind.ServiceExpectedState) return [MonitoringSeriesEvaluator.ServiceResourceKey(rule.Condition.ResourceName!, rule.Condition.ServicePlatform!.Value)];
        if (rule.Condition.ResourceName is { } resource) return [MonitoringSeriesEvaluator.DiskResourceKey(resource)];
        var resources = new HashSet<string>(existing.Select(key => key.ResourceKey), StringComparer.Ordinal);
        if (telemetry is not null) foreach (var disk in telemetry.Snapshot.Disks)
        { try { resources.Add(MonitoringSeriesEvaluator.DiskResourceKey(disk.Scope)); } catch (ArgumentException) { continue; } }
        return resources.Order(StringComparer.Ordinal).ToArray();
    }
    private bool IsApplicable(MonitoringRuleDto rule, MonitoringConfigurationSnapshot config, ImmutableArray<Guid> eligible) =>
        rule.Enabled && eligible.Contains(_client.AgentId) && (rule.Targets.Mode == MonitoringTargetMode.AllEligible || rule.Targets.AgentIds.Contains(_client.AgentId) ||
            config.Groups.Any(group => rule.Targets.GroupIds.Contains(group.GroupId) && group.AgentIds.Contains(_client.AgentId)));
    private async Task RefreshAsync(CancellationToken ct)
    {
        var config = await _configurations.GetAsync(_client.TenantId, ct);
        var fence = await _directory.GetCurrentEvidenceAsync(_client, ct);
        ImmutableArray<Guid> eligible = await _directory.IsEligibleAsync(_client, ct) ? [_client.AgentId] : [];
        foreach (var state in await _store.LoadClientAsync(_client, ct))
        {
            var rule = config.Rules.SingleOrDefault(rule => rule.RuleId == state.Series.RuleId);
            if (rule is null) continue;
            var result = !IsApplicable(rule, config, eligible) && state.Phase is not (MonitoringPhase.Suspended or MonitoringPhase.NotApplicable)
                ? _evaluator.SetAuthoritativeApplicability(state, rule, false)
                : _evaluator.Refresh(state, rule, config.Bypasses, fence?.EvidenceStreamId, config.Groups);
            if (result.State.StateRevision == result.ExpectedStateRevision) continue;
            var write = await _store.CommitAsync(new(result, config.Revision), ct);
            if (write.Disposition != MonitoringStoreWriteDisposition.Stored) throw new InvalidOperationException("monitoring_refresh_conflict");
        }
    }
    private Task<MonitoringStoreWriteResult> OperateAsync(MonitoringOperatorCommand command, bool clear, CancellationToken ct) =>
        // The actor orders its operations; the existing store locks also cover writers on another API process.
        _store.OperateOccurrenceAsync(command, clear, ct);
    private static MonitoringInputResult Input(MonitoringInputDisposition disposition) => new(disposition, 0, 0);
}
