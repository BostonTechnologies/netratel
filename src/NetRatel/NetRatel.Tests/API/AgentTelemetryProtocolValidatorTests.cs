using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTelemetryProtocolValidatorTests
{
    private const int TenantId = 91;
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly AuthenticatedAgentIdentity Identity = new(TenantId, AgentId);

    [Fact]
    public void Validate_AcceptsSupportedTelemetryFields()
    {
        var result = AgentTelemetryProtocolValidator.Validate(
            CreateValidFrame(),
            Identity,
            "1.0",
            maxScopesPerFrame: 64);

        result.Should().Be(AgentFrameValidationResult.Success);
    }

    [Theory]
    [InlineData("protocol", StatusCode.FailedPrecondition)]
    [InlineData("identity", StatusCode.PermissionDenied)]
    [InlineData("connection", StatusCode.InvalidArgument)]
    [InlineData("epoch", StatusCode.InvalidArgument)]
    [InlineData("sequence", StatusCode.InvalidArgument)]
    [InlineData("timestamp", StatusCode.InvalidArgument)]
    [InlineData("empty", StatusCode.InvalidArgument)]
    [InlineData("scope-limit", StatusCode.ResourceExhausted)]
    [InlineData("cpu", StatusCode.InvalidArgument)]
    [InlineData("memory", StatusCode.InvalidArgument)]
    [InlineData("disk", StatusCode.InvalidArgument)]
    [InlineData("network", StatusCode.InvalidArgument)]
    [InlineData("health", StatusCode.InvalidArgument)]
    public void Validate_RejectsUnsupportedFrames(string invalidField, StatusCode expectedStatus)
    {
        var frame = CreateValidFrame();
        var maxScopes = 64;
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
            case "sequence":
                frame.Sequence = 0;
                break;
            case "timestamp":
                frame.ObservedAtUtc = null;
                break;
            case "empty":
                frame.Cpu = null;
                frame.Memory = null;
                frame.Disks.Clear();
                frame.Networks.Clear();
                frame.TransportHealth = null;
                break;
            case "scope-limit":
                maxScopes = 0;
                break;
            case "cpu":
                frame.Cpu.UsagePercent = 101;
                break;
            case "memory":
                frame.Memory.TotalMb = -1;
                break;
            case "disk":
                frame.Disks.Add(frame.Disks[0].Clone());
                break;
            case "network":
                frame.Networks[0].RxBytesPerSec = -1;
                break;
            case "health":
                frame.TransportHealth.UptimeSeconds = -1;
                break;
        }

        var result = AgentTelemetryProtocolValidator.Validate(
            frame,
            Identity,
            "1.0",
            maxScopes);

        result.IsValid.Should().BeFalse();
        result.StatusCode.Should().Be(expectedStatus);
    }

    private static TelemetryFrame CreateValidFrame() =>
        new()
        {
            ProtocolVersion = "1.0",
            TenantId = TenantId,
            ClientId = AgentId.ToString("D"),
            ConnectionId = Guid.NewGuid().ToString("D"),
            ConnectionEpoch = 1,
            Sequence = 1,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Cpu = new TelemetryCpu
            {
                UsagePercent = 20,
                LoadAverage = 0.25,
                ProcessCount = 10
            },
            Memory = new TelemetryMemory
            {
                TotalMb = 1024,
                UsedMb = 512,
                AvailableMb = 512,
                UsagePercent = 50
            },
            TransportHealth = new TelemetryTransportHealth
            {
                UptimeSeconds = 100,
                AgentVersion = "phase2-test",
                OsVersion = "linux",
                LastHeartbeat = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
            },
            Disks =
            {
                new TelemetryDisk
                {
                    Scope = "/",
                    TotalGb = 100,
                    UsedGb = 50,
                    FreeGb = 50,
                    UsagePercent = 50
                }
            },
            Networks =
            {
                new TelemetryNetwork
                {
                    Scope = "eth0",
                    RxBytesPerSec = 1000,
                    TxBytesPerSec = 500
                }
            }
        };
}
