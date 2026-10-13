using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "BrowserAcceptance")]
public sealed class ClientUpdateActivityResponsiveTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>
{
    [Fact]
    public async Task Activity_notification_and_reconnect_refresh_the_visible_page_and_preserve_focus_and_expansion()
    {
        await using var fixture = await ClientsManagementFixtureHost.StartAsync();
        await using var context = await browserFixture.Browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 1366, Height = 768 }, ColorScheme = ColorScheme.Light });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15_000);
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(fixture.BaseAddress + "/clients/mgmt");
        await page.GetByTestId("clients-mgmt-interactive").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await page.Locator(".mud-tab").Filter(new() { HasText = "Activity" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("activity-live-status")).ToContainTextAsync("Live");
        await page.Locator("button.mud-table-row-expander").ClickAsync();
        await Assertions.Expect(page.GetByTestId("attempt-table")).ToContainTextAsync("RolledBack");
        var search = page.GetByTestId("attempt-search");
        await search.FocusAsync();
        fixture.Data.HistoryStatus = "Accepted";
        await fixture.ActivityStream.SendAsync("invalidate");
        await Assertions.Expect(page.GetByTestId("attempt-table")).ToContainTextAsync("Accepted");
        await Assertions.Expect(search).ToBeFocusedAsync();
        await CaptureAsync(page, "activity-live-transition");
        fixture.ActivityStream.Disconnect();
        await Assertions.Expect(page.GetByTestId("activity-live-status")).ToContainTextAsync("Reconnecting");
        fixture.Data.HistoryStatus = "FailedPreActivation";
        await fixture.ActivityStream.SendAsync("ready");
        await Assertions.Expect(page.GetByTestId("attempt-table")).ToContainTextAsync("FailedPreActivation");
        await Assertions.Expect(page.GetByTestId("activity-live-status")).ToContainTextAsync("Live");
        await Assertions.Expect(search).ToBeFocusedAsync();
        await CaptureAsync(page, "activity-reconnect-reconciled");
        Assert.Empty(errors);
    }

    private static async Task CaptureAsync(IPage page, string name)
    {
        var root = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("work/browser-evidence");
        var destination = Path.Combine(root, "activity");
        Directory.CreateDirectory(destination);
        await page.ScreenshotAsync(new() { Path = Path.Combine(destination, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
    }
}
