using Microsoft.Playwright;
using System.Text.Json;

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
        try
        {
            // Use the same bounded cold-start allowance as the existing client-management fixture.
            await page.GotoAsync(fixture.BaseAddress + "/clients/mgmt", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
            await page.GetByTestId("clients-mgmt-interactive").WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 90_000 });
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
        catch
        {
            await CaptureAsync(page, "activity-failure");
            var state = await page.EvaluateAsync<JsonElement>("""
                () => ({
                  readyState: document.readyState,
                  shell: !!document.querySelector('[data-testid="app-main-content"]'),
                  interactive: !!document.querySelector('[data-testid="clients-mgmt-interactive"]'),
                  live: document.querySelector('[data-testid="activity-live-status"]')?.textContent,
                  table: document.querySelector('[data-testid="attempt-table"]')?.textContent?.slice(0,3000),
                  expandedGroups: document.querySelectorAll('button.mud-table-row-expander[aria-expanded="true"]').length,
                  focusedTestId: document.activeElement?.getAttribute('data-testid'),
                  resources: performance.getEntriesByType('resource').slice(-30).map(e => ({
                    path: new URL(e.name).origin + new URL(e.name).pathname, duration: e.duration, responseEnd: e.responseEnd
                  }))
                })
                """);
            var root = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("work/browser-evidence");
            await File.WriteAllTextAsync(Path.Combine(root, "activity", "activity-failure.json"),
                JsonSerializer.Serialize(new { state, errors, serverDiagnostics = fixture.StartupServerDiagnostics.TakeLast(20).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
    }

    private static async Task CaptureAsync(IPage page, string name)
    {
        var root = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("work/browser-evidence");
        var destination = Path.Combine(root, "activity");
        Directory.CreateDirectory(destination);
        await page.ScreenshotAsync(new() { Path = Path.Combine(destination, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
    }
}
