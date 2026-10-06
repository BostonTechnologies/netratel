using AwesomeAssertions;
using Google.Protobuf;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Services;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ServiceSnapshotSerializerTests
{
    [Fact]
    public void Chunking_MeasuresActualProtobufEnvelope_WithUnicodeAndMaximumSequence()
    {
        var observed = DateTimeOffset.UtcNow;
        var services = Enumerable.Range(0, 500).Select(index => new ClientServiceObservation($"unit{index}.service", new string('界', 512),
            ClientServicePlatform.LinuxSystemd, ClientServiceState.Stopped, "inactive/dead", null, "loaded", "inactive", "dead", "disabled", observed)).ToArray();
        var result = new ServiceCollectionResult(Guid.NewGuid(), ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Complete, observed, services);
        var session = new GatewayPresenceSession(int.MaxValue, Guid.NewGuid(), ulong.MaxValue, Guid.NewGuid());

        var chunks = ServiceSnapshotSerializer.CreateChunks(result, session, "1.0");

        chunks.Should().HaveCountGreaterThan(1);
        chunks.SelectMany(chunk => chunk.Services).Select(service => service.Name).Should().Equal(services.Select(service => service.Name));
        chunks.Select(chunk => chunk.CollectionId).Distinct().Should().ContainSingle();
        chunks.Select(chunk => chunk.ChunkIndex).Should().Equal(Enumerable.Range(0, chunks.Count).Select(index => (uint)index));
        chunks.SkipLast(1).Should().OnlyContain(chunk => !chunk.IsFinal);
        chunks[^1].IsFinal.Should().BeTrue();
        chunks.Should().OnlyContain(chunk => (int)chunk.Status == (int)ServiceCollectionStatus.Complete);
        chunks.Select(chunk => ServiceSnapshotSerializer.CreateEnvelope(chunk, session, "1.0", ulong.MaxValue).CalculateSize()).Should().OnlyContain(bytes => bytes < 12 * 1024);
    }

    [Fact]
    public void InventoryByteLimit_DowngradesAllChunksWithoutClaimingComplete()
    {
        var observed = DateTimeOffset.UtcNow;
        var services = Enumerable.Range(0, 2048).Select(index => new ClientServiceObservation($"unit{index}.service", new string('界', 512),
            ClientServicePlatform.LinuxSystemd, ClientServiceState.Running, new string('界', 128), null, "loaded", "active", "running", "enabled", observed)).ToArray();
        var session = new GatewayPresenceSession(4, Guid.NewGuid(), 1, Guid.NewGuid());
        var chunks = ServiceSnapshotSerializer.CreateChunks(new(Guid.NewGuid(), ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Complete, observed, services), session, "1.0");

        chunks.Should().OnlyContain(chunk => (int)chunk.Status == (int)ServiceCollectionStatus.Partial);
        chunks.SelectMany(chunk => chunk.Services).Should().HaveCountLessThan(services.Length);
        chunks.Count.Should().BeLessThanOrEqualTo(ClientServicesLimits.MaximumChunks);
        chunks.Sum(chunk => ServiceSnapshotSerializer.CreateEnvelope(chunk, session, "1.0", ulong.MaxValue).CalculateSize()).Should().BeLessThanOrEqualTo(ClientServicesLimits.MaximumInventoryBytes);
    }

    [Fact]
    public void EmptyUnsupportedResult_IsOneFinalBoundedChunk()
    {
        var session = new GatewayPresenceSession(4, Guid.NewGuid(), 1, Guid.NewGuid());
        var chunks = ServiceSnapshotSerializer.CreateChunks(new(Guid.NewGuid(), ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Unsupported, DateTimeOffset.UtcNow, [], "platform-unsupported"), session, "1.0");
        chunks.Should().ContainSingle();
        chunks[0].IsFinal.Should().BeTrue();
        chunks[0].Services.Should().BeEmpty();
        ((int)chunks[0].Status).Should().Be((int)ServiceCollectionStatus.Unsupported);
    }
}
