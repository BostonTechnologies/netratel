using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Services.Telemetry;
using NetRatel.Web.Themes;

namespace NetRatel.Web.PlaywrightTests;

[Route("/telemetry")]
public sealed class TelemetryFixtureApp : ComponentBase
{
    private static readonly string DashboardStyles = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "NetRatel.Web.styles.css"));
    private static readonly MudTheme Theme = new NetRatelTheme();

    private static readonly GatewayTelemetrySummary Snapshot = new(
        9,
        Guid.Parse("9a0ce4b0-7f3d-4b22-a059-d33b2b3f9ec5"),
        DateTimeOffset.Parse("2026-08-23T13:06:45Z"),
        DateTimeOffset.Parse("2026-08-23T13:06:45Z"),
        new GatewayTelemetryCpu(47.2, 1.11, 221),
        new GatewayTelemetryMemory(32_768, 19_456, 13_312, 59.4),
        [
            new GatewayTelemetryDisk("/var/lib/very-long-container-storage-mount-name", 500, 212, 288, 42.4),
            new GatewayTelemetryDisk("/srv/proxmox-backed-volume", 1000, 850, 150, 85)
        ],
        [
            new GatewayTelemetryNetwork("enp7s0", 4_194_304, 2_097_152),
            new GatewayTelemetryNetwork("docker0", 12_288, 8_192)
        ],
        new GatewayTelemetryTransportHealth(86_400, "0.4.131-rc.1+long-build-metadata-for-visual-truncation", "Linux 6.8", DateTimeOffset.Parse("2026-08-23T13:06:45Z")),
        "Akka",
        true);

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "html");
        builder.AddAttribute(1, "lang", "en");
        builder.OpenElement(2, "head");
        builder.OpenElement(3, "meta");
        builder.AddAttribute(4, "name", "viewport");
        builder.AddAttribute(5, "content", "width=device-width, initial-scale=1.0");
        builder.CloseElement();
        builder.OpenElement(6, "link");
        builder.AddAttribute(7, "rel", "stylesheet");
        builder.AddAttribute(8, "href", "_content/MudBlazor/MudBlazor.min.css");
        builder.CloseElement();
        builder.AddMarkupContent(12, "<style>html,body{margin:0;min-width:0;background:var(--mud-palette-background);color:var(--mud-palette-text-primary);font-family:system-ui,sans-serif}.telemetry-fixture-shell{min-height:100dvh;padding:clamp(12px,2vw,28px);box-sizing:border-box}.telemetry-fixture-toolbar{display:flex;min-height:44px;align-items:center;justify-content:space-between;gap:1rem;margin-bottom:1rem}.telemetry-fixture-toolbar p{margin:0;color:var(--mud-palette-text-secondary);font-size:.82rem}.telemetry-fixture-close{min-width:44px;min-height:44px;border:1px solid var(--mud-palette-divider);border-radius:6px;color:inherit;background:transparent}</style>");
        builder.CloseElement();
        builder.OpenElement(13, "body");
        builder.OpenElement(37, "style");
        builder.AddMarkupContent(38, DashboardStyles);
        builder.CloseElement();
        builder.OpenComponent<MudThemeProvider>(39);
        builder.AddComponentParameter(40, nameof(MudThemeProvider.IsDarkMode), true);
        builder.AddComponentParameter(41, nameof(MudThemeProvider.Theme), Theme);
        builder.CloseComponent();
        builder.OpenElement(14, "main");
        builder.AddAttribute(15, "class", "telemetry-fixture-shell");
        builder.OpenElement(16, "header");
        builder.AddAttribute(17, "class", "telemetry-fixture-toolbar");
        builder.OpenElement(18, "div");
        builder.OpenElement(19, "strong");
        builder.AddContent(20, "long-example-telemetry-hostname.example.invalid");
        builder.CloseElement();
        builder.OpenElement(21, "p");
        builder.AddAttribute(22, "data-testid", "fixture-authenticated-user");
        builder.AddContent(23, "Authenticated visual-fixture-admin");
        builder.CloseElement();
        builder.CloseElement();
        builder.OpenElement(24, "button");
        builder.AddAttribute(25, "type", "button");
        builder.AddAttribute(26, "class", "telemetry-fixture-close");
        builder.AddAttribute(27, "data-testid", "close-telemetry-dashboard");
        builder.AddAttribute(28, "aria-label", "Close telemetry dashboard");
        builder.AddContent(29, "Close");
        builder.CloseElement();
        builder.CloseElement();
        builder.OpenComponent<GatewayTelemetryDashboard>(30);
        builder.AddComponentParameter(31, nameof(GatewayTelemetryDashboard.Snapshot), Snapshot);
        builder.AddComponentParameter(32, nameof(GatewayTelemetryDashboard.CpuHistory), (IReadOnlyList<double>)[28, 44, 48, 53, 47, 61, 55, 47, 52, 46, 47]);
        builder.AddComponentParameter(33, nameof(GatewayTelemetryDashboard.MemoryHistory), (IReadOnlyList<double>)[55, 61, 59, 62, 61, 60, 61, 59, 60, 59, 59.4]);
        builder.AddComponentParameter(34, nameof(GatewayTelemetryDashboard.NetworkRxHistory), (IReadOnlyList<double>)[2048, 4096, 5120, 3072, 8192, 6144, 4096, 9216, 5120]);
        builder.AddComponentParameter(35, nameof(GatewayTelemetryDashboard.NetworkTxHistory), (IReadOnlyList<double>)[1024, 2048, 1536, 4096, 2048, 3072, 6144, 3072, 2048]);
        builder.AddComponentParameter(36, nameof(GatewayTelemetryDashboard.CadenceLabel), "Interactive 1 s");
        builder.CloseComponent();
        builder.CloseElement();
        builder.CloseElement();
        builder.CloseElement();
    }

}
