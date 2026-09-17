using FluentAssertions;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentControlSessionRegistryTests
{
    [Fact]
    public async Task RequestPingAsync_Routes_A_Fenced_Ping_And_Uses_Server_Receipt_Time()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        using var registration = registry.Register(client, Guid.NewGuid(), connectionEpoch: 4);

        var ping = registry.RequestPingAsync(client, TimeSpan.FromSeconds(1), CancellationToken.None);
        var frame = await registration.Reader.ReadAsync();

        frame.PingRequest.Should().NotBeNull();
        Guid.TryParse(frame.PingRequest.RequestId, out var requestId).Should().BeTrue();
        registration.TryCompletePing(requestId, DateTimeOffset.UtcNow).Should().BeTrue();

        var result = await ping;
        result.Client.Should().Be(client);
        result.RequestId.Should().Be(requestId);
        result.ReceivedAtUtc.Should().BeOnOrAfter(result.SentAtUtc);
        result.RoundTripTime.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        registry.TryGetLatestPing(client, out var latest).Should().BeTrue();
        latest.RequestId.Should().Be(requestId);
        latest.ReceivedAtUtc.Should().Be(result.ReceivedAtUtc);
    }

    [Fact]
    public async Task RequestPingAsync_Rejects_Agent_Without_An_Active_Control_Session()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentControlSessionRegistry(TimeProvider.System);

        var action = () => registry.RequestPingAsync(client, TimeSpan.FromSeconds(1), CancellationToken.None);

        await action.Should().ThrowAsync<AgentControlSessionUnavailableException>();
        registry.TryGetLatestPing(client, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Register_RejectsStaleFences_AndAnOldDisposeCannotRemoveTheCurrentSession()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var first = registry.Register(client, Guid.NewGuid(), connectionEpoch: 4);
        var currentConnection = Guid.NewGuid();
        using var current = registry.Register(client, currentConnection, connectionEpoch: 5);

        var staleEpoch = () => registry.Register(client, Guid.NewGuid(), connectionEpoch: 4);
        staleEpoch.Should().Throw<AgentGatewayRegistrationFencedException>();
        var ambiguousFence = () => registry.Register(client, Guid.NewGuid(), connectionEpoch: 5);
        ambiguousFence.Should().Throw<AgentGatewayRegistrationFencedException>();

        first.Dispose();
        var ping = registry.RequestPingAsync(client, TimeSpan.FromSeconds(1), CancellationToken.None);
        var frame = await current.Reader.ReadAsync();
        Guid.TryParse(frame.PingRequest.RequestId, out var requestId).Should().BeTrue();
        current.TryCompletePing(requestId, DateTimeOffset.UtcNow).Should().BeTrue();
        (await ping).Client.Should().Be(client);
    }
    [Fact]
    public async Task ExactReconnect_RejectsOldPingResponse_AndLateDisposePreservesReplacement()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var client = new ClientKey(71, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var old = registry.Register(client, connection, 5);
        var oldPing = registry.RequestPingAsync(client, TimeSpan.FromSeconds(5), CancellationToken.None);
        var oldRequest = await old.Reader.ReadAsync();
        using var current = registry.Register(client, connection, 5);
        old.IsCurrent.Should().BeFalse();
        old.CompletionToken.IsCancellationRequested.Should().BeTrue();
        current.RegistrationId.Should().NotBe(old.RegistrationId);
        old.Dispose();
        current.IsCurrent.Should().BeTrue();
        old.TryCompletePing(Guid.Parse(oldRequest.PingRequest.RequestId), DateTimeOffset.UtcNow).Should().BeFalse();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldPing);

        var ping = registry.RequestPingAsync(client, TimeSpan.FromSeconds(5), CancellationToken.None);
        var request = await current.Reader.ReadAsync();
        var requestId = Guid.Parse(request.PingRequest.RequestId);
        old.TryCompletePing(requestId, DateTimeOffset.UtcNow).Should().BeFalse();
        ping.IsCompleted.Should().BeFalse();
        current.TryCompletePing(requestId, DateTimeOffset.UtcNow).Should().BeTrue();
        (await ping).RequestId.Should().Be(requestId);
    }

    [Fact]
    public async Task ProvisionalRegistration_CannotDispatchPingUntilActivated()
    {
        var registry = new AgentControlSessionRegistry(TimeProvider.System);
        var client = new ClientKey(71, Guid.NewGuid());
        using var candidate = registry.Register(client, Guid.NewGuid(), 5, provisional: true);
        await Assert.ThrowsAsync<AgentControlSessionUnavailableException>(() =>
            registry.RequestPingAsync(client, TimeSpan.FromSeconds(5), CancellationToken.None));
        candidate.Activate().Should().BeTrue();
        var ping = registry.RequestPingAsync(client, TimeSpan.FromSeconds(5), CancellationToken.None);
        var frame = await candidate.Reader.ReadAsync();
        candidate.TryCompletePing(Guid.Parse(frame.PingRequest.RequestId), DateTimeOffset.UtcNow).Should().BeTrue();
        await ping;
    }
}
