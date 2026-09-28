using FluentAssertions;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class AkkaGatewayCanarySourceTests
{
    [Fact]
    public void NetRatelClient_DefaultsTo_The_Normal_AkkaPresence_Without_Spacetime_Settings()
    {
        var appSettings = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Client/appsettings.json"));

        appSettings.Should().NotContain("\"Transport\"");
        appSettings.Should().NotContain("\"Endpoint\"");
        appSettings.Should().NotContain("GatewayEnabled");
        appSettings.Should().NotContain("AuthorityEnabled");
        appSettings.Should().NotContain("ShadowEnabled");
        appSettings.Should().Contain("\"Enabled\": false");
        appSettings.Should().NotContain("\"SpaceTime\"");
    }

    [Fact]
    public void Public_Client_Image_Is_Standalone_And_Does_Not_Require_The_Private_Operator_Bundle()
    {
        var dockerfile = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../../docker/client/Dockerfile.public"));

        dockerfile.Should().Contain("Public, standalone NetRatel Client image");
        dockerfile.Should().Contain("NetRatel.Client.csproj");
        dockerfile.Should().NotContain("BuildKit");
        dockerfile.Should().NotContain("external-service");
    }

    [Fact]
    public void Gateway_Client_Has_No_Selectable_Authority_Or_Spacetime_Fallback()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Client/Service/Gateway/AgentGatewayPresenceClient.cs"));

        source.Should().Contain("GatewayWireProtocol.HasAkkaAuthority");
        source.Should().Contain("endpoint.Scheme != Uri.UriSchemeHttps");
        source.Should().Contain("no SpacetimeDB fallback");
        source.Should().NotContain("Only AkkaPresenceCanary is supported.");
    }

    [Fact]
    public void Client_Runtime_Uses_Akka_Without_Transport_Selection()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Client/Program.cs"));

        source.Should().NotContain("Transport:Mode");
        source.Should().NotContain("AkkaPresenceCanary");
        source.Should().NotContain("Unsupported Transport");
        source.Should().NotContain("SpacetimeDbService");
        source.Should().NotContain("ClientSpacetimeSubscriptions");
        source.Should().NotContain("--spacetime-check");
        source.Should().NotContain("#pragma warning disable CS0162");
    }

    [Fact]
    public void Gateway_Telemetry_Uses_V2_With_Rate_Control_And_No_Legacy_Client_Stream()
    {
        var presenceSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Client/Service/Gateway/AgentGatewayPresenceClient.cs"));
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Client/Service/Gateway/AgentGatewayTelemetryPublisher.cs"));

        presenceSource.Should().Contain("hello.Capabilities.Add(\"telemetry-shadow\")");
        source.Should().Contain("AgentTelemetryGatewayV2");
        source.Should().Contain("SnapshotAccepted");
        source.Should().Contain("TelemetrySamplingPolicy");
        source.Should().Contain("telemetry-rate-control-v1");
        source.Should().Contain("ConnectionEpoch = session.ConnectionEpoch");
        source.Should().Contain("ConnectionId = session.ConnectionId.ToString");
        source.Should().NotContain("using Spacetime");
        source.Should().NotContain("AgentTelemetryGateway.AgentTelemetryGatewayClient");
        source.Should().NotContain("PublishTelemetry(");
    }

    [Fact]
    public void Gateway_Telemetry_Frame_Retains_The_Authenticated_Agent_Identity()
    {
        var agentId = Guid.NewGuid();
        var session = new GatewayPresenceSession(3, agentId, 7, Guid.NewGuid());
        var collector = new GatewayTelemetrySnapshotCollector("0.4.95-test", _ => { });

        var frame = collector.CreateFrame(
            session: session,
            sequence: 1,
            observedAtUtc: DateTimeOffset.UtcNow,
            includeSlowMetrics: true);

        frame.ProtocolVersion.Should().Be("1.0");
        frame.TenantId.Should().Be(3);
        frame.ClientId.Should().Be(agentId.ToString("D"));
        frame.ConnectionEpoch.Should().Be(7);
        frame.ConnectionId.Should().Be(session.ConnectionId.ToString("D"));
        frame.Sequence.Should().Be(1);
        frame.TransportHealth!.AgentVersion.Should().Be("0.4.95-test");
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/var/", "/var")]
    [InlineData("  /mnt/data/  ", "/mnt/data")]
    [InlineData("C:\\", "C:\\")]
    [InlineData("C:", "C:\\")]
    public void Gateway_Telemetry_Disk_Scope_Preserves_Root_And_Normalizes_NonRoot_Mounts(string input, string expected)
    {
        GatewayTelemetrySnapshotCollector.NormalizeDiskScope(input).Should().Be(expected);
    }

    [Fact]
    public void Control_Gateway_Is_A_Separate_Reconnecting_NonTerminal_Transport()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Client/Service/Gateway/AgentControlGatewayClient.cs"));

        source.Should().Contain("AgentControlGatewayClient");
        source.Should().Contain("Retrying in");
        source.Should().Contain("PingResponse");
        source.Should().NotContain("using Spacetime");
        source.Should().NotContain("TerminalSessionManager");
    }
}
