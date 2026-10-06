using AwesomeAssertions;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;
using Xunit;

namespace NetRatel.Tests.Monitoring;

/// <summary>Deterministic evaluator clock boundaries, not the separately required physical full-client acceptance.</summary>
public sealed class MonitoringDiskReceiptClockBoundaryTests
{
    [Fact]
    public void Wall_clock_rewind_retains_already_admitted_transport_receipt_high_water()
    {
        var s = new Scenario();
        s.Receive(1, 0, 0, s.First);
        s.Receive(1, 15, 15, s.First);
        s.State.LastDiskTransportReceivedAtUtc.Should().Be(s.Clock.At(15));
        s.Receive(1, 12, 12, s.First);
        s.State.LastDiskTransportReceivedAtUtc.Should().Be(s.Clock.At(15));
        s.State.LatestEvidence!.UnknownReason.Should().Be("reordered_clock");
        s.State.EvidenceQuality.Should().Be(MonitoringEvidenceQuality.Unknown);
        s.State.LastDiskCollection.Should().Be(s.First);
        s.State.WindowStartedAtUtc.Should().BeNull();
        // Catching up the receipt clock does not restore the old cached collection.
        s.Receive(1, 20, 20, s.First);
        s.State.EvidenceQuality.Should().Be(MonitoringEvidenceQuality.Unknown);
        s.Events.Should().BeEmpty();
    }

    [Theory]
    [InlineData("future")]
    [InlineData("default")]
    public void Invalid_first_receipt_in_new_committed_epoch_cannot_carry_the_previous_epoch_clock(string defect)
    {
        var s = new Scenario();
        s.Receive(1, 0, 0, s.First);
        s.Receive(1, 15, 15, s.First);
        s.Clock.Set(5);
        var received = defect == "future" ? s.Clock.At(20) : default;
        s.Apply(s.Evaluator.Evaluate(s.State, s.Rule,
            new(s.State.Series, new(2, 1), s.Stream, s.First.CollectedAtUtc, received, true, true, 5, DiskCollection: s.First),
            2, s.Stream, []));
        s.State.Cursor!.ConnectionEpoch.Should().Be(2);
        s.State.LastDiskTransportReceivedAtUtc.Should().BeNull();
        s.State.LastDiskCollection.Should().Be(s.First);
        s.State.EvidenceQuality.Should().Be(MonitoringEvidenceQuality.Unknown);
        // This physical collection is newer than the retained collection stamp and uses the new epoch's valid receipt clock.
        var fresh = new MonitoringDiskCollectionStamp(Guid.NewGuid(), s.Clock.At(6), new string('B', 64));
        s.Clock.Set(6);
        s.Apply(s.Evaluator.Evaluate(s.State, s.Rule,
            new(s.State.Series, new(2, 2), s.Stream, fresh.CollectedAtUtc, s.Clock.At(6), true, true, 5, DiskCollection: fresh),
            2, s.Stream, []));
        s.State.LastDiskTransportReceivedAtUtc.Should().Be(s.Clock.At(6));
        s.State.LastDiskCollection.Should().Be(fresh);
        s.State.EvidenceQuality.Should().Be(MonitoringEvidenceQuality.Fresh);
        s.State.Phase.Should().Be(MonitoringPhase.Pending);
        s.Events.Should().BeEmpty();
    }

    private sealed class Clock : TimeProvider
    {
        private static readonly DateTimeOffset Origin = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _now = Origin.AddSeconds(-1);
        public override DateTimeOffset GetUtcNow() => _now;
        public DateTimeOffset At(int at) => Origin.AddSeconds(at);
        public void Set(int at) => _now = At(at);
    }

    private sealed class Scenario
    {
        public Clock Clock { get; } = new();
        public MonitoringSeriesEvaluator Evaluator { get; }
        public MonitoringRuleDto Rule { get; } = new(41, Guid.NewGuid(), 1, 1, "disk", true,
            MonitoringSeverity.Warning, new(MonitoringTargetMode.AllEligible, [], []),
            new(MonitoringMetricKind.DiskFreePercent, MonitoringNumericUnit.Percent, 10, 15, "/", null, []),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45));
        public MonitoringSeriesState State { get; private set; }
        public Guid Stream { get; } = Guid.NewGuid();
        public MonitoringDiskCollectionStamp First { get; }
        public List<MonitoringEventIntent> Events { get; } = [];
        private ulong _sequence;

        public Scenario()
        {
            Evaluator = new(Clock);
            State = Evaluator.CreateInitial(new(41, Rule.RuleId, Guid.NewGuid(), "disk:/"), Rule);
            First = new(Guid.NewGuid(), Clock.At(0), new string('A', 64));
        }
        public void Receive(long epoch, int now, int received, MonitoringDiskCollectionStamp stamp)
        {
            Clock.Set(now);
            Apply(Evaluator.Evaluate(State, Rule,
                new(State.Series, new(epoch, ++_sequence), Stream, stamp.CollectedAtUtc, Clock.At(received), true, true, 5, DiskCollection: stamp),
                epoch, Stream, []));
        }
        public void Apply(MonitoringEvaluationResult result)
        {
            State = result.State;
            Events.AddRange(result.Events);
        }
    }
}
