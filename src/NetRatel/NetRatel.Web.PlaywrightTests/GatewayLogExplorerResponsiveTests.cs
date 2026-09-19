using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Services.Clients;
using NetRatel.Web.Themes;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class GatewayLogExplorerResponsiveTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private GatewayLogExplorerFixtureHost? _fixture;

    [Theory]
    [InlineData(1440, 900, "desktop")]
    [InlineData(390, 844, "mobile")]
    public async Task ExplorerFixture_RendersFullViewportWithoutHorizontalOverflow(int width, int height, string viewportName)
    {
        var browser = _browser ?? throw new InvalidOperationException("Playwright browser was not initialized.");
        var fixture = _fixture ?? throw new InvalidOperationException("Log explorer fixture was not initialized.");
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height }
        });
        var page = await context.NewPageAsync();
        try
        {
            var response = await page.GotoAsync(fixture.BaseAddress, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 10_000 });
            Assert.NotNull(response);
            Assert.True(response.Ok, $"Log explorer fixture returned HTTP {response.Status}.");
            await page.GetByTestId("gateway-log-explorer-dialog").WaitForAsync();
            await page.GetByTestId("gateway-log-record").First.WaitForAsync();

            var dialog = page.GetByTestId("gateway-log-explorer-dialog");
            var bounds = await dialog.EvaluateAsync<Bounds>("element => { const rect = element.getBoundingClientRect(); return { top: rect.top, width: rect.width, height: rect.height }; }");
            Assert.True(bounds.Width >= width * .98, $"{viewportName} explorer does not use the viewport width.");
            Assert.True(bounds.Height >= height * .95, $"{viewportName} explorer does not use the viewport height.");
            Assert.True(Math.Abs(bounds.Top) <= 2, $"{viewportName} explorer should begin at the top viewport edge.");
            Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth"), $"{viewportName} explorer has horizontal overflow.");
            Assert.Equal("Live", await page.GetByText("Live", new PageGetByTextOptions { Exact = true }).First.InnerTextAsync());
            Assert.Contains("runtime entry 2", await page.GetByTestId("gateway-log-record").First.InnerTextAsync(), StringComparison.Ordinal);
            Assert.True(await page.GetByTestId("toggle-log-filters").IsVisibleAsync());
            Assert.Equal(0, await page.GetByTestId("log-filter-panel").CountAsync());
            Assert.Contains("Event 42", await page.GetByTestId("gateway-log-record").Nth(1).InnerTextAsync(), StringComparison.Ordinal);
            Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync(), "The Blazor error UI is visible.");

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine("TestResults", "playwright", $"gateway-log-explorer-{viewportName}-{width}x{height}.png"),
                FullPage = true
            });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task Explorer_exposes_a_utc_date_range_and_disables_time_narrowing_until_one_day_is_selected()
    {
        var browser = _browser ?? throw new InvalidOperationException("Playwright browser was not initialized.");
        var fixture = _fixture ?? throw new InvalidOperationException("Log explorer fixture was not initialized.");
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        try
        {
            await page.GotoAsync($"{fixture.BaseAddress}/filters", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 10_000 });
            await page.GetByTestId("gateway-log-record").First.WaitForAsync();

            Assert.True(await page.GetByTestId("log-filter-panel").IsVisibleAsync());
            Assert.Equal(2, await page.GetByTestId("log-date-range").CountAsync());
            Assert.True(await page.GetByTestId("log-date-range").First.IsVisibleAsync());
            Assert.True(await page.GetByTestId("log-from-time").First.IsDisabledAsync());
            Assert.True(await page.GetByTestId("log-to-time").First.IsDisabledAsync());
            Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync(), "The Blazor error UI is visible.");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async ValueTask InitializeAsync()
    {
        _fixture = await GatewayLogExplorerFixtureHost.StartAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        if (_fixture is not null) await _fixture.DisposeAsync();
    }

    private sealed class Bounds
    {
        public float Top { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }
}

[Route("/")]
[Route("/filters")]
public sealed class GatewayLogExplorerFixtureApp : ComponentBase
{
    private static readonly string ExplorerStyles = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "NetRatel.Web.styles.css"));
    private static readonly MudTheme Theme = new NetRatelTheme();
    [Inject] private NavigationManager Navigation { get; set; } = default!;

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
        builder.CloseElement();
        builder.OpenElement(9, "body");
        builder.OpenElement(10, "style");
        builder.AddMarkupContent(11, "html,body{margin:0;min-width:0;background:var(--mud-palette-background);color:var(--mud-palette-text-primary);font-family:system-ui,sans-serif}");
        builder.CloseElement();
        builder.OpenElement(12, "style");
        builder.AddMarkupContent(13, ExplorerStyles);
        builder.CloseElement();
        builder.OpenComponent<MudThemeProvider>(14);
        builder.AddComponentParameter(15, nameof(MudThemeProvider.IsDarkMode), true);
        builder.AddComponentParameter(24, nameof(MudThemeProvider.Theme), Theme);
        builder.CloseComponent();
        builder.OpenComponent<GatewayClientLogExplorerDialog>(16);
        builder.AddComponentParameter(17, nameof(GatewayClientLogExplorerDialog.TenantId), 43);
        builder.AddComponentParameter(18, nameof(GatewayClientLogExplorerDialog.AgentId), GatewayLogExplorerFixtureHost.AgentId);
        builder.AddComponentParameter(19, nameof(GatewayClientLogExplorerDialog.HostLabel), "very-long-client-hostname.example.internal");
        builder.AddComponentParameter(20, nameof(GatewayClientLogExplorerDialog.TenantLabel), "Long Test Tenant");
        builder.AddComponentParameter(21, nameof(GatewayClientLogExplorerDialog.Online), true);
        builder.AddComponentParameter(22, nameof(GatewayClientLogExplorerDialog.Embedded), true);
        builder.AddComponentParameter(23, nameof(GatewayClientLogExplorerDialog.FiltersInitiallyExpanded), Navigation.Uri.EndsWith("/filters", StringComparison.OrdinalIgnoreCase));
        builder.CloseComponent();
        builder.CloseElement();
        builder.CloseElement();
    }
}

internal sealed class GatewayLogExplorerFixtureHost : IAsyncDisposable
{
    public static readonly Guid AgentId = Guid.Parse("2ea5f61a-4c4c-4e1c-9910-416019a4aa63");
    private readonly WebApplication _application;

    private GatewayLogExplorerFixtureHost(WebApplication application, string baseAddress)
    {
        _application = application;
        BaseAddress = baseAddress;
    }

    public string BaseAddress { get; }

    public static async Task<GatewayLogExplorerFixtureHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRazorComponents();
        builder.Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        builder.Services.AddScoped<IGatewayLogApiService, FixtureLogApiService>();
        builder.Services.AddScoped<IGatewayLogLiveStreamService, FixtureLogLiveStreamService>();

        var application = builder.Build();
        application.MapGet("/_content/MudBlazor/MudBlazor.min.css", () => Results.File(ResolveMudBlazorStylesheet(), "text/css"));
        application.UseAntiforgery();
        application.MapRazorComponents<GatewayLogExplorerFixtureApp>();
        await application.StartAsync().ConfigureAwait(false);
        var baseAddress = application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        return new GatewayLogExplorerFixtureHost(application, baseAddress);
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }

    private static string ResolveMudBlazorStylesheet()
    {
        var stylesheet = Path.Combine(AppContext.BaseDirectory, "MudBlazor.min.css");
        return File.Exists(stylesheet)
            ? stylesheet
            : throw new FileNotFoundException("The copied MudBlazor stylesheet required by the log explorer visual fixture was not found.", stylesheet);
    }
}

internal static class GatewayLogExplorerFixtureData
{
    public static readonly IReadOnlyList<GatewayLogSourceDescriptorDto> Sources =
    [
        new("netratel-runtime", "runtime", "NetRatel Client Logs", "windows", true, null, true, true, true, ["time", "severity", "prefix", "provider", "event-id", "text"])
    ];

    public static readonly GatewayLogPageDto Page = new(
    [
        new("1", 1, DateTimeOffset.Parse("2026-08-23T13:06:45Z"), "Information", "netratel-runtime", "Agent", "Gateway", "NetRatel.Client", 42, 1, "fixture-client", "runtime entry 1", null, false),
        new("2", 2, DateTimeOffset.Parse("2026-08-23T13:06:46Z"), "Warning", "netratel-runtime", "Agent", "Gateway", "NetRatel.Client", null, null, "fixture-client", "runtime entry 2", null, false)
    ], "2", null, false, 0, false);
}

internal sealed class FixtureLogApiService : IGatewayLogApiService
{
    public Task<IReadOnlyList<GatewayLogSourceDescriptorDto>> GetSourcesAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(GatewayLogExplorerFixtureData.Sources);

    public Task<GatewayLogPageDto> GetHistoryAsync(int tenantId, Guid agentId, string sourceId, string? cursor = null, bool after = false, CancellationToken cancellationToken = default, GatewayLogQueryFilters? filters = null) =>
        Task.FromResult(GatewayLogExplorerFixtureData.Page);
}

internal sealed class FixtureLogLiveStreamService : IGatewayLogLiveStreamService
{
    public Task<GatewayLogLiveSubscription> SubscribeAsync(
        int tenantId,
        Guid agentId,
        string sourceId,
        Func<GatewayLogBatchDto, Task> onBatch,
        CancellationToken cancellationToken = default,
        GatewayLogQueryFilters? filters = null) => SubscribeCoreAsync();

    private static Task<GatewayLogLiveSubscription> SubscribeCoreAsync() =>
        Task.FromResult(new GatewayLogLiveSubscription(GatewayLogExplorerFixtureData.Page, () => ValueTask.CompletedTask));
}
