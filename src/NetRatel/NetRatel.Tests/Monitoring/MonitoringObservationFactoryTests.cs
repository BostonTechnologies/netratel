using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Application.Telemetry;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.Monitoring;

public sealed class MonitoringObservationFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 11, 0, 0, TimeSpan.Zero);
    private static readonly ClientKey Client = new(41, Guid.NewGuid());
    private static readonly MonitoringEvidenceFence Fence = new(Client, Guid.NewGuid(), 9, Guid.NewGuid());

    [Fact]
    public void ExactBytesAndLegacyRoundedCountersPreserveTheirPrecision()
    {
        var rule = DiskRule(MonitoringMetricKind.DiskFreeSpace);
        var series = new MonitoringSeriesKey(Client.TenantId, rule.RuleId, Client.AgentId, "disk:/");
        var legacy = MonitoringObservationFactory.FromTelemetry(rule, series, Input(new("/", 10, 5, 5, 50)));
        var exact = MonitoringObservationFactory.FromTelemetry(rule, series, Input(new("/", 10, 5, 5, 50, 10UL << 30, 5UL << 30)));
        Assert.True(legacy.Complete);
        Assert.Equal(0.1 * Math.Pow(1024, 3), legacy.NumericResolution);
        Assert.Equal(5d * Math.Pow(1024, 3), exact.NumericValue);
        Assert.Equal(0, exact.NumericResolution);
        Assert.Equal(Fence.EvidenceStreamId, exact.EvidenceStreamId);

        var evaluator = new MonitoringSeriesEvaluator(new FrozenClock(Now));
        var state = evaluator.CreateInitial(series, rule) with { NotBeforeObservedAtUtc = Now.AddSeconds(-1), NotBeforeReceivedAtUtc = Now.AddSeconds(-1) };
        Assert.Equal("numeric_uncertainty", evaluator.Evaluate(state, rule, legacy, 9, Fence.EvidenceStreamId, []).State.LatestEvidence!.UnknownReason);
        Assert.Equal(MonitoringEvidenceQuality.Fresh, evaluator.Evaluate(state, rule, exact, 9, Fence.EvidenceStreamId, []).State.EvidenceQuality);
    }

    [Fact]
    public void ExactFreePercentageUsesBytesWhileLegacyRatiosKeepBounds()
    {
        var rule = DiskRule(MonitoringMetricKind.DiskFreePercent);
        var series = new MonitoringSeriesKey(Client.TenantId, rule.RuleId, Client.AgentId, "disk:/");
        var legacy = MonitoringObservationFactory.FromTelemetry(rule, series, Input(new("/", 1, 0.9, 0.1, 90)));
        var exact = MonitoringObservationFactory.FromTelemetry(rule, series, Input(new("/", 1, 0.9, 0.1, 90, 1000, 101)));
        Assert.True(legacy.NumericResolution > 0);
        Assert.Equal(10.1d, exact.NumericValue!.Value, 10);
        Assert.InRange(exact.NumericResolution, double.Epsilon, 1e-10);
    }

    [Fact]
    public void ExactIntegerCountersAboveDoublePrecisionKeepRepresentableUncertainty()
    {
        var free = (1UL << 53) + 1;
        var rule = DiskRule(MonitoringMetricKind.DiskFreeSpace) with
            { Condition = new(MonitoringMetricKind.DiskFreeSpace, MonitoringNumericUnit.Bytes, 1d * (1UL << 53), 1d * (1UL << 54), null, null, []) };
        var series = new MonitoringSeriesKey(Client.TenantId, rule.RuleId, Client.AgentId, "disk:/");
        var observation = MonitoringObservationFactory.FromTelemetry(rule, series, Input(new("/", 0, 0, 0, 0, 1UL << 55, free)));
        Assert.True(observation.NumericResolution >= 2d);
        var evaluator = new MonitoringSeriesEvaluator(new FrozenClock(Now));
        var state = evaluator.CreateInitial(series, rule) with { NotBeforeObservedAtUtc = Now.AddSeconds(-1), NotBeforeReceivedAtUtc = Now.AddSeconds(-1) };
        Assert.Equal("numeric_uncertainty", evaluator.Evaluate(state, rule, observation, 9, Fence.EvidenceStreamId, []).State.LatestEvidence!.UnknownReason);
    }

    [Theory]
    [InlineData(null, 10UL)]
    [InlineData(10UL, null)]
    [InlineData(0UL, 0UL)]
    [InlineData(10UL, 11UL)]
    public void InvalidExactCounterPairsRemainUnknown(ulong? total, ulong? free)
    {
        var rule = DiskRule(MonitoringMetricKind.DiskFreeSpace);
        var series = new MonitoringSeriesKey(Client.TenantId, rule.RuleId, Client.AgentId, "disk:/");
        var observation = MonitoringObservationFactory.FromTelemetry(rule, series, Input(new("/", 10, 5, 5, 50, total, free)));
        Assert.False(observation.Complete);
        Assert.Null(observation.NumericValue);
    }

    [Fact]
    public void CachedForeignAndAmbiguousDiskEvidenceRemainUnknown()
    {
        var rule = DiskRule(MonitoringMetricKind.DiskFreePercent);
        var series = new MonitoringSeriesKey(Client.TenantId, rule.RuleId, Client.AgentId, "disk:/");
        var input = Input(new("/", 10, 5, 5, 50));
        Assert.False(MonitoringObservationFactory.FromTelemetry(rule, series, input with { Snapshot = input.Snapshot with { IsAuthoritative = false } }).Complete);
        Assert.False(MonitoringObservationFactory.FromTelemetry(rule, series, input with { Fence = Fence with { Client = new(42, Client.AgentId) } }).Complete);
        Assert.False(MonitoringObservationFactory.FromTelemetry(rule, series, input with { Snapshot = input.Snapshot with { Disks = [input.Snapshot.Disks[0], input.Snapshot.Disks[0]] } }).Complete);
    }

    [Fact]
    public void OnlyCompleteCurrentSelectedServiceWatchCanSupplyMissingEvidence()
    {
        var rule = ServiceRule(ClientServicePlatform.Windows, "spooler");
        var series = new MonitoringSeriesKey(Client.TenantId, rule.RuleId, Client.AgentId, MonitoringSeriesEvaluator.ServiceResourceKey("spooler", ClientServicePlatform.Windows));
        var service = new ClientServiceObservation("Spooler", "Spooler", ClientServicePlatform.Windows, ClientServiceState.Missing, "missing", null, null, null, null, null, Now, true);
        var input = ServicesInput(service);
        var valid = MonitoringObservationFactory.FromServices(rule, series, input);
        Assert.True(valid.Complete);
        Assert.True(valid.AuthoritativeMissing);
        Assert.Equal((ulong)15, valid.Cursor.Sequence);
        foreach (var changed in new[]
        {
            input.Services with { LatestAttempt = input.Services.LatestAttempt! with { Status = ServiceCollectionStatus.Partial } },
            input.Services with { LatestAttempt = input.Services.LatestAttempt! with { Kind = ServiceSnapshotKind.Inventory } },
            input.Services with { LatestAttempt = input.Services.LatestAttempt! with { WatchPolicyRevision = 2 } },
            input.Services with { LatestAttempt = input.Services.LatestAttempt! with { Sequence = 14 } },
            input.Services with { ConnectionId = Guid.NewGuid() },
            input.Services with { MonitoredServiceNames = [] },
            input.Services with { WatchedServices = [service with { ObservedAtUtc = Now.AddSeconds(-1) }] }
        })
        {
            var unknown = MonitoringObservationFactory.FromServices(rule, series, input with { Services = changed });
            Assert.False(unknown.Complete);
            Assert.False(unknown.AuthoritativeMissing);
        }
    }

    [Fact]
    public void LinuxSelectedServiceNamesKeepExactCase()
    {
        var rule = ServiceRule(ClientServicePlatform.LinuxSystemd, "Foo.service");
        var series = new MonitoringSeriesKey(Client.TenantId, rule.RuleId, Client.AgentId, MonitoringSeriesEvaluator.ServiceResourceKey("Foo.service", ClientServicePlatform.LinuxSystemd));
        var service = new ClientServiceObservation("foo.service", "foo", ClientServicePlatform.LinuxSystemd, ClientServiceState.Running, "active", null, "loaded", "active", "running", "enabled", Now);
        var input = ServicesInput(service) with { Services = ServicesInput(service).Services with { MonitoredServiceNames = ["Foo.service"] } };
        Assert.False(MonitoringObservationFactory.FromServices(rule, series, input).Complete);
    }

    private static MonitoringTelemetryInput Input(TelemetryDisk disk) => new(Fence,
        new(Client, Fence.ConnectionEpoch, 15, Now, Now, null, null, [disk], [], null, "akka", true));

    private static MonitoringServicesInput ServicesInput(ClientServiceObservation service) => new(Fence,
        new(Client, Fence.ConnectionEpoch, 15, 1, null,
            new(Guid.NewGuid(), ServiceSnapshotKind.Watch, ServiceCollectionStatus.Complete, Fence.ConnectionEpoch, 15, Now, Now, 3),
            [service], [service.Name], 3, Fence.ConnectionId));

    private static MonitoringRuleDto DiskRule(MonitoringMetricKind kind) => new(Client.TenantId, Guid.NewGuid(), 1, 1, "disk", true,
        MonitoringSeverity.Warning, new(MonitoringTargetMode.AllEligible, [], []),
        new(kind, kind == MonitoringMetricKind.DiskFreeSpace ? MonitoringNumericUnit.GiB : MonitoringNumericUnit.Percent,
            kind == MonitoringMetricKind.DiskFreeSpace ? 5 : 10, kind == MonitoringMetricKind.DiskFreeSpace ? 8 : 15, null, null, []),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));

    private static MonitoringRuleDto ServiceRule(ClientServicePlatform platform, string name) => DiskRule(MonitoringMetricKind.DiskFreePercent) with
    {
        Condition = new(MonitoringMetricKind.ServiceExpectedState, null, null, null, name, platform, [ClientServiceState.Running])
    };

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
