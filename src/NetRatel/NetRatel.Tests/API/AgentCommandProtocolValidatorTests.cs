using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentCommandProtocolValidatorTests
{
    private const int TenantId = 94;
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly AuthenticatedAgentIdentity Identity = new(TenantId, AgentId);

    [Fact]
    public void Validate_AcceptsTheSupportedCommandLifecycleEnvelope()
    {
        var result = AgentCommandProtocolValidator.Validate(
            CreateValidFrame(),
            Identity,
            "1.0");

        result.Should().Be(AgentFrameValidationResult.Success);
    }

    [Theory]
    [InlineData("protocol", StatusCode.FailedPrecondition)]
    [InlineData("identity", StatusCode.PermissionDenied)]
    [InlineData("connection", StatusCode.InvalidArgument)]
    [InlineData("epoch", StatusCode.InvalidArgument)]
    [InlineData("command", StatusCode.InvalidArgument)]
    [InlineData("correlation", StatusCode.InvalidArgument)]
    [InlineData("version", StatusCode.InvalidArgument)]
    [InlineData("sequence", StatusCode.InvalidArgument)]
    [InlineData("status", StatusCode.InvalidArgument)]
    [InlineData("request-timestamp", StatusCode.InvalidArgument)]
    [InlineData("status-timestamp", StatusCode.InvalidArgument)]
    [InlineData("chronology", StatusCode.InvalidArgument)]
    public void Validate_RejectsUnsupportedCommandFrames(
        string invalidField,
        StatusCode expectedStatus)
    {
        var frame = CreateValidFrame();
        switch (invalidField)
        {
            case "protocol":
                frame.ProtocolVersion = "2.0";
                break;
            case "identity":
                frame.TenantId++;
                break;
            case "connection":
                frame.ConnectionId = Guid.Empty.ToString("D");
                break;
            case "epoch":
                frame.ConnectionEpoch = 0;
                break;
            case "command":
                frame.CommandId = string.Empty;
                break;
            case "correlation":
                frame.CorrelationId = new string('x', 257);
                break;
            case "version":
                frame.Version = 0;
                break;
            case "sequence":
                frame.Sequence = 0;
                break;
            case "status":
                frame.Status = CommandShadowStatus.Unspecified;
                break;
            case "request-timestamp":
                frame.RequestTimestamp = null;
                break;
            case "status-timestamp":
                frame.StatusTimestamp = null;
                break;
            case "chronology":
                frame.StatusTimestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-1));
                break;
        }

        var result = AgentCommandProtocolValidator.Validate(frame, Identity, "1.0");

        result.IsValid.Should().BeFalse();
        result.StatusCode.Should().Be(expectedStatus);
    }

    private static CommandShadowFrame CreateValidFrame()
    {
        var requestTimestamp = DateTimeOffset.UtcNow;
        return new CommandShadowFrame
        {
            ProtocolVersion = "1.0",
            TenantId = TenantId,
            ClientId = AgentId.ToString("D"),
            ConnectionId = Guid.NewGuid().ToString("D"),
            ConnectionEpoch = 1,
            CommandId = "opaque-command-id",
            CorrelationId = "netratel-task-opaque-command-id",
            RequestTimestamp = Timestamp.FromDateTimeOffset(requestTimestamp),
            StatusTimestamp = Timestamp.FromDateTimeOffset(requestTimestamp.AddSeconds(1)),
            Version = 1,
            Sequence = 1,
            Status = CommandShadowStatus.Created
        };
    }
}
