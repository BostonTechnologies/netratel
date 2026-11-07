using System.Collections.Immutable;
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
            if (disk.HasTotalBytes) disk.CollectionQuality.Should().Be(WireQuality.Complete);
            else { disk.CollectionQuality.Should().Be(WireQuality.Unsupported); disk.Metadata.Should().NotBeNull(); }
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

    [Fact]
    public void SmallClientClockLeadKeepsTransportOpenButCannotBecomeEarlyMonitoringEvidence()
    {
        var collected = TimeProvider.System.GetUtcNow();
        var frame = Frame(collected);
        var client = new ClientKey(91, Guid.Parse(frame.ClientId));
        var earlyReceipt = collected.AddMilliseconds(-250);
        AgentTelemetryProtocolValidator.Validate(frame, new(91, client.AgentId), "1.0", 64, earlyReceipt).IsValid.Should().BeTrue();

        var rule = Rule();
        var series = new MonitoringSeriesKey(91, rule.RuleId, client.AgentId, "disk:/");
        var fence = new MonitoringEvidenceFence(client, Guid.Parse(frame.ConnectionId), 7, Guid.NewGuid());
        var early = AgentTelemetryGatewayMapper.MapSnapshot(frame, client, 7, earlyReceipt);
        var earlyObservation = MonitoringObservationFactory.FromTelemetry(rule, series, new(fence, early));
        earlyObservation.Complete.Should().BeFalse();

        var laterReceipt = collected.AddSeconds(1);
        var later = AgentTelemetryGatewayMapper.MapSnapshot(frame, client, 7, laterReceipt);
        var laterObservation = MonitoringObservationFactory.FromTelemetry(rule, series, new(fence, later));
        laterObservation.Complete.Should().BeTrue();

        AgentTelemetryProtocolValidator.Validate(frame, new(91, client.AgentId), "1.0", 64,
            collected.AddMilliseconds(-501)).IsValid.Should().BeFalse();
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

    [Fact]
    public void LinuxMountMetadataClassifiesPackagePseudoAndProvenBackingViewsWithoutDiscardingRealVolumes()
    {
        var lines = new[]
        {
            "10 1 8:1 / / rw,relatime shared:1 - ext4 /dev/system rw",
            "11 1 8:2 / /data/backup/volume rw - ext4 /dev/backup rw",
            "12 1 7:1 / /snap/core/42 ro - squashfs /dev/loop1 ro",
            "13 1 8:3 / /readonly-data ro - ext4 /dev/data ro",
            "14 1 8:1 / /mirror rw - ext4 /dev/system rw",
            "15 1 0:20 / /a rw - overlay overlay rw,lowerdir=/images,upperdir=/data/backup/volume/upper,workdir=/data/backup/volume/work",
            "16 1 0:21 / /quota-a rw - zfs pool/quota-a rw",
            "17 1 0:22 / /quota-b rw - zfs pool/quota-b rw",
            "18 1 0:23 / /network rw - nfs backup.example:/export rw",
            "19 1 0:24 / /proc rw - proc proc rw",
            "20 1 8:1 /archive /subdirectory-view rw - ext4 /dev/system rw"
        };
        var parsed = lines.Select(ParseMount).ToArray();
        var result = LinuxMountInventory.Classify(parsed, _ => true).ToDictionary(item => item.Mount.Mount);
        Assert.Equal("package-image", result["/snap/core/42"].Metadata.DefaultExclusionReason);
        Assert.Equal("pseudo-filesystem", result["/proc"].Metadata.DefaultExclusionReason);
        Assert.Equal("duplicate-capacity", result["/mirror"].Metadata.DefaultExclusionReason);
        Assert.Equal("duplicate-capacity", result["/a"].Metadata.DefaultExclusionReason);
        Assert.Equal(result["/data/backup/volume"].Metadata.CapacityKey, result["/a"].Metadata.CapacityKey);
        foreach (var mount in new[] { "/", "/data/backup/volume", "/readonly-data", "/quota-a", "/quota-b", "/network", "/subdirectory-view" })
            Assert.Null(result[mount].Metadata.DefaultExclusionReason);
        Assert.NotEqual(result["/quota-a"].Metadata.CapacityKey, result["/quota-b"].Metadata.CapacityKey);
        var reordered = LinuxMountInventory.Classify(parsed.Reverse(), _ => true).ToDictionary(item => item.Mount.Mount);
        Assert.All(result, pair => Assert.Equal(pair.Value.Metadata, reordered[pair.Key].Metadata));
        // A container's only meaningful overlay root survives, even with visible backing paths.
        var containerRoot = ParseMount("22 1 0:25 / / rw - overlay overlay rw,upperdir=/data/backup/volume/upper,workdir=/data/backup/volume/work");
        Assert.Null(LinuxMountInventory.Classify([containerRoot], _ => true).Single().Metadata.DefaultExclusionReason);
    }

    [Fact]
    public void LinuxMountParserHandlesOptionalFieldsEscapesAndRefusesUnrepresentableControlScopes()
    {
        var mount = ParseMount(@"42 1 8:9 / /data/Backup\040Files rw,nosuid shared:4 master:2 - ext4 /dev/disk\134alias rw,errors=remount-ro");
        Assert.Equal("/data/Backup Files", mount.Mount);
        Assert.Equal("/dev/disk\\alias", mount.Source);
        Assert.Equal(new[] { "shared:4", "master:2" }, mount.OptionalFields);
        Assert.Contains("errors=remount-ro", mount.SuperOptions);
        Assert.False(LinuxMountInventory.TryParse(@"42 1 8:9 / /data/bad\011scope rw - ext4 /dev/disk rw", out _));
        Assert.False(LinuxMountInventory.TryParse("42 1 malformed / /data rw - ext4 /dev/disk rw", out _));
    }

    [Fact]
    public void RulePolicyUsesBoundedCaseAwareGlobsExcludesWinAndNullDefaultsKeepLegacyFingerprint()
    {
        var rule = Rule();
        var package = new TelemetryDiskMetadataDto("squashfs", "/dev/loop7", "/", new string('A', 64), true, "package-image");
        Assert.False(MonitoringDiskPolicy.Select("/snap/core/42", package, rule.Condition).Eligible);
        var include = rule.Condition with { DiskPolicy = new(["/snap/*"], [], [], []) };
        Assert.True(MonitoringDiskPolicy.Select("/snap/core/42", package, include).Eligible);
        Assert.False(MonitoringDiskPolicy.Select("/Snap/core/42", package, include).Eligible);
        Assert.False(MonitoringDiskPolicy.Select("/snap/core/42", package, include with { DiskPolicy = include.DiskPolicy! with { ExcludeFileSystems = ["squashfs"] } }).Eligible);
        var windows = include with { DiskPolicy = new([@"C:\DATA\*"], [], [], []) };
        Assert.True(MonitoringDiskPolicy.Select(@"c:\data\backup", null, windows).Eligible);
        Assert.Equal("unclassified-legacy", MonitoringDiskPolicy.Select("/data", null, rule.Condition).Reason);
        Assert.False(MonitoringDiskPolicy.Select("/other", null, rule.Condition with { ResourceName = "/data" }).Eligible);
        Assert.Equal(MonitoringContractValidator.EvaluationFingerprint(rule), MonitoringContractValidator.EvaluationFingerprint(rule with { Condition = rule.Condition with { DiskPolicy = new([], [], [], []) } }));
        Assert.NotEqual(MonitoringContractValidator.EvaluationFingerprint(rule), MonitoringContractValidator.EvaluationFingerprint(rule with { Condition = include }));
        Assert.False(MonitoringDiskPolicy.TryValidate(new(Enumerable.Repeat("/*", 33).ToImmutableArray(), [], [], []), out _));
        Assert.False(MonitoringDiskPolicy.TryValidate(new([new string('x', 257)], [], [], []), out _));
        Assert.DoesNotContain("DiskPolicy", System.Text.Json.JsonSerializer.Serialize(rule));
    }

    private static LinuxMountEntry ParseMount(string line)
    {
        Assert.True(LinuxMountInventory.TryParse(line, out var mount));
        return mount!;
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
