using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class TelemetryShadowScopeAuditTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public void GatewayTelemetryPublisher_RemainsIndependentOfTheLegacyClientService()
    {
        var client = Read("src/NetRatel/NetRatel.Client/Service/Gateway/AgentGatewayTelemetryShadowPublisher.cs");

        client.Should().Contain("AgentTelemetryGatewayV2");
        client.Should().Contain("GatewayTelemetrySnapshotCollector");
        client.Should().NotContain("using Spacetime");
        File.Exists(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Client/Service/Telemetry/ClientTelemetryService.cs"))
            .Should().BeFalse();
    }

    [Fact]
    public void Phase2GatewayContract_ContainsTelemetryOnly()
    {
        var proto = Read("src/NetRatel/NetRatel.AgentGateway.Contracts/Protos/agent_gateway.proto");
        var start = proto.IndexOf("service AgentTelemetryGateway", StringComparison.Ordinal);
        var end = proto.IndexOf('}', start) + 1;
        var telemetrySection = proto[start..end];

        telemetrySection.Should().Contain("PublishTelemetry");
        telemetrySection.Should().Contain("TelemetryFrame");
        telemetrySection.Should().NotContain("Command");
        telemetrySection.Should().NotContain("Job");
        telemetrySection.Should().NotContain("Terminal");
        telemetrySection.Should().NotContain("FileBrowser");
        telemetrySection.Should().NotContain("RemoteSupport");
        telemetrySection.Should().NotContain("RemoteDesktop");
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
