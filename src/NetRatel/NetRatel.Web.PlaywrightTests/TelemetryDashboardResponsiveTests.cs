using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlaywrightCollection
{
    public const string Name = "playwright-telemetry-fixture";
}

[Collection(PlaywrightCollection.Name)]
public sealed class TelemetryDashboardResponsiveTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private TelemetryFixtureHost? _fixture;

    [Theory]
    [InlineData(1680, 900, "desktop-wide")]
    [InlineData(1440, 900, "desktop")]
    [InlineData(768, 1024, "tablet")]
    [InlineData(390, 844, "mobile")]
    [InlineData(360, 800, "mobile-narrow")]
    public async Task AuthenticatedFixture_RendersResponsiveTelemetryGeometry(int width, int height, string viewportName)
    {
        var browser = _browser ?? throw new InvalidOperationException("Playwright browser was not initialized.");
        var fixture = _fixture ?? throw new InvalidOperationException("Telemetry fixture was not initialized.");
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height }
        });
        var page = await context.NewPageAsync();
        try
        {
            var response = await page.GotoAsync($"{fixture.BaseAddress}/telemetry", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 10_000 });
            Assert.NotNull(response);
            Assert.True(response.Ok, $"Authenticated fixture returned HTTP {response.Status}.");
            await page.GetByTestId("fixture-authenticated-user").WaitForAsync();
            await page.GetByTestId("telemetry-dashboard").WaitForAsync();
            await AssertNoBlazorErrorAsync(page);

            var grid = page.GetByTestId("telemetry-metric-grid");
            var gridBounds = (await GetBoundsAsync(grid)).Single();
            var gridColumns = await grid.EvaluateAsync<string>("element => getComputedStyle(element).gridTemplateColumns");
            Assert.NotEqual("none", gridColumns);
            var cards = await GetBoundsAsync(grid.Locator("[data-testid^='telemetry-metric-card-']"));
            var charts = await GetBoundsAsync(grid.Locator(".telemetry-sparkline"));
            Assert.Equal(3, cards.Length);
            Assert.Equal(3, charts.Length);
            Assert.Equal("rgb(210, 118, 85)", await grid.Locator(".telemetry-sparkline polyline").First.EvaluateAsync<string>("element => getComputedStyle(element).stroke"));
            Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth"), $"{viewportName} viewport has horizontal overflow.");
            Assert.True(await page.GetByTestId("close-telemetry-dashboard").IsVisibleAsync(), "The 44px close target must remain reachable.");

            if (width >= 1000)
            {
                Assert.All(cards, card => Assert.True(card.Width >= gridBounds.Width * .28, $"{viewportName} primary card did not consume one third of the grid."));
                Assert.True(cards.Max(card => card.Height) - cards.Min(card => card.Height) <= cards.Max(card => card.Height) * .10, $"{viewportName} primary card heights differ by more than 10%.");
                Assert.All(charts, chart =>
                {
                    Assert.True(chart.Width >= cards.Min(card => card.Width) * .75, $"{viewportName} sparkline is too narrow.");
                    Assert.True(chart.Height >= 120, $"{viewportName} sparkline is too short.");
                });
                Assert.True(cards.Max(card => card.Right) - cards.Min(card => card.Left) >= gridBounds.Width * .90, $"{viewportName} metric row does not use the available width.");
            }
            else if (width >= 600)
            {
                Assert.True(Math.Abs(cards[0].Top - cards[1].Top) <= 1, $"Tablet CPU and memory cards should share the first row. CPU={cards[0].Top}, Memory={cards[1].Top}, columns={gridColumns}.");
                Assert.True(cards[2].Top > cards[0].Top, $"Tablet network card should deliberately occupy the second row. CPU={cards[0].Top}, Network={cards[2].Top}.");
                Assert.True(cards[2].Width >= gridBounds.Width * .95, "Tablet network card should span the available grid width.");
                Assert.All(charts, chart => Assert.True(chart.Height >= 120, "Tablet charts must remain useful."));
            }
            else
            {
                Assert.True(cards[0].Top < cards[1].Top && cards[1].Top < cards[2].Top, "Mobile cards must render in one column.");
                Assert.All(cards, card => Assert.True(card.Width >= width * .84, "Mobile cards should use nearly all available content width."));
                Assert.All(charts, chart => Assert.True(chart.Height >= 110, "Mobile charts must remain readable."));
            }

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine("TestResults", "playwright", $"telemetry-fixture-{viewportName}-{width}x{height}.png"),
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
        _fixture = await TelemetryFixtureHost.StartAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        if (_fixture is not null) await _fixture.DisposeAsync();
    }

    private static async Task<Bounds[]> GetBoundsAsync(ILocator locator) =>
        await locator.EvaluateAllAsync<Bounds[]>("elements => elements.map(element => { const rect = element.getBoundingClientRect(); return { left: rect.left, right: rect.right, top: rect.top, width: rect.width, height: rect.height }; })");

    private static async Task AssertNoBlazorErrorAsync(IPage page)
    {
        var error = page.Locator("#blazor-error-ui");
        if (await error.IsVisibleAsync())
        {
            throw new Xunit.Sdk.XunitException($"Blazor error UI is visible: {await error.InnerTextAsync()}");
        }
    }

    private sealed class Bounds
    {
        public float Left { get; set; }
        public float Right { get; set; }
        public float Top { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }
}
