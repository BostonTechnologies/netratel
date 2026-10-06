using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Web.Services.Services;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class ClientServicesResponsiveTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private ClientsManagementFixtureHost? _fixture;

    [Theory]
    [InlineData(1280, 800, "light", 100, false, 1d)]
    [InlineData(1280, 800, "dark", 100, true, 1d)]
    [InlineData(390, 844, "light", 100, false, 1d)]
    [InlineData(390, 844, "system", 200, true, 1d)]
    // At 200% zoom, a physical viewport has half the CSS width/height and
    // twice the device scale. Keep the 16px root font: every CSS unit, icon,
    // breakpoint and scroll boundary reflows, rather than just enlarging text.
    [InlineData(195, 422, "light", 100, true, 2d)] // Physical 390 x 844.
    [InlineData(640, 400, "dark", 100, false, 2d)] // Physical 1280 x 800.
    public async Task Services_Uses_Viewport_With_Drawer_Open_And_Returns_Focus(
        int width, int height, string theme, int textScalePercent, bool table, double deviceScaleFactor)
    {
        var fixture = _fixture ?? throw new InvalidOperationException("Services fixture is not initialized.");
        await using var context = await browserFixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height }, ColorScheme = ColorScheme.Light,
            DeviceScaleFactor = (float)deviceScaleFactor
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30_000);
        await page.GotoAsync($"{fixture.BaseAddress}/clients{(table ? "?view=table" : "")}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
        await page.GetByTestId("clients-page-interactive").WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 90_000 });
        await page.GetByTestId("client-services-launcher").First.WaitForAsync();
        Assert.Equal(0, fixture.ServicesData.Reads);
        Assert.Equal(0, fixture.ServicesData.Subscriptions);
        Assert.Equal(0, await page.GetByRole(AriaRole.Button, new() { Name = "Ping client", Exact = true }).CountAsync());
        await SetThemeAsync(page, theme);
        if (textScalePercent == 200) await page.EvaluateAsync("() => document.documentElement.style.fontSize = '32px'");

        // Keep the real production navigation drawer open beneath the full-screen dialog.
        var drawer = page.GetByTestId("app-navigation-drawer");
        var drawerClosed = await drawer.EvaluateAsync<bool>("element => element.classList.contains('mud-drawer--closed')");
        if ((width >= 1280 && drawerClosed) || (width < 1280 && !drawerClosed))
            await page.GetByTestId("navigation-toggle").ClickAsync();
        if (width >= 1280) await Assertions.Expect(drawer).ToBeVisibleAsync();
        var launcher = page.GetByTestId("client-services-launcher").First;
        await launcher.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        var dialog = page.GetByTestId("client-services-dialog");
        await dialog.WaitForAsync();
        await page.GetByTestId("service-row").First.WaitForAsync();
        var evidence = EvidenceRoot();
        Directory.CreateDirectory(evidence);
        var caseName = $"services-{width}x{height}-{theme}-{textScalePercent}-{(table ? "table" : "cards")}{(deviceScaleFactor == 1 ? "" : $"-dpr{deviceScaleFactor}")}";
        await CaptureGeometryAsync(page, dialog, Path.Combine(evidence, caseName + "-initial.json"));
        try
        {
            await WaitForSettledGeometryAsync(page);
        }
        catch
        {
            await CaptureGeometryAsync(page, dialog, Path.Combine(evidence, caseName + "-failed.json"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, caseName + "-failed.png") });
            throw;
        }
        var bounds = await dialog.EvaluateAsync<Bounds>("element => { const r = element.getBoundingClientRect(); return { x:r.x,y:r.y,width:r.width,height:r.height }; }");
        Assert.InRange(bounds.X, -1, 1);
        Assert.InRange(bounds.Y, -1, 1);
        Assert.True(bounds.Width >= width * .98, "Services must use the viewport width with the drawer open.");
        Assert.True(bounds.Height >= height * .98, "Services must use the viewport height.");
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth"), "Services must reflow without document horizontal overflow.");
        Assert.False(await page.GetByTestId("services-inventory").EvaluateAsync<bool>("element => element.scrollWidth > element.clientWidth + 1"), "Service rows must wrap without horizontal overflow.");
        await AssertControlsReachableAsync(page);
        await page.GetByTestId("services-filter-monitored").ClickAsync();
        await Assertions.Expect(page.GetByTestId("service-row")).ToHaveCountAsync(1);
        await page.GetByTestId("services-filter-all").ClickAsync();
        await Assertions.Expect(page.GetByTestId("services-filter-all")).ToHaveAttributeAsync("aria-pressed", "true");
        await page.GetByTestId("services-search").FillAsync("oneshot.service");
        await Assertions.Expect(page.Locator("[data-service-name='oneshot.service']")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("service-row")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("service-row")).ToContainTextAsync("Running");
        await Assertions.Expect(page.GetByTestId("service-row")).ToContainTextAsync("Sub: exited");
        await page.GetByTestId("services-search").FillAsync("");
        await page.GetByTestId("services-refresh").ClickAsync();
        await Assertions.Expect(page.GetByTestId("services-refresh-result")).ToContainTextAsync("Complete inventory received");
        Assert.Equal(1, fixture.ServicesData.Refreshes);
        Assert.Equal(1, fixture.ServicesData.Subscriptions);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());

        await CaptureGeometryAsync(page, dialog, Path.Combine(evidence, caseName + "-settled.json"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, caseName + ".png"), Animations = ScreenshotAnimations.Disabled });
        if (width <= 650)
        {
            foreach (var cell in await page.GetByTestId("service-row").Last.Locator("td").AllAsync())
            {
                await cell.EvaluateAsync("element => element.scrollIntoView({block:'center',inline:'nearest'})");
                var cellBounds = await cell.EvaluateAsync<Bounds>("element => { const r = element.getBoundingClientRect(); return {x:r.x,y:r.y,width:r.width,height:r.height}; }");
                var headerBounds = await page.Locator(".client-services-header").EvaluateAsync<Bounds>("element => { const r = element.getBoundingClientRect(); return {x:r.x,y:r.y,width:r.width,height:r.height}; }");
                Assert.InRange(headerBounds.Y, -1, 1);
                Assert.True(await page.GetByTestId("close-services").IsVisibleAsync(), "The close control must remain visible while the last row is scrolled into view.");
                Assert.True(cellBounds.Y >= headerBounds.Y + headerBounds.Height - 1, "Every last-row field must be reachable below the close header.");
                Assert.True(cellBounds.Y + cellBounds.Height <= height + 1, "Every last-row field must fit within the scrolled viewport.");
            }
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, caseName + "-rows.png"), Animations = ScreenshotAnimations.Disabled });
        }
        await page.GetByTestId("close-services").ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await Assertions.Expect(launcher).ToBeFocusedAsync();
        await fixture.ServicesData.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (theme == "system")
        {
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
            await page.Locator("html[data-netratel-theme='light']").WaitForAsync();
            await launcher.ClickAsync();
            await page.GetByTestId("service-row").First.WaitForAsync();
            await WaitForSettledGeometryAsync(page);
            await AssertControlsReachableAsync(page);
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(launcher).ToBeFocusedAsync();
        }
    }

    [Fact]
    public async Task Offline_Launcher_Opens_Stale_Cache_Without_Requesting_Collection()
    {
        var fixture = _fixture ?? throw new InvalidOperationException("Services fixture is not initialized.");
        await using var context = await browserFixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30_000);
        await page.GotoAsync($"{fixture.BaseAddress}/clients", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("clients-page-interactive").WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 90_000 });
        var offline = page.Locator("[data-agent-id='99f5a0b0-5e61-4039-8d09-6c9d44c7c100']").GetByTestId("client-services-launcher");
        await Assertions.Expect(offline).ToBeEnabledAsync();
        await offline.ClickAsync();
        await Assertions.Expect(page.GetByTestId("services-summary")).ToContainTextAsync("Cached offline inventory");
        await Assertions.Expect(page.GetByTestId("services-summary")).ToContainTextAsync("Stale inventory");
        await Assertions.Expect(page.GetByTestId("services-refresh")).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByTestId("service-row")).ToHaveCountAsync(3);
        Assert.Equal(0, fixture.ServicesData.Refreshes);
        await page.GetByTestId("close-services").ClickAsync();
        await Assertions.Expect(offline).ToBeFocusedAsync();
    }

    private static async Task SetThemeAsync(IPage page, string theme)
    {
        var mobile = await page.GetByTestId("mobile-overflow").IsVisibleAsync();
        await page.GetByTestId(mobile ? "mobile-overflow" : "theme-preference-menu").ClickAsync();
        await page.GetByTestId($"{(mobile ? "mobile-theme-option" : "theme-option")}-{theme}").ClickAsync();
        if (theme == "system") await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await page.Locator($"html[data-netratel-theme='{(theme == "system" ? "dark" : theme)}']").WaitForAsync();
    }

    private static async Task AssertControlsReachableAsync(IPage page)
    {
        var controls = await page.GetByTestId("client-services-dialog").Locator("button,input").AllAsync();
        foreach (var control in controls)
        {
            await control.EvaluateAsync("element => element.scrollIntoView({block:'center',inline:'nearest'})");
            var problem = await control.EvaluateAsync<string?>("""
            element => {
                const r = element.getBoundingClientRect();
                const owner = element.closest('.client-services-controls,.client-services-header');
                const o = owner?.getBoundingClientRect();
                const header = document.querySelector('.client-services-header')?.getBoundingClientRect();
                const narrow = innerWidth <= 650;
                return r.width <= 0 || r.height <= 0 || r.left < -1 || r.right > innerWidth + 1 || r.top < -1 || r.bottom > innerHeight + 1 ||
                    (o && (r.left < o.left - 1 || r.right > o.right + 1)) ||
                    (narrow && header && (Math.abs(header.top) > 1 || (!element.closest('.client-services-header') && r.top < header.bottom - 1)))
                    ? JSON.stringify({target:element.getAttribute('data-testid') || element.getAttribute('aria-label'),
                        r:{x:r.x,y:r.y,width:r.width,height:r.height},owner:o,header,
                        body:Array.from(document.querySelectorAll('.client-services-shell,.client-services-body,.mud-dialog-content')).map(node => ({
                            classes:node.className,rect:node.getBoundingClientRect(),scrollTop:node.scrollTop,scrollHeight:node.scrollHeight,clientHeight:node.clientHeight,overflow:getComputedStyle(node).overflow
                        }))}) : null;
            }
            """);
            if (problem is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(EvidenceRoot(), "services-controls-failure.json"), problem);
                await page.ScreenshotAsync(new() { Path = Path.Combine(EvidenceRoot(), "services-controls-failure.png") });
            }
            Assert.Null(problem);
        }
    }

    private static async Task WaitForSettledGeometryAsync(IPage page)
    {
        await page.WaitForFunctionAsync("""
            () => {
                const dialog = document.querySelector('[data-testid="client-services-dialog"]');
                if (!dialog) return false;
                const outer = dialog.closest('.mud-dialog');
                const r = dialog.getBoundingClientRect();
                const settled = Math.abs(r.x) <= 1 && Math.abs(r.y) <= 1 && r.width >= innerWidth * .98 && r.height >= innerHeight * .98 &&
                    outer?.getAnimations({subtree:true}).every(animation => animation.playState !== 'running' && !animation.pending);
                if (!settled) { dialog.servicesGeometryStableSince = null; return false; }
                dialog.servicesGeometryStableSince ??= performance.now();
                return performance.now() - dialog.servicesGeometryStableSince >= 200;
            }
            """, null, new() { Timeout = 5_000 });
    }

    private static async Task CaptureGeometryAsync(IPage page, ILocator dialog, string path)
    {
        var geometry = await dialog.EvaluateAsync<string>("""
            element => {
                const ancestors = [];
                for (let node = element; node && ancestors.length < 6; node = node.parentElement) {
                    const r = node.getBoundingClientRect(), c = getComputedStyle(node);
                    ancestors.push({ tag:node.tagName, classes:node.className, attributes:Array.from(node.attributes).map(a => a.name),
                        x:r.x,y:r.y,width:r.width,height:r.height,margin:c.margin,padding:c.padding,transform:c.transform,
                        position:c.position,display:c.display,maxWidth:c.maxWidth,maxHeight:c.maxHeight,overflow:c.overflow });
                }
                return JSON.stringify({innerWidth,innerHeight,devicePixelRatio,
                    visualViewport:{width:visualViewport?.width,height:visualViewport?.height,scale:visualViewport?.scale},
                    rootFont:getComputedStyle(document.documentElement).fontSize,
                    documentWidth:document.documentElement.scrollWidth,ancestors},null,2);
            }
            """);
        await File.WriteAllTextAsync(path, geometry);
    }

    public async ValueTask InitializeAsync() => _fixture = await ClientsManagementFixtureHost.StartAsync();
    public async ValueTask DisposeAsync() { if (_fixture is not null) await _fixture.DisposeAsync(); }
    private static string EvidenceRoot()
    {
        var configured = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT");
        if (string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(Path.Combine("TestResults", "playwright"));
        if (!Path.IsPathFullyQualified(configured)) throw new InvalidOperationException("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT must be an absolute path.");
        return Path.GetFullPath(configured);
    }
    private sealed class Bounds { public double X { get; set; } public double Y { get; set; } public double Width { get; set; } public double Height { get; set; } }
}

internal sealed class FixtureClientServicesService : IClientServicesApiService, IClientServicesLiveStreamService
{
    private readonly Channel<ClientServicesLiveEvent> _events = Channel.CreateBounded<ClientServicesLiveEvent>(4);
    private readonly Guid _initialCollection = Guid.NewGuid();
    private int _reads;
    private int _subscriptions;
    private int _refreshes;
    public int Reads => Volatile.Read(ref _reads);
    public int Subscriptions => Volatile.Read(ref _subscriptions);
    public int Refreshes => Volatile.Read(ref _refreshes);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ClientServicesReadModelDto?> GetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        return Task.FromResult<ClientServicesReadModelDto?>(Model(tenantId, agentId, _initialCollection));
    }

    public Task<ClientServicesRefreshResponse> RefreshAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _refreshes);
        _events.Writer.TryWrite(new("Live", Model(tenantId, agentId, Guid.NewGuid())));
        return Task.FromResult(new ClientServicesRefreshResponse(ClientServicesRefreshStatus.Requested, Guid.NewGuid()));
    }

    public async IAsyncEnumerable<ClientServicesLiveEvent> SubscribeAsync(int tenantId, Guid agentId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _subscriptions);
        try
        {
            yield return new("Live", Model(tenantId, agentId, _initialCollection));
            await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken)) yield return item;
        }
        finally { Cancelled.TrySetResult(); }
    }

    private static ClientServicesReadModelDto Model(int tenantId, Guid agentId, Guid collection)
    {
        var offline = agentId == Guid.Parse("99f5a0b0-5e61-4039-8d09-6c9d44c7c100");
        var observed = DateTimeOffset.UtcNow.AddMinutes(offline ? -45 : -1);
        ClientServiceObservation Service(string name, string display, ClientServiceState state, string subState) => new(name, display,
            ClientServicePlatform.LinuxSystemd, state, $"{(state == ClientServiceState.Running ? "active" : "inactive")}/{subState}", null,
            "loaded", state == ClientServiceState.Running ? "active" : "inactive", subState, "enabled", observed);
        var inventory = new ClientServicesSnapshotDto(collection, 1, 1, observed, observed,
        [
            Service("worker-with-a-very-long-stable-unit-name.service", "Worker with a deliberately long display name that must wrap at narrow widths", ClientServiceState.Running, "running"),
            Service("disabled.service", "Optional stopped service", ClientServiceState.Stopped, "dead"),
            Service("oneshot.service", "Successful one-shot job", ClientServiceState.Running, "exited")
        ]);
        return new(tenantId, agentId, inventory, null, [], ["worker-with-a-very-long-stable-unit-name.service"], 1,
            !offline, true, DateTimeOffset.UtcNow);
    }
}
