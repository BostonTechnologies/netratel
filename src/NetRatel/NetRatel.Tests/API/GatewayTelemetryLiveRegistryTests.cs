using FluentAssertions;
using NetRatel.API.Realtime;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GatewayTelemetryLiveRegistryTests
{
    [Fact]
    public async Task Subscription_IsTenantIsolated_AndPublishesIndependentModeUpdates()
    {
        var registry = new GatewayTelemetryLiveRegistry();
        var agent = Guid.NewGuid();
        var client = new ClientKey(4, agent);
        var otherTenant = new ClientKey(5, agent);
        await using var subscription = registry.Subscribe(client);

        registry.PublishAccepted(Snapshot(otherTenant, 1, 1));
        registry.PublishMode(client, new GatewayTelemetryLiveMode("Live", true, true, true, 1000, "v2", 4, DateTimeOffset.UtcNow.AddMinutes(1), "interactive-viewer"));

        var update = await subscription.Reader.ReadAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        update.Snapshot.Should().BeNull();
        update.Mode!.InteractiveEffective.Should().BeTrue();
        registry.GetLatest(client).Should().BeNull();
        registry.GetLatest(otherTenant).Should().BeNull();
    }

    [Fact]
    public async Task StaleSnapshot_DoesNotReplaceLatestAcceptedSnapshot()
    {
        var registry = new GatewayTelemetryLiveRegistry();
        var client = new ClientKey(4, Guid.NewGuid());
        await using var subscription = registry.Subscribe(client);
        registry.PublishAccepted(Snapshot(client, 3, 5));
        registry.PublishAccepted(Snapshot(client, 3, 4));

        registry.GetLatest(client)!.Snapshot.Sequence.Should().Be(5);
    }

    [Fact]
    public async Task ReleasingFinalSubscription_RemovesProcessLocalState()
    {
        var registry = new GatewayTelemetryLiveRegistry();
        var client = new ClientKey(4, Guid.NewGuid());
        var subscription = registry.Subscribe(client);
        registry.PublishAccepted(Snapshot(client, 1, 1));

        await subscription.DisposeAsync();

        registry.GetLatest(client).Should().BeNull();
        registry.GetMode(client).Should().BeNull();
    }

    [Fact]
    public async Task BoundedSubscription_CoalescesBurstAndRetainsTheNewestSnapshot()
    {
        var registry = new GatewayTelemetryLiveRegistry();
        var client = new ClientKey(4, Guid.NewGuid());
        await using var subscription = registry.Subscribe(client);
        for (ulong sequence = 1; sequence <= 1000; sequence++)
        {
            registry.PublishAccepted(Snapshot(client, 1, sequence));
        }

        GatewayTelemetryLiveUpdate? latest = null;
        while (subscription.Reader.TryRead(out var update)) latest = update;

        latest.Should().NotBeNull();
        latest!.Snapshot!.Snapshot.Sequence.Should().Be(1000);
        registry.GetLatest(client)!.Snapshot.Sequence.Should().Be(1000);
    }

    private static TelemetrySnapshot Snapshot(ClientKey client, long epoch, ulong sequence) => new(
        client, epoch, sequence, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, [], [], null, "akka", true);
}
