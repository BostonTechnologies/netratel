using Microsoft.Playwright;
using Xunit;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("category", "manual-integration")]
public sealed class FlowManagementBrowserTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [InlineData(1366,768,"light")]
    [InlineData(390,844,"dark")]
    public async Task Synthetic_server_pages_keep_header_pager_named_actions_and_confirmed_toggle_usable(int width, int height, string theme)
    {
        await using var fixture = await ClientsManagementFixtureHost.StartAsync(); fixture.FlowsData.SeedManagementList();
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height }, ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(20_000);
        await page.GotoAsync(fixture.BaseAddress + "/flows?tenantId=17", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assertions.Expect(page.Locator(".flows-table .flow-row")).ToHaveCountAsync(20);
        var mobile = await page.GetByTestId("mobile-overflow").IsVisibleAsync();
        await page.GetByTestId(mobile ? "mobile-overflow" : "theme-preference-menu").ClickAsync();
        await page.GetByTestId($"{(mobile ? "mobile-theme-option" : "theme-option")}-{theme}").ClickAsync();
        await page.Locator($"html[data-netratel-theme='{theme}']").WaitForAsync();
        var pager = page.Locator(".mud-table-pagination"); await Assertions.Expect(pager).ToBeVisibleAsync();
        await Assertions.Expect(pager).ToContainTextAsync("of 61");
        await Assertions.Expect(page.Locator(".flows-table .flow-row").First).ToContainTextAsync("deliberately long readable name");
        var header = page.Locator(".flows-table thead th").First; var before = (await header.BoundingBoxAsync())!; Assert.InRange(before.Height, 20, 72);
        Assert.InRange((await page.Locator(".flows-table .flow-row").First.BoundingBoxAsync())!.Height, 44, 160);
        await page.Locator(".flows-table .mud-table-container").EvaluateAsync("e => e.scrollTop = e.scrollHeight");
        var after = (await header.BoundingBoxAsync())!; Assert.InRange(Math.Abs(after.Y - before.Y), 0, 2);
        var pagerBounds = (await pager.BoundingBoxAsync())!;
        Assert.True(pagerBounds.Y + pagerBounds.Height <= height + 1, $"Pager bottom {pagerBounds.Y + pagerBounds.Height}, viewport {height}");
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"));
        foreach (var size in new[] { 50,100 })
        {
            await page.Locator(".mud-table-pagination-select").ClickAsync();
            await page.GetByRole(AriaRole.Option, new() { Name = size.ToString(), Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator(".flows-table .flow-row")).ToHaveCountAsync(Math.Min(size,61));
        }
        Assert.Equal(new[] {20,50,100}, fixture.FlowsData.ManagementQueries.Select(query => query.PageSize).Distinct().ToArray());
        var toggle = page.GetByTestId("flow-enabled").First; await toggle.ScrollIntoViewIfNeededAsync();
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-pressed","false");
        await toggle.FocusAsync(); await Assertions.Expect(toggle).ToBeFocusedAsync(); await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.GetByTestId("flow-enabled").First).ToHaveAttributeAsync("aria-pressed","true");
        var delete = page.GetByTestId("flow-delete").First; await delete.ScrollIntoViewIfNeededAsync();
        await Assertions.Expect(delete).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("^Delete "));
        await delete.ClickAsync(); await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("90 days");
        await page.GetByTestId("flow-delete-cancel").ClickAsync(); await Assertions.Expect(page.Locator(".flows-table .flow-row")).ToHaveCountAsync(61);
        await page.GetByTestId("flow-delete").First.ClickAsync(); await page.GetByTestId("flow-delete-confirm").ClickAsync();
        await Assertions.Expect(page.Locator(".flows-table .flow-row")).ToHaveCountAsync(60);
        await Assertions.Expect(pager).ToContainTextAsync("of 60");
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        var evidence = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(),"TestResults","playwright");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence,$"flows-management-{width}-{theme}.png"), FullPage = true });
    }
}
