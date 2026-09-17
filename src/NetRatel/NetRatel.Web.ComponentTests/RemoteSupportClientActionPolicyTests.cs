using FluentAssertions;
using NetRatel.Shared;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Services.RemoteSupport;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class RemoteSupportClientActionPolicyTests
{
    [Theory]
    [InlineData("0.4.1")]
    [InlineData("0.4.82+legacy")]
    [InlineData("99.0.0-preview")]
    public void Policy_IsDrivenByCapabilitiesInsteadOfAgentVersion(string agentVersion)
    {
        var policy = RemoteSupportClientActionPolicy.For(Client(agentVersion, """
            {
              "capabilities": {
                "remoteDesktopAvailable": true,
                "remoteSupportPreLoginSupportLevel": "media_preview",
                "remoteSupportConsoleProviderScaffolded": true,
                "remoteSupportWindowsSessionInventorySupported": true,
                "remoteSupportExplicitTargetingSupported": true
              }
            }
            """));

        policy.ToolsAvailable.Should().BeTrue();
        policy.ConsoleLoginAvailable.Should().BeTrue();
        policy.SessionInventoryAvailable.Should().BeTrue();
        policy.ExplicitUserAssistAvailable.Should().BeTrue();
        policy.LegacyAutomaticAvailable.Should().BeFalse();
    }

    [Fact]
    public void Policy_ExposesLegacyAutomaticActionForFallbackOnlyClient()
    {
        var policy = RemoteSupportClientActionPolicy.For(Client("0.1.0", """
            { "capabilities": { "remoteDesktopAvailable": true } }
            """));

        policy.ToolsAvailable.Should().BeTrue();
        policy.LegacyAutomaticAvailable.Should().BeTrue();
        policy.ConsoleLoginAvailable.Should().BeFalse();
        policy.ExplicitUserAssistAvailable.Should().BeFalse();
    }

    [Fact]
    public void Policy_DoesNotInventSpecializedActionsForMalformedCapabilities()
    {
        var policy = RemoteSupportClientActionPolicy.For(Client("0.4.85", "malformed"));

        policy.ToolsAvailable.Should().BeFalse();
        policy.ConsoleLoginAvailable.Should().BeFalse();
        policy.SessionInventoryAvailable.Should().BeFalse();
        policy.ExplicitUserAssistAvailable.Should().BeFalse();
    }

    private static ClientDto Client(string agentVersion, string clientInfoJson) =>
        new(
            "client-1",
            "client-1",
            "client-1",
            1,
            "Windows client",
            "HOST1",
            "127.0.0.1",
            Online: true,
            Enabled: true,
            LastHeartbeat: DateTimeOffset.UtcNow,
            EnableRundeckLogForwarding: false,
            EnableClientLogForwarding: false,
            Logs: Array.Empty<string>(),
            DetectedOs: "Windows",
            AvailableShells: new[] { "powershell" },
            ClientInfoJson: clientInfoJson,
            AgentVersion: agentVersion,
            LastLatencyMs: 1,
            LastLatencyTimestamp: DateTimeOffset.UtcNow,
            Environment: ClientEnvironment.Dev,
            TenantName: "test",
            Disks: Array.Empty<ClientDiskDto>(),
            Status: new ClientStatusDto(true, true, false, false, false));
}
