using FluentAssertions;
using NetRatel.API.Realtime;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GatewayTelemetryCompatibilityRegistryTests
{
    [Fact]
    public void Upsert_MapsGatewaySnapshotToTheExistingV1DtoAndKeepsBoundedHistory()
    {
        var registry = new GatewayTelemetryCompatibilityRegistry();
        var agentId = Guid.NewGuid();
        var observedAt = DateTimeOffset.UtcNow;

        for (var index = 0; index < 61; index++)
        {
            registry.Upsert(CreateSnapshot(agentId, observedAt.AddSeconds(index), index));
        }

        var snapshot = registry.GetSnapshot(agentId.ToString("D"));

        snapshot.Should().NotBeNull();
        snapshot!.ClientIdentity.Should().Be(agentId.ToString("D"));
        snapshot.Cpu!.UsagePercent.Should().Be(60);
        snapshot.Memory!.TotalMb.Should().Be(1024);
        snapshot.Disks.Single().Scope.Should().Be("/");
        snapshot.Networks.Single().RxBytesPerSec.Should().Be(120);
        snapshot.Health!.AgentVersion.Should().Be("0.4.102");
        snapshot.CpuHistory.Should().HaveCount(60);
        snapshot.CpuHistory.First().Value.Should().Be(1);
        snapshot.NetworkRxHistory.Last().Value.Should().Be(120);
    }

    [Fact]
    public async Task StreamAsync_EmitsGatewayCompatibilitySnapshots()
    {
        var registry = new GatewayTelemetryCompatibilityRegistry();
        var agentId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = registry.StreamAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        registry.Upsert(CreateSnapshot(agentId, DateTimeOffset.UtcNow, 42));

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.ClientIdentity.Should().Be(agentId.ToString("D"));
        enumerator.Current.Cpu!.UsagePercent.Should().Be(42);
    }

    private static TelemetrySnapshot CreateSnapshot(Guid agentId, DateTimeOffset observedAt, int usage) => new(
        new ClientKey(7, agentId),
        ConnectionEpoch: 1,
        Sequence: checked((ulong)usage + 1),
        ObservedAtUtc: observedAt,
        ReceivedAtUtc: observedAt,
        Cpu: new TelemetryCpu(usage, 0.5, 12),
        Memory: new TelemetryMemory(1024, 512, 512, 50),
        Disks: [new TelemetryDisk("/", 100, 25, 75, 25)],
        Networks: [new TelemetryNetwork("eth0", usage * 2, usage * 3)],
        TransportHealth: new TelemetryTransportHealth(3600, "0.4.102", "Linux", observedAt),
        Source: "akka",
        IsAuthoritative: true);
}
