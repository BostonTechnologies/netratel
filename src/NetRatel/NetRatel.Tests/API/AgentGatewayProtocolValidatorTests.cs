using FluentAssertions;
using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentGatewayProtocolValidatorTests
{
    [Fact]
    public void ValidateHello_AcceptsAuthenticatedMatchingEnvelope()
    {
        var identity = new AuthenticatedAgentIdentity(7, Guid.NewGuid());
        var frame = CreateHello(identity);

        var result = AgentGatewayProtocolValidator.ValidateHello(frame, identity, "1.0");

        result.IsValid.Should().BeTrue(result.Error);
    }

    [Fact]
    public void ValidateHello_RejectsClientIdentitySpoofing()
    {
        var identity = new AuthenticatedAgentIdentity(7, Guid.NewGuid());
        var frame = CreateHello(identity);
        frame.ClientId = Guid.NewGuid().ToString("D");

        var result = AgentGatewayProtocolValidator.ValidateHello(frame, identity, "1.0");

        result.IsValid.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public void ValidateHeartbeat_RejectsStaleServerEpoch()
    {
        var identity = new AuthenticatedAgentIdentity(7, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var frame = new AgentFrame
        {
            ProtocolVersion = "1.0",
            TenantId = identity.TenantId,
            ClientId = identity.ClientId,
            ConnectionEpoch = 8,
            ConnectionId = connectionId.ToString("D"),
            OperationId = Guid.NewGuid().ToString("D"),
            Sequence = 1,
            Heartbeat = new PresenceHeartbeat()
        };

        var result = AgentGatewayProtocolValidator.ValidateHeartbeat(
            frame,
            identity,
            "1.0",
            connectionId,
            connectionEpoch: 9);

        result.IsValid.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCode.Aborted);
    }

    [Fact]
    public void ValidateHello_RejectsNonPresencePayloadOrUnsupportedVersion()
    {
        var identity = new AuthenticatedAgentIdentity(7, Guid.NewGuid());
        var frame = CreateHello(identity);
        frame.ProtocolVersion = "2.0";

        var result = AgentGatewayProtocolValidator.ValidateHello(frame, identity, "1.0");

        result.IsValid.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    private static AgentFrame CreateHello(AuthenticatedAgentIdentity identity) => new()
    {
        ProtocolVersion = "1.0",
        TenantId = identity.TenantId,
        ClientId = identity.ClientId,
        ConnectionEpoch = 0,
        ConnectionId = Guid.NewGuid().ToString("D"),
        OperationId = Guid.NewGuid().ToString("D"),
        Sequence = 0,
        Hello = new ConnectHello
        {
            AgentVersion = "test",
            LegacySpacetimeIdentity = new string('a', 64)
        }
    };
}
