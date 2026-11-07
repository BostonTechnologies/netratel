using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class TelemetrySnapshotSerializerTests
{
    [Fact]
    public void FullEnvelopeWithExactCountersFitsExistingLimitWithoutMutatingCachedScopes()
    {
        var session = new GatewayPresenceSession(91, Guid.NewGuid(), ulong.MaxValue, Guid.NewGuid());
        var snapshot = new TelemetryFrame
        {
            Sequence = ulong.MaxValue, ProtocolVersion = "1.0", TenantId = session.TenantId,
            ClientId = session.AgentId.ToString("D"), ConnectionEpoch = session.ConnectionEpoch,
            ConnectionId = session.ConnectionId.ToString("D"), ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Cpu = new() { UsagePercent = 95 }, Memory = new() { TotalMb = 100, UsedMb = 50, AvailableMb = 50, UsagePercent = 50 }
        };
        for (var index = 0; index < 64; index++)
        {
            snapshot.Disks.Add(new TelemetryDisk { Scope = "/" + new string('é', 120) + index, TotalGb = 100, UsedGb = 99, FreeGb = 1,
                UsagePercent = 99, TotalBytes = ulong.MaxValue, FreeBytes = 1,
                CollectionId = Guid.NewGuid().ToString("D"), CollectedAtUtc = snapshot.ObservedAtUtc.Clone(),
                CollectionQuality = TelemetryDiskCollectionQuality.Complete });
            snapshot.Networks.Add(new TelemetryNetwork { Scope = new string('é', 120) + index, RxBytesPerSec = 1024, TxBytesPerSec = 2048 });
        }
        var envelope = TelemetrySnapshotSerializer.CreateEnvelope(snapshot, session, "2.0");
        Assert.InRange(envelope.CalculateSize(), 1, 16 * 1024 - 1);
        Assert.Equal(ulong.MaxValue, envelope.Sequence);
        Assert.Equal(snapshot.Cpu, envelope.Snapshot.Cpu);
        Assert.Equal(snapshot.Memory, envelope.Snapshot.Memory);
        Assert.NotEmpty(envelope.Snapshot.Disks);
        Assert.All(envelope.Snapshot.Disks, disk =>
        {
            Assert.True(disk.HasTotalBytes); Assert.True(disk.HasFreeBytes);
            var cached = snapshot.Disks.Single(original => original.Scope == disk.Scope);
            Assert.Equal(cached.CollectionId, disk.CollectionId);
            Assert.Equal(cached.CollectedAtUtc, disk.CollectedAtUtc);
            Assert.Equal(cached.CollectionQuality, disk.CollectionQuality);
        });
        Assert.Equal(64, snapshot.Disks.Count);
        Assert.Equal(64, snapshot.Networks.Count);
    }
    [Fact]
    public void EligibleCapacityWinsBeforeScopeAndEnvelopeBudgetsAndExcludedInventoryIsBounded()
    {
        var session = new GatewayPresenceSession(91, Guid.NewGuid(), 1, Guid.NewGuid());
        var snapshot = new TelemetryFrame { Sequence = 1, ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };
        for (var index = 0; index < 64; index++)
            snapshot.Disks.Add(new TelemetryDisk { Scope = "/snap/" + index, Metadata = new() { DefaultExclusionReason = "package-image" } });
        for (var index = 0; index < 30; index++)
            snapshot.Disks.Add(new TelemetryDisk { Scope = "/data/" + new string('é', 120) + index, TotalBytes = 100, FreeBytes = 10,
                CollectionId = Guid.NewGuid().ToString("D"), CollectedAtUtc = snapshot.ObservedAtUtc.Clone(), CollectionQuality = TelemetryDiskCollectionQuality.Complete });
        var selected = GatewayTelemetrySnapshotCollector.PrioritizeDisks(snapshot.Disks);
        Assert.Equal(30, selected.Count(disk => disk.Metadata is null));
        Assert.Equal(16, selected.Count(disk => disk.Metadata is not null));
        // Force the complete-envelope trim path, with excluded inventory originally ordered first.
        snapshot.TransportHealth = new() { OsVersion = new string('x', 12000) };
        var envelope = TelemetrySnapshotSerializer.CreateEnvelope(snapshot, session, "2.0");
        Assert.InRange(envelope.CalculateSize(), 1, TelemetrySnapshotSerializer.MaximumEnvelopeBytes);
        Assert.NotEmpty(envelope.Snapshot.Disks);
        Assert.All(envelope.Snapshot.Disks, disk => Assert.Null(disk.Metadata));
        Assert.True(envelope.Snapshot.DiskInventoryTruncated);
        Assert.Equal(94, snapshot.Disks.Count);
    }

}
