using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class TelemetryHistoryResponsiveTests(ClientsManagementBrowserFixture browserFixture)
    : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [Trait("Category", "ManualBrowserAcceptance")]
    [InlineData(1280, 800)]
    [InlineData(390, 844)]
    public async Task Offline_card_history_and_live_tabs_keep_the_clients_route_and_fit(int width, int height)
    {
        await using var host = await ClientsManagementFixtureHost.StartAsync();
        await using var context = await browserFixture.Browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height }, TimezoneId = "Africa/Johannesburg"
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.BaseAddress + "/clients");
        var card = page.GetByTestId("client-card").Filter(new() { HasText = "registered-offline-agent" });
        await card.GetByRole(AriaRole.Button, new() { Name = "Open telemetry dashboard" }).ClickAsync();
        var url = page.Url;
        var close = page.GetByTestId("close-telemetry-dashboard");
        var live = page.GetByRole(AriaRole.Tab, new() { Name = "Live", Exact = true });
        var history = page.GetByRole(AriaRole.Tab, new() { Name = "History", Exact = true });
        await Assertions.Expect(live).ToHaveAttributeAsync("aria-selected", "true");
        await history.PressAsync("Enter");
        await Assertions.Expect(history).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(page.GetByTestId("metrics-collection-status")).ToContainTextAsync("Collection disabled");
        await Assertions.Expect(page.GetByText("This computer is offline", new() { Exact = false })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("metrics-received")).ToContainTextAsync("125 KiB");
        await Assertions.Expect(page.GetByTestId("monitoring-client-filter")).ToHaveCountAsync(0);

        var range = page.GetByLabel("Time range");
        await range.SelectOptionAsync("custom");
        await Assertions.Expect(page.GetByText("Africa/Johannesburg", new() { Exact = true })).ToBeVisibleAsync();
        await close.ScrollIntoViewIfNeededAsync();
        var bounds = await close.BoundingBoxAsync();
        Assert.NotNull(bounds);
        Assert.True(bounds.X >= 0 && bounds.X + bounds.Width <= width);
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth"));
        await live.PressAsync("Enter");
        await Assertions.Expect(live).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(page.GetByTestId("metrics-received")).ToBeHiddenAsync();
        await history.ClickAsync();
        await Assertions.Expect(range).ToHaveValueAsync("custom");
        Assert.Equal(url, page.Url);
        await close.ClickAsync();
        await Assertions.Expect(close).ToBeHiddenAsync();
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }
}
