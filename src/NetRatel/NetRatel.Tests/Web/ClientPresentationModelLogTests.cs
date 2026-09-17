using FluentAssertions;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Models.Clients;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class ClientPresentationModelLogTests
{
    [Fact]
    public void LogsRequireAnOnlineAgentWithTheNegotiatedGatewayCapability()
    {
        CreateClient(online: true, capabilities: []).CanUseLogs.Should().BeFalse();
        CreateClient(online: true, capabilities: []).LogsUnavailableReason.Should().Be("Client upgrade required");
        CreateClient(online: false, capabilities: ["log-gateway"]).CanUseLogs.Should().BeFalse();
        CreateClient(online: false, capabilities: ["log-gateway"]).LogsUnavailableReason.Should().Be("Offline");
        CreateClient(online: true, capabilities: ["log-gateway"]).CanUseLogs.Should().BeTrue();
    }

    [Fact]
    public void FilesRequireAnAdmittedCurrentSessionWhenReadinessIsProjected()
    {
        var inactive = new GatewayFileCapabilityDto(true, true, false, false, "0.5.4-rc.1", [], DateTimeOffset.UtcNow, null, "file_gateway_not_admitted");
        var active = new GatewayFileCapabilityDto(true, true, true, true, "0.5.4-rc.1", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

        var unavailable = CreateClient(online: true, capabilities: ["file-gateway"], inactive);
        unavailable.CanUseFilesystem.Should().BeFalse();
        unavailable.FilesystemUnavailableReason.Should().Be("File gateway is advertised but not admitted");

        var ready = CreateClient(online: true, capabilities: ["file-gateway"], active);
        ready.CanUseFilesystem.Should().BeTrue();
        ready.FilesystemUnavailableReason.Should().Be("Browse files");
    }

    private static ClientPresentationModel CreateClient(
        bool online,
        IReadOnlyList<string> capabilities,
        GatewayFileCapabilityDto? file = null) => new(
        8,
        Guid.NewGuid(),
        "Agent",
        "agent.example.test",
        "Tenant 8",
        "Linux",
        "x64",
        online,
        true,
        DateTimeOffset.UtcNow,
        "0.4.134-rc.1",
        capabilities,
        null,
        null,
        file);
}
