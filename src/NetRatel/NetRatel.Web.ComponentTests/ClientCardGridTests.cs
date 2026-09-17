using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Components.Pages.Clients;
using NetRatel.Web.Services.Clients;
using NetRatel.Web.Models.Clients;
using NetRatel.Web.Services.Telemetry;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientCardGridTests : AsyncBunitContext
{
    private static readonly Guid AgentId = Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46");

    public ClientCardGridTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<IHttpClientFactory, EmptyHttpClientFactory>();
        Services.AddScoped<GatewayClientActionApiService>();
    }

    [Fact]
    public void Renders_Canonical_V2_Actions_And_Reported_Terminal_Inventory()
    {
        var popoverProvider = Render<MudBlazor.MudPopoverProvider>();
        var cut = RenderGrid(online: true, capabilities: ["terminal-gateway", "file-gateway", "remote-support-gateway"],
            terminal: new GatewayTerminalCapabilityDto(true, ["pwsh", "powershell", "bash", "sh", "zsh", "cmd"], true, null, DateTimeOffset.UtcNow));

        cut.Find("button[aria-label='Open terminal']").Click();
        popoverProvider.WaitForAssertion(() =>
        {
            foreach (var shell in new[] { "PowerShell 7", "Windows PowerShell", "Bash", "Shell", "Zsh", "Command Prompt" })
            {
                popoverProvider.Markup.Should().Contain(shell);
            }
        });
        cut.Find("button[aria-label='Open file browser']").Should().NotBeNull();
        cut.Find("button[aria-label='Open remote support']").Should().NotBeNull();
        cut.Find("button[aria-label='Copy host']").Should().NotBeNull();
        cut.FindAll(".client-card").Should().ContainSingle();
        cut.Find(".client-meta-grid").TextContent.Should().Contain("Last heartbeat");
        cut.Find(".client-connectivity-row").TextContent.Should().Contain("IP Address");
        cut.Find(".client-connectivity-row").TextContent.Should().Contain("Network");
        cut.Find(".telemetry-split-grid").Should().NotBeNull();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Disables_Terminal_When_Offline_Or_Capability_Is_Absent(bool online, bool hasCapability)
    {
        var capabilities = hasCapability ? new[] { "terminal-gateway" } : Array.Empty<string>();
        var cut = RenderGrid(online, capabilities, new GatewayTerminalCapabilityDto(true, ["bash"], true, null, DateTimeOffset.UtcNow));

        cut.Find("button[aria-label='Open terminal']").HasAttribute("disabled").Should().BeTrue();
    }

    private IRenderedComponent<ClientCardGrid> RenderGrid(bool online, IReadOnlyList<string> capabilities, GatewayTerminalCapabilityDto terminal) =>
        Render<ClientCardGrid>(parameters => parameters
            .Add(component => component.Clients,
            [new ClientPresentationModel(3, AgentId, "gateway-agent-01", "gateway-agent-01", "NetRatel", "Linux", "x64", online, true,
                DateTimeOffset.UtcNow, "0.4.101", capabilities, terminal,
                new GatewayTelemetrySummary(3, AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    new GatewayTelemetryCpu(37.5, null, null), new GatewayTelemetryMemory(100, 61, 39, 61),
                    [new GatewayTelemetryDisk("/", 80, 20, 60, 25)], [new GatewayTelemetryNetwork("eth0", 1000, 500)], null, "gateway", true))]));

    private sealed class EmptyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri("https://netratel.test") };
    }
}
