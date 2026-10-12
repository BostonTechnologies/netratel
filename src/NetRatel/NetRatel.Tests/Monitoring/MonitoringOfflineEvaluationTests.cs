using System.Collections.Immutable;
using System.Text.Json;
using NetRatel.Application.Flows;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;
using Xunit;

namespace NetRatel.Tests.Monitoring;

public sealed class MonitoringOfflineEvaluationTests
{
    [Fact]
    public void Site_outage_uses_one_occurrence_through_membership_changes_and_durable_reload()
    {
        var scenario = new Scenario();
        var first = scenario.Client("Dispatch"); var second = scenario.Client("Database"); var late = scenario.Client("Storage");
        scenario.Observe(1, first, second);
        scenario.Observe(10, first, second);
        Assert.Equal(MonitoringPhase.Pending, scenario.State.Phase);
        scenario.Observe(11, first, second);
        var occurrence = scenario.State.Occurrence!.OccurrenceId;
        Assert.Equal(1, scenario.Raises);
        Assert.Single(scenario.Outbox);
        Assert.Equal(2, scenario.State.LatestEvidence!.OfflineSummary!.OfflineCount);

        scenario.State = JsonSerializer.Deserialize<MonitoringSeriesState>(JsonSerializer.Serialize(scenario.State))!;
        scenario.Observe(16, first with { Offline = false }, second, late);
        Assert.Equal(occurrence, scenario.State.Occurrence!.OccurrenceId);
        Assert.Equal(new[] { second.AgentId, late.AgentId }.Order(), scenario.State.OfflineAgentIds);
        scenario.Observe(21, first with { Offline = false }, second with { Offline = false }, late);
        Assert.Equal(1, scenario.Raises);
        Assert.Single(scenario.Outbox);
        Assert.Equal(1, scenario.State.LatestEvidence!.OfflineSummary!.OfflineCount);

        var online = new[] { first, second, late }.Select(client => client with { Offline = false }).ToArray();
        scenario.Observe(26, online);
        Assert.Equal(MonitoringPhase.Recovering, scenario.State.Phase);
        scenario.Observe(31, online);
        Assert.Equal(MonitoringPhase.Resolved, scenario.State.Phase);
        scenario.Observe(32, first);
        scenario.Observe(42, first);
        Assert.Equal(2, scenario.Raises);
        Assert.NotEqual(occurrence, scenario.State.Occurrence!.OccurrenceId);

        var envelope = MonitoringCanonicalFlowEvent.Create(scenario.Outbox[0]);
        Assert.NotNull(envelope);
        Assert.Equal("ClientOffline", envelope.Data.Metric);
        Assert.Equal(Guid.Empty, envelope.Data.AgentId);
        Assert.Equal(2, envelope.Data.OfflineSummary!.OfflineCount);
        Assert.Equal(envelope, JsonSerializer.Deserialize<FlowEventEnvelope>(JsonSerializer.Serialize(envelope)));
    }

    [Fact]
    public void Group_and_direct_targets_are_deduplicated_and_only_eligible_input_members_are_counted()
    {
        var scenario = new Scenario();
        var first = scenario.Client("Dispatch"); var second = scenario.Client("Database"); var unrelated = scenario.Client("Other site");
        var group = new MonitoringGroupDto(7, Guid.NewGuid(), 1, "Factory", [first.AgentId, second.AgentId, Guid.NewGuid()]);
        var rule = scenario.Rule with { Targets = new(MonitoringTargetMode.Selected, [first.AgentId], [group.GroupId]) };
        var observation = MonitoringOfflineEvaluation.Observe(rule, [group], [first, second, unrelated]);
        Assert.Equal(2, observation.Summary.OfflineCount);
        Assert.Equal(2, observation.Summary.MonitoredCount);
        Assert.DoesNotContain(unrelated.AgentId, observation.AgentIds);
        Assert.Contains("Dispatch", observation.Summary.ClientNames);
        Assert.Contains("Database", observation.Summary.ClientNames);
    }

    [Theory]
    [InlineData(10, 8, false)]
    [InlineData(11, 8, true)]
    [InlineData(10, 128, true)]
    public void Outgoing_snapshot_caps_both_client_count_and_text_length(int count, int nameLength, bool truncated)
    {
        var scenario = new Scenario();
        var clients = Enumerable.Range(0, count).Select(index => scenario.Client(new string((char)('A' + index), nameLength))).ToArray();
        var observed = MonitoringOfflineEvaluation.Observe(scenario.Rule, [], clients);
        Assert.Equal(count, observed.AgentIds.Length);
        Assert.Equal(count, observed.Summary.OfflineCount);
        Assert.Equal(truncated, observed.Summary.Truncated);
        Assert.True(MonitoringOfflinePresentation.IsValid(observed.Summary));
        if (truncated) Assert.Empty(observed.Summary.ClientNames);
        else Assert.True(observed.Summary.ClientNames.Length <= MonitoringOfflinePresentation.MaximumClientNamesLength);
    }

    [Fact]
    public void Group_bypass_keeps_the_episode_and_dispatches_once_when_the_overlay_expires()
    {
        var scenario = new Scenario(); var client = scenario.Client("Factory");
        var group = new MonitoringGroupDto(7, Guid.NewGuid(), 1, "Factory", [client.AgentId]);
        scenario.Groups = [group];
        scenario.Bypasses = [new(Guid.NewGuid(), 7, scenario.Rule.RuleId, null, null, Guid.NewGuid(), "Maintenance",
            scenario.Clock.GetUtcNow(), scenario.Clock.GetUtcNow().AddSeconds(20), group.GroupId)];
        scenario.Observe(1, client); scenario.Observe(11, client);
        Assert.True(scenario.State.Suppressed);
        Assert.Equal(1, scenario.State.LatestEvidence!.OfflineSummary!.SuppressedCount);
        var occurrence = scenario.State.Occurrence!.OccurrenceId;
        Assert.Empty(scenario.Outbox);
        scenario.Clock.Set(21);
        var refresh = scenario.Evaluator.Refresh(scenario.State, scenario.Rule, scenario.Bypasses, scenario.Rule.RuleId, scenario.Groups);
        Assert.Empty(refresh.Outbox);
        scenario.State = refresh.State;
        scenario.Observe(21, client);
        Assert.False(scenario.State.Suppressed);
        Assert.Equal(occurrence, scenario.State.Occurrence!.OccurrenceId);
        Assert.Single(scenario.Outbox);
        Assert.Equal(1, scenario.State.Occurrence.RaisedEvidence.OfflineSummary!.SuppressedCount);
        var restoredIntent = JsonSerializer.Deserialize<MonitoringOutboxIntent>(JsonSerializer.Serialize(scenario.Outbox[0]))!;
        var envelope = MonitoringCanonicalFlowEvent.Create(restoredIntent);
        Assert.NotNull(envelope);
        Assert.Equal(0, envelope.Data.OfflineSummary!.SuppressedCount);
        Assert.Contains("Factory", envelope.Data.OfflineSummary.ClientNames);
        Assert.Equal(scenario.Clock.GetUtcNow(), envelope.Data.ObservedAtUtc);
        scenario.Observe(26, client);
        Assert.Single(scenario.Outbox);
    }

    [Fact]
    public void Partial_client_bypass_keeps_full_dashboard_membership_but_only_lists_actionable_clients()
    {
        var scenario = new Scenario(); var bypassed = scenario.Client("Maintenance"); var actionable = scenario.Client("Database");
        scenario.Bypasses = [new(Guid.NewGuid(), 7, scenario.Rule.RuleId, bypassed.AgentId, null, Guid.NewGuid(), "Maintenance",
            scenario.Clock.GetUtcNow(), scenario.Clock.GetUtcNow().AddSeconds(20))];
        scenario.Observe(1, bypassed, actionable); scenario.Observe(11, bypassed, actionable);
        Assert.False(scenario.State.Suppressed);
        Assert.Equal(2, scenario.State.OfflineAgentIds.Length);
        Assert.Equal(2, scenario.State.LatestEvidence!.OfflineSummary!.OfflineCount);
        var summary = MonitoringCanonicalFlowEvent.Create(Assert.Single(scenario.Outbox))!.Data.OfflineSummary!;
        Assert.Equal(1, summary.SuppressedCount);
        Assert.Equal("1 client is offline: Database.", summary.Summary);
        Assert.DoesNotContain("Maintenance", summary.ClientNames);
        scenario.Observe(21, bypassed, actionable);
        Assert.Equal(1, scenario.Raises);
        Assert.Single(scenario.Outbox);
    }

    [Fact]
    public void Clear_rearms_only_after_a_new_full_grace_window_and_old_defaults_keep_their_fingerprint()
    {
        var scenario = new Scenario(); var client = scenario.Client("Factory");
        scenario.Observe(1, client); scenario.Observe(11, client);
        scenario.State = scenario.Evaluator.Clear(scenario.State, scenario.Rule, Guid.NewGuid(), "Investigated").State;
        scenario.Observe(12, client); scenario.Observe(21, client);
        Assert.Equal(1, scenario.Raises);
        scenario.Observe(22, client);
        Assert.Equal(2, scenario.Raises);
        var legacy = scenario.Rule with { AlertIfOffline = false, OfflineGraceSeconds = null };
        Assert.Equal(MonitoringContractValidator.EvaluationFingerprint(legacy),
            MonitoringContractValidator.EvaluationFingerprint(legacy with { OfflineGraceSeconds = 60 }));
        Assert.DoesNotContain("AlertIfOffline", JsonSerializer.Serialize(legacy));
        Assert.DoesNotContain("OfflineGraceSeconds", JsonSerializer.Serialize(legacy));
        Assert.DoesNotContain("OfflineSummary", JsonSerializer.Serialize(scenario.Outbox[0].PinnedEvidence with { OfflineSummary = null }));
        Assert.DoesNotContain("OfflineSummary", JsonSerializer.Serialize(scenario.Outbox[0] with
            { OfflineSummary = null, PinnedEvidence = scenario.Outbox[0].PinnedEvidence with { OfflineSummary = null } }));
    }

    private sealed class Scenario
    {
        public TestClock Clock { get; } = new();
        public MonitoringSeriesEvaluator Evaluator { get; }
        public MonitoringRuleDto Rule { get; }
        public MonitoringSeriesState State { get; set; }
        public IReadOnlyList<MonitoringBypassDto> Bypasses { get; set; } = [];
        public IReadOnlyList<MonitoringGroupDto> Groups { get; set; } = [];
        public int Raises { get; private set; }
        public List<MonitoringOutboxIntent> Outbox { get; } = [];

        public Scenario()
        {
            Evaluator = new(Clock);
            // Offline presence must work independently of disk collection stamps and a very short metric freshness budget.
            Rule = new(7, Guid.NewGuid(), 1, 1, "Factory monitoring", true, MonitoringSeverity.Critical,
                new(MonitoringTargetMode.AllEligible, [], []), new(MonitoringMetricKind.DiskFreePercent, MonitoringNumericUnit.Percent, 10, 20, null, null, []),
                TimeSpan.FromHours(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), Guid.NewGuid(), "operator",
                AlertIfOffline: true, OfflineGraceSeconds: 10);
            State = Evaluator.CreateInitial(MonitoringOfflinePresentation.Series(7, Rule.RuleId), Rule);
        }
        public MonitoringOfflineClient Client(string name) => new(Guid.NewGuid(), name, true);
        public void Observe(int seconds, params MonitoringOfflineClient[] clients)
        {
            Clock.Set(seconds);
            var result = MonitoringOfflineEvaluation.Evaluate(Evaluator, State, Rule,
                MonitoringOfflineEvaluation.Observe(Rule, Groups, clients, Bypasses, Clock.GetUtcNow()), Clock.GetUtcNow(), Bypasses, Groups);
            State = result.State; Raises += result.Events.Count(item => item.Kind == MonitoringEventKind.AlertRaised); Outbox.AddRange(result.Outbox);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly DateTimeOffset _origin = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        private int _seconds;
        public void Set(int seconds) => _seconds = seconds;
        public override DateTimeOffset GetUtcNow() => _origin.AddSeconds(_seconds);
    }
}
