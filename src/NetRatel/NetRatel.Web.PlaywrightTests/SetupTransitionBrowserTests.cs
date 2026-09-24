using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class SetupTransitionBrowserTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    [Fact]
    public async Task RejectedSetupReturnsToAnActionableFormWithoutStartingReadinessPolling()
    {
        await using var context = await Browser().NewContextAsync();
        var page = await OpenSetupAsync(context);
        await page.EvaluateAsync("() => sessionStorage.setItem('fixture.initializeMode', 'reject')");

        await page.EvaluateAsync("() => window.netratelSetup.initialize()");

        await page.GetByText("Correct the administrator details.").WaitForAsync();
        Assert.False(await page.GetByTestId("setup-initialize").IsDisabledAsync());
        Assert.False(await page.GetByTestId("setup-initializing").IsVisibleAsync());
        Assert.Equal(string.Empty, await page.GetByTestId("setup-password").InputValueAsync());
        Assert.Equal("1", await page.EvaluateAsync<string>(
            "() => sessionStorage.getItem('fixture.initializeCount')"));
        Assert.Equal("0", await page.EvaluateAsync<string>(
            "() => sessionStorage.getItem('fixture.statusCount')"));
    }

    [Fact]
    public async Task LostResponseAndReloadReconcileRecoveryWithoutRepeatingOwnerCreation()
    {
        await using var context = await Browser().NewContextAsync();
        var page = await OpenSetupAsync(context);
        await page.EvaluateAsync("() => sessionStorage.setItem('fixture.initializeMode', 'drop')");
        await page.EvaluateAsync("() => sessionStorage.setItem('fixture.statusMode', 'recovery')");

        await page.EvaluateAsync("() => window.netratelSetup.initialize()");
        await page.GetByText("Setup recovery required").WaitForAsync();
        Assert.Equal("1", await page.EvaluateAsync<string>(
            "() => sessionStorage.getItem('fixture.initializeCount')"));
        Assert.Equal(string.Empty, await page.GetByTestId("setup-password").InputValueAsync());
        var marker = await page.EvaluateAsync<string>(
            "() => sessionStorage.getItem('netratel.first-setup-progress.v1')");
        Assert.DoesNotContain("disposable-passphrase", marker);
        Assert.DoesNotContain("admin@example.test", marker);

        await page.ReloadAsync();
        await page.GetByText("Setup recovery required").WaitForAsync();
        Assert.Equal("1", await page.EvaluateAsync<string>(
            "() => sessionStorage.getItem('fixture.initializeCount')"));
        Assert.True(int.Parse(await page.EvaluateAsync<string>(
            "() => sessionStorage.getItem('fixture.statusCount')")) >= 2);
        Assert.True(await page.EvaluateAsync<bool>("""
            () => { const view = document.getElementById('setup-initializing');
                const bounds = view.getBoundingClientRect();
                return !view.hidden && getComputedStyle(view).visibility === 'visible' &&
                    bounds.width > 0 && bounds.height > 0; }
            """));
    }

    public async ValueTask InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    private IBrowser Browser() => _browser ?? throw new InvalidOperationException("Playwright browser was not initialized.");

    private static async Task<IPage> OpenSetupAsync(IBrowserContext context)
    {
        var scriptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "NetRatel.Web", "wwwroot", "js", "setup-wizard.js"));
        var script = await File.ReadAllTextAsync(scriptPath);
        var html = """
            <!doctype html><html><body>
            <input data-testid="setup-tenant" value="Fixture tenant">
            <input data-testid="setup-display-name" value="Fixture administrator">
            <input data-testid="setup-email" value="admin@example.test">
            <input data-testid="setup-password" value="disposable-passphrase-for-test">
            <input data-testid="setup-confirm-password" value="disposable-passphrase-for-test">
            <button data-testid="setup-initialize">Initialize</button>
            <div id="setup-client-error" hidden></div>
            <section id="setup-initializing" hidden>
              <h1 id="setup-initializing-title"></h1>
              <p id="setup-initializing-status"></p>
              <p id="setup-initializing-detail"></p>
              <button id="setup-check-again" hidden>Check again</button>
              <button id="setup-review-details" hidden>Review setup details</button>
            </section>
            </body></html>
            """.Replace("</body>", "<script>" + script + "</script></body>", StringComparison.Ordinal);
        await context.RouteAsync("**/setup", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200, ContentType = "text/html", Body = html
        }));
        await context.AddInitScriptAsync("""
            window.fetch = async (url) => {
                if (String(url).includes('/api/v2/setup/initialize')) {
                    sessionStorage.setItem('fixture.initializeCount',
                        String(Number(sessionStorage.getItem('fixture.initializeCount') || '0') + 1));
                    if (sessionStorage.getItem('fixture.initializeMode') === 'drop') throw new Error('lost response');
                    return new Response(JSON.stringify({ errors: { setup: ['Correct the administrator details.'] } }),
                        { status: 400, headers: { 'content-type': 'application/json' } });
                }
                if (String(url).includes('/api/v2/setup/status')) {
                    sessionStorage.setItem('fixture.statusCount',
                        String(Number(sessionStorage.getItem('fixture.statusCount') || '0') + 1));
                    return new Response(JSON.stringify({ state: 4, isReady: false, isRecoveryRequired: true }),
                        { status: 200, headers: { 'content-type': 'application/json' } });
                }
                throw new Error('unexpected fixture request');
            };
            """);
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(5_000);
        await page.GotoAsync("http://setup.test/setup", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.EvaluateAsync("() => { sessionStorage.setItem('fixture.initializeCount', '0'); sessionStorage.setItem('fixture.statusCount', '0'); }");
        return page;
    }
}
