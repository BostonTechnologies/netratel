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

        appSettings.Should().Contain("\"Mode\": \"AkkaPresence\"");
        appSettings.Should().Contain("\"RequiredPresenceAuthority\": \"akka\"");
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
    public void Gateway_Client_Rejects_NonAkka_Authority_And_Has_No_Spacetime_Fallback()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Client/Service/Gateway/AgentGatewayPresenceClient.cs"));

        source.Should().Contain("Gateway reported authority");
        source.Should().Contain("Gateway changed authority");
        source.Should().Contain("no SpacetimeDB fallback");
        source.Should().NotContain("Only AkkaPresenceCanary is supported.");
    }

    [Fact]
    public void Client_Runtime_Accepts_Normal_And_Legacy_Akka_Transport_During_Rollout()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Client/Program.cs"));

        source.Should().Contain("AkkaPresence\", StringComparison.OrdinalIgnoreCase");
        source.Should().Contain("AkkaPresenceCanary\", StringComparison.OrdinalIgnoreCase");
        source.Should().NotContain("Only AkkaPresenceCanary is supported.");
        source.Should().NotContain("SpacetimeDbService");
        source.Should().NotContain("ClientSpacetimeSubscriptions");
        source.Should().NotContain("--spacetime-check");
        source.Should().NotContain("#pragma warning disable CS0162");
    }

    [Theory]
    [InlineData("akka", true)]
    [InlineData("akka-dev-canary", true)]
    [InlineData("spacetimedb", false)]
    public void Gateway_Authority_Accepts_The_Normal_Label_And_Legacy_Rollout_Label(string authority, bool expected) =>
        GatewayAuthority.IsAkka(authority).Should().Be(expected);

    [Theory]
    [InlineData("akka", "akka-dev-canary", true)]
    [InlineData("akka-dev-canary", "akka", true)]
    [InlineData("spacetimedb", "akka", false)]
    [InlineData("akka", "spacetimedb", false)]
    public void Gateway_Authority_Only_Equates_The_Two_Akka_Labels(
        string reportedAuthority,
        string requiredAuthority,
        bool expected) =>
        GatewayAuthority.MatchesRequired(reportedAuthority, requiredAuthority).Should().Be(expected);

    [Fact]
    public void Gateway_Telemetry_Shadow_Uses_The_Authenticated_Presence_Session_Without_A_Spacetime_Reducer()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Client/Service/Gateway/AgentGatewayTelemetryShadowPublisher.cs"));

        source.Should().Contain("AgentTelemetryGatewayClient");
        source.Should().Contain("ConnectionEpoch = session.ConnectionEpoch");
        source.Should().Contain("ConnectionId = session.ConnectionId.ToString");
        source.Should().NotContain("using Spacetime");
        source.Should().NotContain("PublishAgentTelemetry");
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
