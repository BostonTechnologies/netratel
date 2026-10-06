using AwesomeAssertions;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GatewayCapabilityReadinessTests
{
    private static readonly ClientKey Client = new(3, Guid.NewGuid());
    private static readonly Guid Connection = Guid.NewGuid();

    [Theory]
    [InlineData(ClientPresenceStatus.Offline, true, true, true)]
    [InlineData(ClientPresenceStatus.Online, false, true, true)]
    [InlineData(ClientPresenceStatus.Online, true, false, true)]
    [InlineData(ClientPresenceStatus.Online, true, true, false)]
    public void RetainedOrStaleRegistrations_AreUnavailableForBothCapabilities(
        ClientPresenceStatus status, bool authoritative, bool sameConnection, bool sameEpoch)
    {
        var presence = Presence(status, authoritative) with
        {
            ConnectionId = sameConnection ? Connection : Guid.NewGuid(),
            ConnectionEpoch = sameEpoch ? 5 : 6
        };
        var terminal = ClientPresenceReadEndpoints.MapTerminal(presence,
            new GatewayTerminalAvailability(Connection, 5, ["sh"], DateTimeOffset.UtcNow, true));
        var file = ClientPresenceReadEndpoints.MapFile(presence,
            new GatewayFileGatewayAvailability(Connection, 5, ["file-stat-v1"], DateTimeOffset.UtcNow));

        terminal.TransportReady.Should().BeFalse();
        terminal.ReadinessReason.Should().NotBeNull();
        file.SessionActive.Should().BeTrue("the active transport fact remains separate from authoritative readiness");
        file.FenceMatchesPresence.Should().BeFalse();
        file.ReadinessReason.Should().NotBeNull();
    }

    [Fact]
    public void OptionalChildLoss_WithdrawsOnlyItsReadiness_AndRestoredCurrentRegistrationBecomesReady()
    {
        var presence = Presence(ClientPresenceStatus.Online, true);
        ClientPresenceReadEndpoints.MapTerminal(presence, null).TransportReady.Should().BeFalse();
        ClientPresenceReadEndpoints.MapFile(presence, null).SessionActive.Should().BeFalse();
        var terminal = ClientPresenceReadEndpoints.MapTerminal(presence,
            new GatewayTerminalAvailability(Connection, 5, ["sh"], DateTimeOffset.UtcNow, true));
        var file = ClientPresenceReadEndpoints.MapFile(presence,
            new GatewayFileGatewayAvailability(Connection, 5, ["file-stat-v1"], DateTimeOffset.UtcNow));
        terminal.TransportReady.Should().BeTrue();
        terminal.ReadinessReason.Should().BeNull();
        file.SessionActive.Should().BeTrue();
        file.FenceMatchesPresence.Should().BeTrue();
        file.ReadinessReason.Should().BeNull();
    }

    [Fact]
    public void MissingOrUnsupportedPresence_NeverProjectsTerminalReadiness()
    {
        var registration = new GatewayTerminalAvailability(Connection, 5, ["sh"], DateTimeOffset.UtcNow, true);
        ClientPresenceReadEndpoints.MapTerminal(null, registration).TransportReady.Should().BeFalse();
        ClientPresenceReadEndpoints.MapTerminal(Presence(ClientPresenceStatus.Online, true) with { Capabilities = [] }, registration)
            .TransportReady.Should().BeFalse();
    }

    private static ClientPresenceSnapshot Presence(ClientPresenceStatus status, bool authoritative) =>
        new(Client, status, 5, Connection, 0, DateTimeOffset.UtcNow, "test", ["terminal-gateway", "file-gateway"],
            null, "akka", authoritative);
}
