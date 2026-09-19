using Akka.Actor;
using FluentAssertions;
using NetRatel.Akka.Telemetry;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class TelemetryActorTests : IAsyncLifetime
{
    private ActorSystem _system = null!;

    public ValueTask InitializeAsync()
    {
        _system = ActorSystem.Create($"telemetry-tests-{Guid.NewGuid():N}");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _system.Terminate();
    }

    [Fact]
    public async Task LatestUpdateWins_WithoutBecomingAuthoritative()
    {
        var client = new ClientKey(7, Guid.NewGuid());
        var actor = _system.ActorOf(TelemetryActor.Props(client));
        var first = CreateSnapshot(client, epoch: 1, sequence: 1, cpuUsage: 10);
        var latest = CreateSnapshot(client, epoch: 1, sequence: 3, cpuUsage: 30);

        (await actor.Ask<TelemetryMessageResult>(new RecordTelemetrySnapshot(first)))
            .Disposition.Should().Be(TelemetryMessageDisposition.Accepted);
        (await actor.Ask<TelemetryMessageResult>(new RecordTelemetrySnapshot(latest)))
            .Disposition.Should().Be(TelemetryMessageDisposition.Accepted);

        var state = await actor.Ask<ClientTelemetryState>(new GetClientTelemetry(client));
        state.Latest.Should().NotBeNull();
        state.Latest!.Sequence.Should().Be(3);
        state.Latest.Cpu!.UsagePercent.Should().Be(30);
        state.Latest.Source.Should().Be("akka-shadow");
        state.Latest.IsAuthoritative.Should().BeFalse();
    }

    [Fact]
    public async Task StaleSequenceAndEpoch_AreRejectedWithoutReplacingLatest()
    {
        var client = new ClientKey(8, Guid.NewGuid());
        var actor = _system.ActorOf(TelemetryActor.Props(client));

        await actor.Ask<TelemetryMessageResult>(new RecordTelemetrySnapshot(
            CreateSnapshot(client, epoch: 2, sequence: 5, cpuUsage: 50)));
        var duplicate = await actor.Ask<TelemetryMessageResult>(new RecordTelemetrySnapshot(
            CreateSnapshot(client, epoch: 2, sequence: 5, cpuUsage: 55)));
        var staleSequence = await actor.Ask<TelemetryMessageResult>(new RecordTelemetrySnapshot(
            CreateSnapshot(client, epoch: 2, sequence: 4, cpuUsage: 40)));
        var staleEpoch = await actor.Ask<TelemetryMessageResult>(new RecordTelemetrySnapshot(
            CreateSnapshot(client, epoch: 1, sequence: 100, cpuUsage: 99)));

        duplicate.Disposition.Should().Be(TelemetryMessageDisposition.Duplicate);
        staleSequence.Disposition.Should().Be(TelemetryMessageDisposition.StaleSequence);
        staleEpoch.Disposition.Should().Be(TelemetryMessageDisposition.StaleConnectionEpoch);
        var state = await actor.Ask<ClientTelemetryState>(new GetClientTelemetry(client));
        state.Latest!.Cpu!.UsagePercent.Should().Be(50);
        state.Latest.Sequence.Should().Be(5);
    }

    [Fact]
    public async Task HigherEpoch_ResetsSequenceFence()
    {
        var client = new ClientKey(9, Guid.NewGuid());
        var actor = _system.ActorOf(TelemetryActor.Props(client));

        await actor.Ask<TelemetryMessageResult>(new RecordTelemetrySnapshot(
            CreateSnapshot(client, epoch: 1, sequence: 100, cpuUsage: 10)));
        var result = await actor.Ask<TelemetryMessageResult>(new RecordTelemetrySnapshot(
            CreateSnapshot(client, epoch: 2, sequence: 1, cpuUsage: 20)));

        result.Disposition.Should().Be(TelemetryMessageDisposition.Accepted);
        result.LastAcceptedSequence.Should().Be(1);
    }

    [Fact]
    public async Task Router_IsolatesClientsAndReportsBoundedDiagnostics()
    {
        var firstClient = new ClientKey(10, Guid.NewGuid());
        var secondClient = new ClientKey(10, Guid.NewGuid());
        var router = _system.ActorOf(ClientTelemetryRouterActor.Props());
        var firstReceivedAt = DateTimeOffset.UtcNow;

        await router.Ask<TelemetryMessageResult>(new RecordTelemetrySnapshot(
            CreateSnapshot(firstClient, epoch: 1, sequence: 1, cpuUsage: 11, receivedAt: firstReceivedAt)));
        await router.Ask<TelemetryMessageResult>(new RecordTelemetrySnapshot(
            CreateSnapshot(secondClient, epoch: 1, sequence: 1, cpuUsage: 22)));
        await router.Ask<TelemetryMessageResult>(new RecordTelemetrySnapshot(
            CreateSnapshot(firstClient, epoch: 1, sequence: 1, cpuUsage: 99)));

        var first = await router.Ask<ClientTelemetryState>(new GetClientTelemetry(firstClient));
        var second = await router.Ask<ClientTelemetryState>(new GetClientTelemetry(secondClient));
        var diagnostics = await router.Ask<ClientTelemetryRouteStatus>(new ProbeClientTelemetryRoute());

        first.Latest!.Cpu!.UsagePercent.Should().Be(11);
        second.Latest!.Cpu!.UsagePercent.Should().Be(22);
        diagnostics.ActiveTelemetryClients.Should().Be(2);
        diagnostics.AcceptedCount.Should().Be(2);
        diagnostics.RejectedCount.Should().Be(1);
        diagnostics.LastUpdateTimestamp.Should().NotBeNull();
        diagnostics.Authority.Should().Be("unavailable");
    }

    private static TelemetrySnapshot CreateSnapshot(
        ClientKey client,
        long epoch,
        ulong sequence,
        double cpuUsage,
        DateTimeOffset? receivedAt = null) =>
        new(
            client,
            epoch,
            sequence,
            DateTimeOffset.UtcNow,
            receivedAt ?? DateTimeOffset.UtcNow,
            new TelemetryCpu(cpuUsage, 0.5, 12),
            new TelemetryMemory(1024, 512, 512, 50),
            [new TelemetryDisk("/", 100, 50, 50, 50)],
            [new TelemetryNetwork("eth0", 1000, 500)],
            new TelemetryTransportHealth(60, "test", "linux", DateTimeOffset.UtcNow));
}
