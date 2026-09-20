using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class AppBarResponsiveTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private AppBarFixtureHost? _fixture;

    [Theory]
    [InlineData(1280, 900, "desktop-1280")]
    [InlineData(1440, 900, "desktop-1440")]
    [InlineData(1680, 900, "desktop-1680")]
    [InlineData(768, 1024, "tablet")]
    [InlineData(390, 844, "phone-390")]
    [InlineData(360, 800, "phone-360")]
    public async Task AppBarFixture_CentersSearchAndPreventsHorizontalOverflow(int width, int height, string viewportName)
    {
        var browser = _browser ?? throw new InvalidOperationException("Playwright browser was not initialized.");
        var fixture = _fixture ?? throw new InvalidOperationException("App-bar fixture was not initialized.");
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height }
        });
        var page = await context.NewPageAsync();
        try
        {
            var response = await page.GotoAsync($"{fixture.BaseAddress}/appbar", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 10_000
            });
            Assert.NotNull(response);
            Assert.True(response.Ok, $"App-bar fixture returned HTTP {response.Status}.");

            var search = page.GetByTestId("global-search");
            await search.WaitForAsync();
            var bounds = await search.EvaluateAsync<Bounds>("element => { const rect = element.getBoundingClientRect(); return { left: rect.left, width: rect.width }; }");
            var searchCenterDistance = Math.Abs((bounds.Left + (bounds.Width / 2)) - (width / 2d));
            var toolbarLayout = await page.Locator(".netratel-appbar-grid").EvaluateAsync<string>(
                "element => `${getComputedStyle(element).display}; ${getComputedStyle(element).gridTemplateColumns}; ${element.getBoundingClientRect().width}`");
            Assert.True(searchCenterDistance <= 1,
                $"{viewportName} search center distance was {searchCenterDistance}; toolbar layout was {toolbarLayout}.");
            Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth"), $"{viewportName} app bar has horizontal overflow.");
            Assert.True(await search.IsVisibleAsync());

            if (width >= 1280)
            {
                Assert.InRange(bounds.Width, 220, 420);
            }
            else
            {
                Assert.True(bounds.Width >= width * .55, "Mobile search should retain a useful, centered target.");
            }

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine("TestResults", "playwright", $"app-bar-{viewportName}-{width}x{height}.png"),
                FullPage = true
            });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    public async ValueTask InitializeAsync()
    {
        _fixture = await AppBarFixtureHost.StartAsync();
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
        public float Left { get; set; }
        public float Width { get; set; }
    }
}

[Route("/appbar")]
public sealed class AppBarFixtureApp : ComponentBase
{
    private static readonly string AppSiteStyles = File.ReadAllText(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web/wwwroot/app-site.css")));

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "html");
        builder.AddAttribute(1, "lang", "en");
        builder.OpenElement(2, "head");
        builder.OpenElement(3, "meta");
        builder.AddAttribute(4, "name", "viewport");
        builder.AddAttribute(5, "content", "width=device-width, initial-scale=1.0");
        builder.CloseElement();
        builder.CloseElement();
        builder.OpenElement(6, "body");
        builder.OpenElement(7, "style");
        builder.AddMarkupContent(8, AppSiteStyles);
        builder.CloseElement();
        builder.OpenElement(9, "header");
        builder.AddAttribute(10, "class", "netratel-app-bar mud-appbar");
        builder.OpenElement(11, "div");
        builder.AddAttribute(12, "class", "mud-toolbar");
        builder.OpenElement(100, "div");
        builder.AddAttribute(101, "class", "netratel-appbar-grid");
        builder.OpenElement(13, "div");
        builder.AddAttribute(14, "class", "netratel-appbar-left");
        builder.OpenElement(15, "button");
        builder.AddAttribute(16, "class", "mud-icon-button");
        builder.AddContent(17, "Menu");
        builder.CloseElement();
        builder.OpenElement(18, "span");
        builder.AddAttribute(19, "class", "netratel-appbar-desktop-actions");
        builder.AddContent(20, "NetRatel");
        builder.CloseElement();
        builder.CloseElement();
        builder.OpenElement(21, "div");
        builder.AddAttribute(22, "class", "netratel-appbar-search-wrap");
        builder.OpenElement(23, "button");
        builder.AddAttribute(24, "class", "netratel-appbar-search");
        builder.AddAttribute(25, "data-testid", "global-search");
        builder.AddContent(26, "Search clients, tasks, and scripts");
        builder.CloseElement();
        builder.CloseElement();
        builder.OpenElement(27, "div");
        builder.AddAttribute(28, "class", "netratel-appbar-actions");
        builder.OpenElement(29, "span");
        builder.AddAttribute(30, "class", "netratel-appbar-desktop-actions");
        builder.AddContent(31, "API  v0.0.999");
        builder.CloseElement();
        builder.OpenElement(32, "button");
        builder.AddAttribute(33, "class", "netratel-appbar-mobile-actions");
        builder.AddContent(34, "More");
        builder.CloseElement();
        builder.CloseElement();
        builder.CloseElement();
        builder.CloseElement();
        builder.CloseElement();
        builder.OpenElement(35, "main");
        builder.AddAttribute(36, "data-testid", "appbar-fixture-content");
        builder.AddContent(37, "Operational workspace");
        builder.CloseElement();
        builder.CloseElement();
        builder.CloseElement();
    }
}

internal sealed class AppBarFixtureHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    private AppBarFixtureHost(WebApplication application, string baseAddress)
    {
        _application = application;
        BaseAddress = baseAddress;
    }

    public string BaseAddress { get; }

    public static async Task<AppBarFixtureHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRazorComponents();

        var application = builder.Build();
        application.UseAntiforgery();
        application.MapRazorComponents<AppBarFixtureApp>();
        await application.StartAsync().ConfigureAwait(false);
        var baseAddress = application.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        return new AppBarFixtureHost(application, baseAddress);
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}
