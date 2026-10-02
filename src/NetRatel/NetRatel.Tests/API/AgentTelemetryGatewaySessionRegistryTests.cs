using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.Application.Presence;
using Xunit;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Tests.API;

public sealed class AgentTelemetryGatewaySessionRegistryTests
{
    [Fact]
    public async Task ServicesKeysetPagesVisitEveryActiveRegistrationAndExcludeProvisionalOrUnsupportedClients()
    {
        var registry = new AgentTelemetryGatewaySessionRegistry(CreateDemand(), new GatewayTelemetryLiveRegistry());
        var clients = Enumerable.Range(0, 257).Select(_ => new ClientKey(12, Guid.NewGuid()))
            .OrderBy(client => client.AgentId).ToArray();
        var registrations = new List<AgentTelemetryGatewaySessionRegistration>();
        try
        {
            foreach (var client in clients)
                registrations.Add(registry.Register(client, Guid.NewGuid(), 1, false, "services", false, true));
            registrations.Add(registry.Register(new ClientKey(12, Guid.NewGuid()), Guid.NewGuid(), 1, false, "legacy"));
            registrations.Add(registry.Register(new ClientKey(12, Guid.NewGuid()), Guid.NewGuid(), 1, false, "pending", true, true));

            var first = registry.GetServicesSessions(128);
            first.Items.Select(item => item.Client).Should().Equal(clients.Take(128));
            first.NextCursor.Should().Be(clients[127]);

            // Replacing and later disposing a visited stream cannot delete its
            // active replacement from the indexed registration set.
            var old = registrations[0];
            var replacement = registry.Register(clients[0], Guid.NewGuid(), 2, false, "replacement", false, true);
            registrations.Add(replacement);
            await old.DisposeAsync();
            registry.GetServicesSessions(1).Items.Single().RegistrationId.Should().Be(replacement.RegistrationId);

            var second = registry.GetServicesSessions(128, first.NextCursor);
            var final = registry.GetServicesSessions(128, second.NextCursor);
            second.Items.Select(item => item.Client).Should().Equal(clients.Skip(128).Take(128));
            final.Items.Select(item => item.Client).Should().Equal(clients.Skip(256));
            final.NextCursor.Should().BeNull();
            first.Items.Concat(second.Items).Concat(final.Items).Select(item => item.Client)
                .Should().OnlyHaveUniqueItems().And.HaveCount(257);
            registry.GetServicesSessions(128, clients[^1]).Items.Should().BeEmpty();
        }
        finally
        {
            foreach (var registration in registrations) await registration.DisposeAsync();
        }
    }

    [Fact]
    public async Task ServicesPoliciesCoalesceWithoutLosingRefreshOrReliableAcknowledgements()
    {
        var registry = new AgentTelemetryGatewaySessionRegistry(CreateDemand(), new GatewayTelemetryLiveRegistry());
        var client = new ClientKey(12, Guid.NewGuid());
        await using var session = registry.Register(client, Guid.NewGuid(), 1, false, "services", false, true);
        var refresh = Guid.NewGuid();
        var policy = new ClientServiceWatchPolicyDto(1, ["synthetic.service"], 30, 900, DateTimeOffset.UtcNow.AddMinutes(1), refresh);
        registry.TryPublishServicesPolicy(client, policy).Should().BeTrue();
        for (ulong revision = 2; revision <= 1000; revision++)
            registry.TryPublishServicesPolicy(client, policy with { Revision = revision, RefreshRequestId = null }).Should().BeTrue();
        await session.EnqueueReliableAsync(new GatewayTelemetryFrame { SnapshotAccepted = new() { AcceptedSequence = 3, AvailableCredits = 1 } }, CancellationToken.None);
        await using var reader = session.ReadOutboundAsync(CancellationToken.None).GetAsyncEnumerator();
        (await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        reader.Current.SnapshotAccepted.AcceptedSequence.Should().Be(3);
        (await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        reader.Current.ServiceWatchPolicy.Revision.Should().Be(1000);
        reader.Current.ServiceWatchPolicy.RefreshRequestId.Should().Be(refresh.ToString("D"));
        registry.GetStatus(client).SupportsServices.Should().BeTrue();
        registry.TryPublishServicesPolicy(client, policy with { Revision = 999 }).Should().BeFalse();
    }

    [Fact]
    public async Task OldClientsAndOversizedOrUnsafeWatchSelectionAreNeverSentPolicies()
    {
        var registry = new AgentTelemetryGatewaySessionRegistry(CreateDemand(), new GatewayTelemetryLiveRegistry());
        var client = new ClientKey(12, Guid.NewGuid());
        var connection = Guid.NewGuid();
        await using var old = registry.Register(client, connection, 1, false, "old");
        var policy = new ClientServiceWatchPolicyDto(1, [], 30, 900, DateTimeOffset.UtcNow.AddMinutes(1));
        registry.TryPublishServicesPolicy(client, policy).Should().BeFalse();
        await using var current = registry.Register(client, connection, 2, false, "new", false, true);
        registry.TryPublishServicesPolicy(client, policy with { ServiceNames = ["../unsafe.service"] }).Should().BeFalse();
        var names = Enumerable.Range(0, 64).Select(index => new string('界', 245) + index).ToArray();
        registry.TryPublishServicesPolicy(client, policy with { ServiceNames = names }).Should().BeFalse();
        registry.TryPublishServicesPolicy(client, policy with { ServiceNames = ["synthetic.service"] }).Should().BeTrue();
    }

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
        registry.GetStatus(client).Should().Be(new AgentTelemetryGatewaySessionStatus(true, true, "new", 2)
        { ConnectionId = newConnection, RegistrationId = newSession.RegistrationId });
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
        registry.GetStatus(client).Should().Be(new AgentTelemetryGatewaySessionStatus(true, true, "reconnect", 5)
        { ConnectionId = connection, RegistrationId = reconnect.RegistrationId });
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
