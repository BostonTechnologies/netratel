using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Services.Telemetry;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class GatewayTelemetryDashboardTests : AsyncBunitContext
{
    public GatewayTelemetryDashboardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
    }

    [Fact]
    public void Renders_ThreeStretchingPrimaryCards_WithLargeMetricHierarchy()
    {
        var cut = RenderDashboard();

        cut.FindAll("[data-testid^='telemetry-metric-card-']").Should().HaveCount(3);
        cut.Find("[data-testid='telemetry-metric-grid']").ClassList.Should().Contain("telemetry-metric-grid");
        cut.Find("[data-testid='telemetry-cpu-value']").ClassList.Should().Contain("telemetry-primary-value");
        cut.FindAll(".telemetry-chart-region").Should().HaveCount(3);
        cut.Markup.Should().Contain("Interactive 1 s");
    }

    [Fact]
    public void Renders_DualSeriesNetwork_AndLongDiskNames_WithoutDroppingData()
    {
        var cut = RenderDashboard();

        cut.Find("[data-testid='telemetry-network-value']").TextContent.Should().Contain("RX").And.Contain("TX");
        cut.FindAll(".telemetry-sparkline").Should().HaveCount(3);
        cut.Find(".telemetry-disk-card strong").TextContent.Should().Contain("/var/lib/very-long-container-storage-mount-name");
    }

    [Fact]
    public void Renders_EmptyNetwork_AndEmptyDiskStates()
    {
        var snapshot = CreateSnapshot(networks: [], disks: []);
        var cut = Render<GatewayTelemetryDashboard>(parameters => parameters
            .Add(component => component.Snapshot, snapshot)
            .Add(component => component.CpuHistory, new[] { 48d, 48d })
            .Add(component => component.MemoryHistory, new[] { 61d, 61d })
            .Add(component => component.NetworkRxHistory, Array.Empty<double>())
            .Add(component => component.NetworkTxHistory, Array.Empty<double>()));

        cut.Markup.Should().Contain("No active interfaces");
        cut.Markup.Should().Contain("No disk telemetry yet.");
        cut.Find("[data-testid='telemetry-network-value']").TextContent.Should().Contain("0 B/s");
    }

    [Theory]
    [InlineData("Interactive 1 s")]
    [InlineData("Standard 5 s — client upgrade required")]
    [InlineData("Reconnecting")]
    public void Renders_TheTruthfulCadenceState(string cadenceLabel)
    {
        var cut = Render<GatewayTelemetryDashboard>(parameters => parameters
            .Add(component => component.Snapshot, CreateSnapshot())
            .Add(component => component.CpuHistory, new[] { 0d, 100d })
            .Add(component => component.MemoryHistory, new[] { 0d, 100d })
            .Add(component => component.NetworkRxHistory, Array.Empty<double>())
            .Add(component => component.NetworkTxHistory, Array.Empty<double>())
            .Add(component => component.CadenceLabel, cadenceLabel));

        cut.Markup.Should().Contain(cadenceLabel);
        cut.FindAll(".telemetry-sparkline polyline").Should().Contain(sparkline => (sparkline.GetAttribute("points") ?? string.Empty).Contains("0.00,30.00 100.00,2.00"));
    }

    private IRenderedComponent<GatewayTelemetryDashboard> RenderDashboard() => Render<GatewayTelemetryDashboard>(parameters => parameters
        .Add(component => component.Snapshot, CreateSnapshot())
        .Add(component => component.CpuHistory, new[] { 28d, 44d, 48d, 53d, 47d })
        .Add(component => component.MemoryHistory, new[] { 55d, 61d, 59d, 62d, 61d })
        .Add(component => component.NetworkRxHistory, new[] { 2048d, 4096d, 5120d, 3072d })
        .Add(component => component.NetworkTxHistory, new[] { 1024d, 2048d, 1536d, 4096d })
        .Add(component => component.CadenceLabel, "Interactive 1 s"));

    private static GatewayTelemetrySummary CreateSnapshot(
        IReadOnlyList<GatewayTelemetryNetwork>? networks = null,
        IReadOnlyList<GatewayTelemetryDisk>? disks = null) => new(
        9,
        Guid.Parse("9a0ce4b0-7f3d-4b22-a059-d33b2b3f9ec5"),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        new GatewayTelemetryCpu(47.2, 1.11, 221),
        new GatewayTelemetryMemory(32_768, 19_456, 13_312, 59.4),
        disks ?? [new GatewayTelemetryDisk("/var/lib/very-long-container-storage-mount-name", 500, 212, 288, 42.4)],
        networks ?? [new GatewayTelemetryNetwork("enp7s0", 4_194_304, 2_097_152)],
        new GatewayTelemetryTransportHealth(86_400, "0.4.131-rc.1+very-long-build-metadata", "Linux 6.8", DateTimeOffset.UtcNow),
        "Akka",
        true);
}
