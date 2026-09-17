using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTelemetryGatewaySessionRegistryTests
{
    [Fact]
    public async Task CapableLateSession_ReceivesCurrentPolicy_AndNewerPolicyWins()
    {
        var demand = CreateDemand();
        var registry = new AgentTelemetryGatewaySessionRegistry(demand, new GatewayTelemetryLiveRegistry());
        var client = new ClientKey(12, Guid.NewGuid());
        var firstViewer = demand.Acquire(client, 3000);
        await using var session = registry.Register(client, Guid.NewGuid(), 1, true, "0.4.130-rc.1");
        await using var enumerator = session.ReadOutboundAsync(CancellationToken.None).GetAsyncEnumerator();

        (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        enumerator.Current.TelemetrySamplingPolicy.FastIntervalMilliseconds.Should().Be(3000);

        var secondViewer = demand.Acquire(client, 1000);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        enumerator.Current.TelemetrySamplingPolicy.FastIntervalMilliseconds.Should().Be(1000);
        enumerator.Current.TelemetrySamplingPolicy.Revision.Should().BeGreaterThan(0);

        demand.Release(secondViewer);
        demand.Release(firstViewer);
    }

    [Fact]
    public async Task Replacement_CancelsOldRegistration_AndOldDisposeCannotRemoveNewSession()
    {
        var demand = CreateDemand();
        var registry = new AgentTelemetryGatewaySessionRegistry(demand, new GatewayTelemetryLiveRegistry());
        var client = new ClientKey(12, Guid.NewGuid());
        var oldConnection = Guid.NewGuid();
        var newConnection = Guid.NewGuid();
        await using var oldSession = registry.Register(client, oldConnection, 1, false, "old");
        await using var newSession = registry.Register(client, newConnection, 2, true, "new");

        oldSession.CompletionToken.IsCancellationRequested.Should().BeTrue();
        await oldSession.DisposeAsync();

        newSession.IsCurrent.Should().BeTrue();
        registry.GetStatus(client).Should().Be(new AgentTelemetryGatewaySessionStatus(true, true, "new", 2));
    }

    [Fact]
    public async Task Registration_RejectsStaleFences_AndAllowsAnExactSameFenceReconnect()
    {
        var demand = CreateDemand();
        var registry = new AgentTelemetryGatewaySessionRegistry(demand, new GatewayTelemetryLiveRegistry());
        var client = new ClientKey(12, Guid.NewGuid());
        var connection = Guid.NewGuid();
        await using var first = registry.Register(client, connection, 5, true, "first");
        await using var reconnect = registry.Register(client, connection, 5, true, "reconnect");

        first.CompletionToken.IsCancellationRequested.Should().BeTrue();
        var staleEpoch = () => registry.Register(client, Guid.NewGuid(), 4, true, "stale");
        staleEpoch.Should().Throw<AgentGatewayRegistrationFencedException>();
        var ambiguousFence = () => registry.Register(client, Guid.NewGuid(), 5, true, "ambiguous");
        ambiguousFence.Should().Throw<AgentGatewayRegistrationFencedException>();

        reconnect.IsCurrent.Should().BeTrue();
        registry.GetStatus(client).Should().Be(new AgentTelemetryGatewaySessionStatus(true, true, "reconnect", 5));
    }

    [Fact]
    public async Task BurstPolicyUpdates_CoalesceToLatest_WithoutDiscardingReliableFrames()
    {
        var demand = CreateDemand();
        var registry = new AgentTelemetryGatewaySessionRegistry(demand, new GatewayTelemetryLiveRegistry());
        var client = new ClientKey(12, Guid.NewGuid());
        await using var session = registry.Register(client, Guid.NewGuid(), 1, true, "new");
        await using var enumerator = session.ReadOutboundAsync(CancellationToken.None).GetAsyncEnumerator();

        (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        enumerator.Current.TelemetrySamplingPolicy.Revision.Should().Be(0);

        for (var revision = 1L; revision <= 1000; revision++)
        {
            registry.PublishPolicy(client, Policy(revision));
        }
        await session.EnqueueReliableAsync(new GatewayTelemetryFrame
        {
            Accepted = new TelemetryConnectAccepted { TelemetryAuthority = "akka", MaximumInFlightFrames = 1 }
        }, CancellationToken.None);

        (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        enumerator.Current.PayloadCase.Should().Be(GatewayTelemetryFrame.PayloadOneofCase.Accepted);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        enumerator.Current.TelemetrySamplingPolicy.Revision.Should().Be(1000);
    }

    [Fact]
    public async Task ExactReconnect_RejectsOldPublication_AndProvisionalModeIsInvisible()
    {
        var registry = new AgentTelemetryGatewaySessionRegistry(CreateDemand(), new GatewayTelemetryLiveRegistry());
        var client = new ClientKey(12, Guid.NewGuid());
        var connection = Guid.NewGuid();
        await using var old = registry.Register(client, connection, 5, false, "old");
        await using var current = registry.Register(client, connection, 5, false, "current", provisional: true);
        old.IsCurrent.Should().BeFalse();
        old.CompletionToken.IsCancellationRequested.Should().BeTrue();
        registry.GetStatus(client).Connected.Should().BeFalse();
        var published = false;
        old.TryPublish(() => published = true).Should().BeFalse();
        current.TryPublish(() => published = true).Should().BeFalse();
        published.Should().BeFalse();
        await old.DisposeAsync();
        current.IsCurrent.Should().BeTrue();
        current.TryActivate().Should().BeTrue();
        current.TryPublish(() => published = true).Should().BeTrue();
        published.Should().BeTrue();
    }

    [Fact]
    public async Task FailedProvisionalReplacement_ClearsPreviousLiveMode()
    {
        var live = new GatewayTelemetryLiveRegistry();
        var registry = new AgentTelemetryGatewaySessionRegistry(CreateDemand(), live);
        var client = new ClientKey(12, Guid.NewGuid());
        var connection = Guid.NewGuid();
        await using var subscription = live.Subscribe(client);
        await using var old = registry.Register(client, connection, 5, true, "old");
        live.GetMode(client)!.ConnectionState.Should().Be("Live");
        await using var candidate = registry.Register(client, connection, 5, true, "candidate", provisional: true);
        await candidate.DisposeAsync();
        registry.GetStatus(client).Connected.Should().BeFalse();
        live.GetMode(client)!.ConnectionState.Should().Be("Offline");
        old.IsCurrent.Should().BeFalse();
    }

    private static TelemetrySamplingPolicyState Policy(long revision) => new(
        revision,
        1000,
        30,
        true,
        DateTimeOffset.UtcNow.AddMinutes(1),
        "interactive-viewer");

    private static TelemetryInteractiveDemandRegistry CreateDemand() => new(
        TimeProvider.System,
        new ConfigurationBuilder().AddInMemoryCollection().Build());
}
