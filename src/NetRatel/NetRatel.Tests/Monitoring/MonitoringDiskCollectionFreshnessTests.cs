using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;
using Xunit;

namespace NetRatel.Tests.Monitoring;

/// <summary>Source-only proposal. Collection clocks and admitted transport cursors are deliberately independent.</summary>
public sealed class MonitoringDiskCollectionFreshnessTests
{
    [Fact]
    public void Fast_frames_copy_one_collection_without_advancing_hold_or_refreshing_evidence_age()
    {
        var s = new Scenario();
        var first = s.Observe(0, 0, 5, Id(9));
        foreach (var received in new[] { 5, 10, 15, 20, 25, 30 }) s.Copy(first, received);
        s.State.Phase.Should().Be(MonitoringPhase.Pending);
        Assert.Equal(s.Clock.At(0), s.State.WindowStartedAtUtc);
        Assert.Equal(s.Clock.At(0), s.State.WindowStartedObservedAtUtc);
        Assert.Equal(s.Clock.At(0), s.State.PreviousQualifyingReceivedAtUtc);
        Assert.Equal(s.Clock.At(0), s.State.LatestEvidence!.ObservedAtUtc);
        Assert.Equal(s.Clock.At(0), s.State.LatestEvidence.ReceivedAtUtc);
        Assert.Equal(first.Cursor, s.State.LatestEvidence.Cursor);
        Assert.True(s.State.Cursor!.Sequence > first.Cursor.Sequence);
        Assert.Empty(s.Events);
        Assert.Empty(s.Outbox);

        // IDs sort opposite to collection time; genuine new evidence completes the hold.
        s.Observe(30, 31, 5, Id(1));
        Assert.Single(s.Events, item => item.Kind == MonitoringEventKind.AlertRaised);
        Assert.Single(s.Outbox);
    }

    [Fact]
    public void Recovery_needs_new_collections_and_a_later_real_breach_gets_one_new_occurrence()
    {
        var s = new Scenario();
        s.Observe(0, 0, 5, Id(1)); s.Observe(30, 30, 5, Id(2));
        var original = s.State.Occurrence!.OccurrenceId;
        var recovery = s.Observe(35, 36, 20, Id(3));
        foreach (var received in new[] { 40, 50, 65 }) s.Copy(recovery, received);
        Assert.Equal(MonitoringPhase.Recovering, s.State.Phase);
        Assert.Equal(s.Clock.At(35), s.State.WindowStartedObservedAtUtc);
        Assert.DoesNotContain(s.Events, item => item.Kind == MonitoringEventKind.AlertResolved);
        s.Observe(65, 66, 20, Id(4));
        Assert.Single(s.Events, item => item.Kind == MonitoringEventKind.AlertResolved);
        Assert.Equal(original, s.State.Occurrence.OccurrenceId);
        s.Observe(70, 71, 5, Id(5)); s.Observe(100, 101, 5, Id(6));
        Assert.Equal(2, s.Events.Count(item => item.Kind == MonitoringEventKind.AlertRaised));
        Assert.Equal(2, s.Outbox.Count);
        Assert.NotEqual(original, s.State.Occurrence.OccurrenceId);
    }

    [Fact]
    public void Copies_become_unknown_at_the_original_collection_deadline_and_cannot_rearm_the_hold()
    {
        var s = new Scenario(); var first = s.Observe(0, 0, 5, Id(1));
        foreach (var received in new[] { 15, 30, 44 }) s.Copy(first, received);
        Assert.Equal(MonitoringEvidenceQuality.Fresh, s.State.EvidenceQuality);
        s.Copy(first, 46);
        Assert.Equal(MonitoringEvidenceQuality.Unknown, s.State.EvidenceQuality);
        Assert.Equal("stale", s.State.LatestEvidence!.UnknownReason);
        Assert.Null(s.State.WindowStartedAtUtc);
        Assert.Null(s.State.WindowStartedObservedAtUtc);
        Assert.Equal(first.DiskCollection, s.State.LastDiskCollection);
        s.Copy(first, 47);
        Assert.Equal(MonitoringEvidenceQuality.Unknown, s.State.EvidenceQuality);
        Assert.Empty(s.Events);
        s.Observe(50, 50, 5, Id(2));
        Assert.Equal(s.Clock.At(50), s.State.WindowStartedObservedAtUtc);
        s.Observe(80, 80, 5, Id(3));
        Assert.Single(s.Events);
    }

    [Fact]
    public void Missing_projection_preserves_the_marker_and_cannot_make_a_cached_collection_fresh_again()
    {
        var s = new Scenario(); var first = s.Observe(0, 0, 5, Id(1));
        s.Missing(20);
        Assert.Equal(MonitoringEvidenceQuality.Unknown, s.State.EvidenceQuality);
        Assert.Equal(first.DiskCollection, s.State.LastDiskCollection);
        s.Copy(first, 21);
        Assert.Equal(MonitoringEvidenceQuality.Unknown, s.State.EvidenceQuality);
        Assert.Null(s.State.WindowStartedAtUtc);
        // The real sample is newer than the durable collection marker, even though
        // its sample time precedes the unrelated fast frame carrying a missing disk.
        s.Observe(15, 25, 5, Id(2));
        Assert.Equal(MonitoringEvidenceQuality.Fresh, s.State.EvidenceQuality);
        Assert.Equal(s.Clock.At(15), s.State.WindowStartedObservedAtUtc);
        s.Observe(45, 55, 5, Id(3));
        Assert.Single(s.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restart_and_new_stream_keep_the_same_occurrence_but_require_new_physical_evidence(bool newStream)
    {
        var s = new Scenario();
        s.Observe(0, 0, 5, Id(1)); var last = s.Observe(30, 30, 5, Id(2));
        var occurrence = s.State.Occurrence!.OccurrenceId;
        var raisedEvent = s.State.Occurrence.RaisedEventId;
        s.Clock.Set(35);
        if (newStream)
        {
            s.StreamId = Id(100);
            s.Apply(s.Evaluator.BeginEvidenceStream(s.State, s.Rule, s.StreamId));
        }
        else s.Apply(s.Evaluator.ResumeAfterRestart(s.State, s.Rule));
        var reloaded = JsonSerializer.Deserialize<MonitoringSeriesState>(JsonSerializer.Serialize(s.State))!;
        Assert.Equal(last.DiskCollection, reloaded.LastDiskCollection);
        s.State = reloaded;
        s.Copy(last, 40);
        Assert.Equal(MonitoringEvidenceQuality.Unknown, s.State.EvidenceQuality);
        Assert.False(s.Evaluator.CanDispatch(s.State, s.Rule, s.Outbox.Single(), s.StreamId, []));
        Assert.Equal(occurrence, s.State.Occurrence!.OccurrenceId);
        Assert.Equal(raisedEvent, s.State.Occurrence.RaisedEventId);
        s.Observe(45, 45, 5, Id(3));
        Assert.True(s.Evaluator.CanDispatch(s.State, s.Rule, s.Outbox.Single(), s.StreamId, []));
        Assert.Equal(occurrence, s.State.Occurrence.OccurrenceId);
        Assert.Single(s.Events);
        Assert.Single(s.Outbox);
    }

    [Fact]
    public void Manual_clear_preserves_the_marker_and_only_a_new_physical_window_can_raise_again()
    {
        var s = new Scenario();
        s.Observe(0, 0, 5, Id(1)); var last = s.Observe(30, 30, 5, Id(2));
        var occurrence = s.State.Occurrence!.OccurrenceId;
        s.Clock.Set(35);
        s.Apply(s.Evaluator.Clear(s.State, s.Rule, Id(100), "validated maintenance"));
        Assert.Equal(last.DiskCollection, s.State.LastDiskCollection);
        s.Copy(last, 40);
        Assert.Equal(MonitoringEvidenceQuality.Unknown, s.State.EvidenceQuality);
        Assert.Null(s.State.WindowStartedAtUtc);
        Assert.Equal(MonitoringClosureDisposition.ManuallyCleared, s.State.Occurrence!.ClosureDisposition);
        s.Observe(45, 45, 5, Id(3));
        Assert.Equal(MonitoringPhase.Pending, s.State.Phase);
        s.Observe(75, 75, 5, Id(4));
        Assert.Equal(2, s.Events.Count(item => item.Kind == MonitoringEventKind.AlertRaised));
        Assert.NotEqual(occurrence, s.State.Occurrence.OccurrenceId);
    }

    [Fact]
    public void Changed_body_and_out_of_order_collection_stay_unknown_without_regressing_the_durable_marker()
    {
        var s = new Scenario(); var first = s.Observe(0, 0, 5, Id(9));
        s.Clock.Set(10);
        s.Apply(s.Evaluator.Evaluate(s.State, s.Rule,
            first with { Cursor = new(s.Epoch, ++s.Sequence), ReceivedAtUtc = s.Clock.At(10),
                DiskCollection = first.DiskCollection! with { PayloadFingerprint = new string('b', 64) } },
            s.Epoch, s.StreamId, []));
        Assert.Equal("disk_collection_changed", s.State.LatestEvidence!.UnknownReason);
        Assert.Equal(first.DiskCollection, s.State.LastDiskCollection);
        s.Copy(first, 15);
        Assert.Equal(MonitoringEvidenceQuality.Unknown, s.State.EvidenceQuality);
        var next = s.Observe(30, 30, 5, Id(1));
        s.Copy(first, 35);
        Assert.Equal("reordered_disk_collection", s.State.LatestEvidence!.UnknownReason);
        Assert.Equal(next.DiskCollection, s.State.LastDiskCollection);
        s.Copy(next, 40);
        Assert.Equal(MonitoringEvidenceQuality.Unknown, s.State.EvidenceQuality);
        Assert.Empty(s.Events);
        s.Observe(60, 60, 5, Id(2)); s.Observe(90, 90, 5, Id(3));
        Assert.Single(s.Events);
    }

    [Theory]
    [InlineData(0, 0, 1, 30, 31, 31)]
    [InlineData(0, 30, 30, 31, 31, 60)]
    public void Both_collection_span_and_first_receipt_span_must_meet_the_hold(
        int firstCollected, int firstReceived, int secondCollected, int secondReceived, int finalCollected, int finalReceived)
    {
        var s = new Scenario();
        s.Observe(firstCollected, firstReceived, 5, Id(1));
        s.Observe(secondCollected, secondReceived, 5, Id(2));
        Assert.Empty(s.Events);
        s.Observe(finalCollected, finalReceived, 5, Id(3));
        Assert.Single(s.Events);
    }

    [Fact]
    public void A_valid_copy_can_release_a_previously_raised_suppressed_event_without_completing_a_hold()
    {
        var s = new Scenario();
        s.Bypasses = [new(Id(100), s.Rule.TenantId, s.Rule.RuleId, s.State.Series.AgentId, s.State.Series.ResourceKey,
            Id(101), "maintenance", s.Clock.At(-1), s.Clock.At(40))];
        s.Observe(0, 0, 5, Id(1)); var last = s.Observe(30, 30, 5, Id(2));
        Assert.Single(s.Events);
        Assert.Empty(s.Outbox);
        s.Copy(last, 45);
        Assert.Single(s.Events);
        Assert.Single(s.Outbox);
        Assert.Equal(s.Clock.At(30), s.State.LatestEvidence!.ReceivedAtUtc);
        Assert.Equal(last.DiskCollection, s.Outbox.Single().PinnedEvidence.DiskCollection);
    }

    [Fact]
    public void A_partial_input_with_a_matching_marker_still_interrupts_continuity()
    {
        var s = new Scenario(); var first = s.Observe(0, 0, 5, Id(1));
        s.Clock.Set(10);
        s.Apply(s.Evaluator.Evaluate(s.State, s.Rule,
            first with { Cursor = new(s.Epoch, ++s.Sequence), ReceivedAtUtc = s.Clock.At(10), Complete = false },
            s.Epoch, s.StreamId, []));
        Assert.Equal("partial", s.State.LatestEvidence!.UnknownReason);
        Assert.Null(s.State.WindowStartedAtUtc);
        Assert.Equal(first.DiskCollection, s.State.LastDiskCollection);
        s.Copy(first, 20);
        Assert.Equal(MonitoringEvidenceQuality.Unknown, s.State.EvidenceQuality);
        Assert.Empty(s.Events);
    }

    [Fact]
    public void Receipt_clock_regression_interrupts_cached_evidence_and_future_receipts_do_not_poison_the_high_water()
    {
        var s = new Scenario(); var first = s.Observe(0, 0, 5, Id(1));
        s.Copy(first, 10);
        s.Clock.Set(15);
        s.Apply(s.Evaluator.Evaluate(s.State, s.Rule,
            first with { Cursor = new(s.Epoch, ++s.Sequence), ReceivedAtUtc = s.Clock.At(5) }, s.Epoch, s.StreamId, []));
        Assert.Equal("reordered_clock", s.State.LatestEvidence!.UnknownReason);
        Assert.Equal(s.Clock.At(10), s.State.LastDiskTransportReceivedAtUtc);
        Assert.Null(s.State.WindowStartedAtUtc);
        s.Apply(s.Evaluator.Evaluate(s.State, s.Rule,
            first with { Cursor = new(s.Epoch, ++s.Sequence), ReceivedAtUtc = s.Clock.At(20) }, s.Epoch, s.StreamId, []));
        Assert.Equal("future_clock", s.State.LatestEvidence!.UnknownReason);
        Assert.Equal(s.Clock.At(10), s.State.LastDiskTransportReceivedAtUtc);
        s.Observe(15, 16, 5, Id(2));
        Assert.Equal(MonitoringEvidenceQuality.Fresh, s.State.EvidenceQuality);
        Assert.Equal(s.Clock.At(16), s.State.LastDiskTransportReceivedAtUtc);
        Assert.Empty(s.Events);
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);

    private sealed class Clock : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _now = Start.AddSeconds(-1);
        public override DateTimeOffset GetUtcNow() => _now;
        public DateTimeOffset At(int seconds) => Start.AddSeconds(seconds);
        public void Set(int seconds) => _now = At(seconds);
    }

    private sealed class Scenario
    {
        public Clock Clock { get; } = new();
        public MonitoringSeriesEvaluator Evaluator { get; }
        public MonitoringRuleDto Rule { get; }
        public MonitoringSeriesState State { get; set; }
        public long Epoch { get; } = 1;
        public Guid StreamId { get; set; } = Id(200);
        public ulong Sequence { get; set; }
        public IReadOnlyList<MonitoringBypassDto> Bypasses { get; set; } = [];
        public List<MonitoringEventIntent> Events { get; } = [];
        public List<MonitoringOutboxIntent> Outbox { get; } = [];

        public Scenario()
        {
            Evaluator = new(Clock);
            Rule = new(41, Id(201), 1, 1, "disk", true, MonitoringSeverity.Warning,
                new(MonitoringTargetMode.AllEligible, [], []),
                new(MonitoringMetricKind.DiskFreePercent, MonitoringNumericUnit.Percent, 10, 15, "/", null, []),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45),
                Id(202), "test-operator", "test-credential");
            State = Evaluator.CreateInitial(new(41, Rule.RuleId, Id(203), "disk:/"), Rule);
        }

        public MonitoringObservationDto Observe(int collected, int received, double value, Guid id)
        {
            Clock.Set(received);
            var collectedAt = Clock.At(collected);
            // Opaque test payload digest. Factory canonicalization has its own tests;
            // this suite exercises immutable marker equality and evaluator decisions.
            var body = Encoding.UTF8.GetBytes($"{id:D}:{collectedAt:O}:{BitConverter.DoubleToInt64Bits(value)}");
            var stamp = new MonitoringDiskCollectionStamp(id, collectedAt, Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
            var observation = new MonitoringObservationDto(State.Series, new(Epoch, ++Sequence), StreamId,
                collectedAt, Clock.At(received), true, true, value, DiskCollection: stamp);
            Apply(Evaluator.Evaluate(State, Rule, observation, Epoch, StreamId, Bypasses));
            return observation;
        }

        public void Copy(MonitoringObservationDto original, int received)
        {
            Clock.Set(received);
            var observation = original with { Cursor = new(Epoch, ++Sequence), EvidenceStreamId = StreamId, ReceivedAtUtc = Clock.At(received) };
            Apply(Evaluator.Evaluate(State, Rule, observation, Epoch, StreamId, Bypasses));
        }

        public void Missing(int received)
        {
            Clock.Set(received);
            Apply(Evaluator.Evaluate(State, Rule, new(State.Series, new(Epoch, ++Sequence), StreamId,
                Clock.At(received), Clock.At(received), false, false), Epoch, StreamId, Bypasses));
        }

        public void Apply(MonitoringEvaluationResult result)
        {
            State = result.State;
            Events.AddRange(result.Events);
            Outbox.AddRange(result.Outbox);
        }
    }
}
