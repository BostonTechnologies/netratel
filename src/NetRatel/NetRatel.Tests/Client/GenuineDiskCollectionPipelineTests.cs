using AwesomeAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Client.Service.Gateway;
using NetRatel.Shared.Contracts.Monitoring;
using Xunit;
using WireDisk = NetRatel.AgentGateway.Contracts.V1.TelemetryDisk;
using WireQuality = NetRatel.AgentGateway.Contracts.V1.TelemetryDiskCollectionQuality;

namespace NetRatel.Tests.Client;

/// <summary>Collection/wire/projection unit coverage. These tests do not establish the separately required physical disk incident acceptance.</summary>
public sealed class GenuineDiskCollectionPipelineTests
{
    [Fact]
    public void ActualDriveInfoCollectionKeepsItsIdentityBytesAndClockAcrossFastFramesAndWireProjection()
    {
        var session = new GatewayPresenceSession(91, Guid.NewGuid(), 7, Guid.NewGuid());
        var collector = new GatewayTelemetrySnapshotCollector("disk-collection-test", _ => { }, TimeProvider.System);
        var slow = collector.CreateFrame(session, 1, TimeProvider.System.GetUtcNow(), includeSlowMetrics: true);
        slow.Disks.Should().NotBeEmpty();
        var fast = collector.CreateFrame(session, 2, TimeProvider.System.GetUtcNow(), includeSlowMetrics: false);
        fast.Disks.Count.Should().Be(slow.Disks.Count);
        foreach (var disk in slow.Disks)
        {
            Guid.TryParseExact(disk.CollectionId, "D", out var id).Should().BeTrue();
            id.Should().NotBe(Guid.Empty);
            disk.CollectionQuality.Should().Be(WireQuality.Complete);
            disk.CollectedAtUtc.ToDateTimeOffset().Should().BeOnOrBefore(slow.ObservedAtUtc.ToDateTimeOffset());
            fast.Disks.Single(other => other.Scope == disk.Scope).ToByteArray().Should().Equal(disk.ToByteArray());
        }

        var envelope = TelemetrySnapshotSerializer.CreateEnvelope(fast, session, "2.0");
        var decoded = AgentTelemetryFrame.Parser.ParseFrom(envelope.ToByteArray());
        var received = TimeProvider.System.GetUtcNow();
        AgentTelemetryProtocolValidator.Validate(decoded.Snapshot, new(91, session.AgentId), "1.0", 64, received).IsValid.Should().BeTrue();
        var client = new ClientKey(91, session.AgentId);
        var snapshot = AgentTelemetryGatewayMapper.MapSnapshot(decoded.Snapshot, client, 7, received);
        var wireDisk = decoded.Snapshot.Disks[0];
        var appDisk = snapshot.Disks.Single(disk => disk.Scope == wireDisk.Scope);
        appDisk.CollectionId.Should().Be(Guid.Parse(wireDisk.CollectionId));
        appDisk.CollectedAtUtc.Should().Be(wireDisk.CollectedAtUtc.ToDateTimeOffset());
        var rule = Rule();
        var series = new MonitoringSeriesKey(91, rule.RuleId, session.AgentId, MonitoringSeriesEvaluator.DiskResourceKey(wireDisk.Scope));
        var fence = new MonitoringEvidenceFence(client, session.ConnectionId, 7, Guid.NewGuid());
        var observation = MonitoringObservationFactory.FromTelemetry(rule, series, new(fence, snapshot));
        observation.Complete.Should().BeTrue();
        observation.ObservedAtUtc.Should().Be(wireDisk.CollectedAtUtc.ToDateTimeOffset());
        observation.DiskCollection!.CollectionId.Should().Be(Guid.Parse(wireDisk.CollectionId));
        observation.DiskCollection.PayloadFingerprint.Should().HaveLength(64);

        // A genuine later collection receives new IDs even when the drive's exact byte counters did not change.
        var later = collector.CreateFrame(session, 3, TimeProvider.System.GetUtcNow(), includeSlowMetrics: true);
        foreach (var disk in later.Disks)
        {
            var prior = slow.Disks.SingleOrDefault(previous => previous.Scope == disk.Scope);
            if (prior is not null) disk.CollectionId.Should().NotBe(prior.CollectionId);
        }
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("empty-id")]
    [InlineData("invalid-id")]
    [InlineData("missing-clock")]
    [InlineData("future-envelope")]
    [InlineData("future-receipt")]
    [InlineData("unspecified-quality")]
    [InlineData("invalid-quality")]
    [InlineData("complete-without-exact-bytes")]
    public void InvalidOrFutureCollectionProofIsRejectedBeforeProjection(string defect)
    {
        var received = TimeProvider.System.GetUtcNow();
        var frame = Frame(received);
        var disk = frame.Disks[0];
        switch (defect)
        {
            case "missing-id": disk.CollectionId = ""; break;
            case "empty-id": disk.CollectionId = Guid.Empty.ToString("D"); break;
            case "invalid-id": disk.CollectionId = "not-a-collection"; break;
            case "missing-clock": disk.CollectedAtUtc = null; break;
            case "future-envelope": disk.CollectedAtUtc = Timestamp.FromDateTimeOffset(received.AddSeconds(1)); break;
            case "future-receipt":
                disk.CollectedAtUtc = Timestamp.FromDateTimeOffset(received.AddSeconds(1));
                frame.ObservedAtUtc = Timestamp.FromDateTimeOffset(received.AddSeconds(2));
                break;
            case "unspecified-quality": disk.CollectionQuality = WireQuality.Unspecified; break;
            case "invalid-quality": disk.CollectionQuality = (WireQuality)99; break;
            case "complete-without-exact-bytes": disk.ClearTotalBytes(); disk.ClearFreeBytes(); break;
        }
        AgentTelemetryProtocolValidator.Validate(frame, new(91, Guid.Parse(frame.ClientId)), "1.0", 64, received).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LegacyPartialUnsupportedAndFailedSamplesRemainUnknown(int quality)
    {
        var received = TimeProvider.System.GetUtcNow();
        var frame = Frame(received);
        frame.Disks[0].CollectionQuality = (WireQuality)quality;
        if (quality == 0)
        {
            frame.Disks[0].CollectionId = "";
            frame.Disks[0].CollectedAtUtc = null;
        }
        var client = new ClientKey(91, Guid.Parse(frame.ClientId));
        AgentTelemetryProtocolValidator.Validate(frame, new(91, client.AgentId), "1.0", 64, received).IsValid.Should().BeTrue();
        var snapshot = AgentTelemetryGatewayMapper.MapSnapshot(frame, client, 7, received);
        var fence = new MonitoringEvidenceFence(client, Guid.Parse(frame.ConnectionId), 7, Guid.NewGuid());
        var rule = Rule();
        var series = new MonitoringSeriesKey(91, rule.RuleId, client.AgentId, "disk:/");
        var observation = MonitoringObservationFactory.FromTelemetry(rule, series, new(fence, snapshot));
        observation.Complete.Should().BeFalse();
        observation.Supported.Should().BeFalse();
        observation.NumericValue.Should().BeNull();
        if (quality == 0) observation.DiskCollection.Should().BeNull();
        else observation.DiskCollection.Should().NotBeNull();
    }

    [Fact]
    public void ImmutableCollectionFingerprintIncludesRawBytesQualityAndEveryRoundedCounter()
    {
        var received = TimeProvider.System.GetUtcNow();
        var frame = Frame(received);
        var client = new ClientKey(91, Guid.Parse(frame.ClientId));
        var fence = new MonitoringEvidenceFence(client, Guid.Parse(frame.ConnectionId), 7, Guid.NewGuid());
        var rule = Rule();
        var series = new MonitoringSeriesKey(91, rule.RuleId, client.AgentId, "disk:/");
        var snapshot = AgentTelemetryGatewayMapper.MapSnapshot(frame, client, 7, received);
        var initial = MonitoringObservationFactory.FromTelemetry(rule, series, new(fence, snapshot));
        foreach (var changed in new[]
        {
            snapshot.Disks[0] with { FreeBytes = 11 },
            snapshot.Disks[0] with { TotalBytes = 101 },
            snapshot.Disks[0] with { FreeGb = 0.2 },
            snapshot.Disks[0] with { CollectionQuality = NetRatel.Application.Telemetry.TelemetryDiskCollectionQuality.Partial }
        })
        {
            var mutation = MonitoringObservationFactory.FromTelemetry(rule, series, new(fence, snapshot with { Disks = [changed] }));
            mutation.DiskCollection!.CollectionId.Should().Be(initial.DiskCollection!.CollectionId);
            mutation.DiskCollection.PayloadFingerprint.Should().NotBe(initial.DiskCollection.PayloadFingerprint);
        }
    }

    private static TelemetryFrame Frame(DateTimeOffset at) => new()
    {
        ProtocolVersion = "1.0", TenantId = 91, ClientId = Guid.NewGuid().ToString("D"),
        ConnectionEpoch = 7, ConnectionId = Guid.NewGuid().ToString("D"), Sequence = 1,
        ObservedAtUtc = Timestamp.FromDateTimeOffset(at),
        Disks = { new WireDisk { Scope = "/", TotalGb = 1, FreeGb = 0.1, UsedGb = 0.9, UsagePercent = 90,
            TotalBytes = 100, FreeBytes = 10, CollectionId = Guid.NewGuid().ToString("D"),
            CollectedAtUtc = Timestamp.FromDateTimeOffset(at), CollectionQuality = WireQuality.Complete } }
    };

    private static MonitoringRuleDto Rule() => new(91, Guid.NewGuid(), 1, 1, "disk", true,
        MonitoringSeverity.Warning, new(MonitoringTargetMode.AllEligible, [], []),
        new(MonitoringMetricKind.DiskFreeSpace, MonitoringNumericUnit.Bytes, 20, 50, null, null, []),
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
}
