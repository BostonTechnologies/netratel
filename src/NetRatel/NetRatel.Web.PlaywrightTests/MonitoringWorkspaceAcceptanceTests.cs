using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Web.Services;
using NetRatel.Web.Services.Monitoring;
using NetRatel.Web.Services.Flows;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class MonitoringWorkspaceAcceptanceTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [InlineData(1280, 900, false)]
    [InlineData(390, 844, true)]
    [InlineData(195, 422, false)]
    public async Task Management_tables_and_same_circuit_viewport_interop_remain_interactive(int width, int height, bool dark)
    {
        var monitoring = new FixtureMonitoringApi { ManagementDataset = true };
        await using var host = await ClientsManagementFixtureHost.StartAsync(services => { services.AddSingleton<IMonitoringApiService>(monitoring); services.AddSingleton<IFlowApiService>(new FixtureFlowApiService(tenantId: 1)); });
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height }, DeviceScaleFactor = width == 195 ? 2f : 1f });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        await page.AddInitScriptAsync($"localStorage.setItem('netratel.theme.preference', '{(dark ? "dark" : "light")}');");
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(host.BaseAddress + "/flows?tenantId=1");
        await page.GetByTestId("flows-refresh").ClickAsync();
        await page.EvaluateAsync("() => Blazor.navigateTo('/monitoring?tenantId=1&view=manage&manageTab=groups')");
        await Assertions.Expect(page.GetByTestId("manage-table-groups").Locator("tbody tr")).ToHaveCountAsync(20);
        await Assertions.Expect(page.GetByTestId("manage-pager")).ToContainTextAsync("1–20 of 107 matching");
        await page.GetByTestId("manage-next").ClickAsync();
        await Assertions.Expect(page.GetByTestId("manage-pager")).ToContainTextAsync("21–40 of 107 matching");
        await page.GetByTestId("manage-search").FillAsync("Group 106");
        await Assertions.Expect(page.GetByTestId("manage-table-groups").Locator("tbody tr")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("manage-table-groups")).ToContainTextAsync("Group 106");
        await page.GetByTestId("manage-tab-rules").ClickAsync();
        await Assertions.Expect(page.GetByTestId("manage-search")).ToHaveValueAsync("");
        await page.GetByTestId("manage-page-size").SelectOptionAsync("100");
        await Assertions.Expect(page.GetByTestId("manage-table-rules").Locator("tbody tr")).ToHaveCountAsync(100);
        var geometry = await page.GetByTestId("manage-table-scroll").EvaluateAsync<bool>("e => { const h=e.querySelector('th'), before=h.getBoundingClientRect().top; e.scrollTop=200; return getComputedStyle(h).position==='sticky' && Math.abs(h.getBoundingClientRect().top-before)<2 && e.clientHeight<e.scrollHeight; }");
        Assert.True(geometry);
        await page.GetByTestId("manage-tab-rules").FocusAsync(); await page.Keyboard.PressAsync("End");
        await Assertions.Expect(page.GetByTestId("manage-tab-bypasses")).ToBeFocusedAsync();
        await Assertions.Expect(page.GetByTestId("manage-table-bypasses")).ToBeVisibleAsync();
        await page.GetByTestId("manage-tab-groups").ClickAsync();
        await Assertions.Expect(page.GetByTestId("manage-search")).ToHaveValueAsync("Group 106");
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByTestId("manage-search")).ToHaveValueAsync("Group 106");
        await page.GetByTestId("monitoring-tab-active").ClickAsync();
        await page.GetByTestId("monitoring-refresh").ClickAsync();
        await page.EvaluateAsync("() => Blazor.navigateTo('/flows?tenantId=1')");
        await page.GetByTestId("flows-refresh").ClickAsync();
        await page.EvaluateAsync("() => Blazor.navigateTo('/monitoring?tenantId=1')");
        await Assertions.Expect(page.GetByTestId("monitoring-tenant")).ToHaveValueAsync("1");
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        Assert.Empty(errors); Assert.Equal(0, monitoring.Writes);
        Assert.DoesNotContain(host.StartupServerDiagnostics, message => message.Contains("not a function", StringComparison.Ordinal));
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth>innerWidth+1"));
        var directory = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(directory);
        await page.GetByTestId("monitoring-tab-manage").ClickAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"monitoring-management-{width}-{height}.png"), FullPage = true });
    }

    [Fact]
    public async Task Cleanup_JS_failure_is_contained_during_real_same_circuit_navigation()
    {
        await using var host = await ClientsManagementFixtureHost.StartAsync(services => services.AddSingleton<IFlowApiService>(new FixtureFlowApiService(tenantId: 1)));
        await using var context = await browserFixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.RouteAsync("**/js/flows-results.js", async route =>
        {
            var source = await route.FetchAsync();
            var text = (await source.TextAsync()).Replace("current = null; }", "current = null; window.__viewportCleanup = (window.__viewportCleanup || 0) + 1; throw new Error('injected viewport cleanup failure'); }");
            await route.FulfillAsync(new() { Response = source, Body = text, ContentType = "text/javascript" });
        });
        await page.GotoAsync(host.BaseAddress + "/flows?tenantId=1");
        await page.GetByTestId("flows-refresh").ClickAsync();
        await page.EvaluateAsync("() => Blazor.navigateTo('/monitoring?tenantId=1')");
        await Assertions.Expect(page.GetByTestId("monitoring-tenant")).ToHaveValueAsync("1");
        await page.GetByTestId("monitoring-refresh").ClickAsync();
        Assert.Equal(1, await page.EvaluateAsync<int>("() => window.__viewportCleanup"));
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    [Fact]
    public async Task Alert_menu_escapes_scroll_clip_and_created_tab_waits_for_explicit_Start()
    {
        var monitoring = new FixtureMonitoringApi(); var ai = new PreparedAiFixture(monitoring);
        await using var host = await ClientsManagementFixtureHost.StartAsync(services => { services.AddSingleton<IMonitoringApiService>(monitoring); services.AddSingleton<INetclawApiService>(ai); });
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.BaseAddress + "/monitoring?tenantId=1&pageSize=100");
        var tools = page.GetByTestId("client-tools-launcher").First.Locator(".mud-fab-menu-container > button");
        await tools.HoverAsync();
        var menu = page.Locator(".client-tools-overlay:popover-open"); await Assertions.Expect(menu).ToBeVisibleAsync();
        await Assertions.Expect(menu.GetByRole(AriaRole.Menuitem)).ToHaveCountAsync(6);
        Assert.True(await menu.EvaluateAsync<bool>("e => {const r=e.getBoundingClientRect(); return r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight&&document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)?.closest('.client-tools-overlay')===e}"));
        Assert.Equal(0, monitoring.Writes); Assert.Equal(0, ai.Creates); Assert.Equal(0, ai.Sends);
        await tools.FocusAsync(); await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(tools).ToBeFocusedAsync(); await Assertions.Expect(menu).ToHaveCountAsync(0);
        var sourceUrl = page.Url;
        var child = await context.RunAndWaitForPageAsync(() => page.GetByTestId("monitoring-ai-investigation").First.ClickAsync());
        await child.WaitForURLAsync(new Regex("/ai/ops\\?tenantId=1&investigation="));
        await Assertions.Expect(child.GetByTestId("ai-prepared-unsent")).ToBeVisibleAsync();
        Assert.Equal(1, ai.Creates); Assert.Equal(0, ai.Sends); Assert.Equal(sourceUrl, page.Url);
        Assert.Equal(monitoring.Active.Series.AgentId, ai.Request!.AgentIds.Single());
        // The selected client is intentionally absent from SearchClientsAsync's first page.
        await Assertions.Expect(child.GetByTestId("ai-ops")).ToContainTextAsync("outside-first-page-host");
        await Assertions.Expect(child.GetByTestId("chat-send")).ToHaveTextAsync("Start investigation");
        await child.GetByTestId("chat-message").FillAsync("Review the CPU evidence before suggesting a change.");
        await child.GetByTestId("chat-message").PressAsync("Enter"); Assert.Equal(0, ai.Sends);
        await child.ReloadAsync();
        await Assertions.Expect(child.GetByTestId("chat-message")).ToHaveValueAsync(new Regex("Review the CPU evidence"));
        await child.GetByTestId("chat-send").ClickAsync();
        await Assertions.Expect(child.GetByTestId("chat-send")).ToHaveTextAsync("Send"); Assert.Equal(1, ai.Sends);
        Assert.Equal(sourceUrl, page.Url); Assert.Equal(0, monitoring.Writes);
        Assert.False(await child.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    [Theory]
    [InlineData(1280, 800, false)]
    [InlineData(195, 422, true)]
    public async Task First_and_last_row_menu_fit_scroll_edges_and_support_touch_and_keyboard(int width, int height, bool touch)
    {
        var monitoring = new FixtureMonitoringApi { PagingDataset = true };
        await using var host = await ClientsManagementFixtureHost.StartAsync(services => services.AddSingleton<IMonitoringApiService>(monitoring));
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height }, HasTouch = touch });
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.BaseAddress + "/monitoring?tenantId=1&pageSize=100");
        await Assertions.Expect(page.GetByTestId("monitoring-series-row")).ToHaveCountAsync(100);
        var buttons = page.GetByTestId("client-tools-launcher").Locator(".mud-fab-menu-container > button");
        foreach (var button in new[] { buttons.First, buttons.Last })
        {
            await button.ScrollIntoViewIfNeededAsync();
            if (touch) await button.TapAsync(); else await button.HoverAsync();
            var menu = page.Locator(".client-tools-overlay:popover-open");
            await Assertions.Expect(menu).ToBeVisibleAsync();
            await Assertions.Expect(menu.GetByRole(AriaRole.Menuitem)).ToHaveCountAsync(6);
            Assert.True(await menu.EvaluateAsync<bool>("e=>{const r=e.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth+1&&r.top>=0&&r.bottom<=innerHeight+1}"));
            await button.FocusAsync(); await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(button).ToBeFocusedAsync(); await Assertions.Expect(menu).ToHaveCountAsync(0);
        }
        await buttons.Last.FocusAsync(); await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator(".client-tools-overlay:popover-open")).ToBeVisibleAsync();
        await page.GetByTestId("monitoring-table-scroll").EvaluateAsync("e=>{e.scrollTop=0;e.dispatchEvent(new Event('scroll'))}");
        await Assertions.Expect(page.Locator(".client-tools-overlay:popover-open")).ToHaveCountAsync(0);
        Assert.Equal(0, monitoring.Writes); Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    [Fact]
    public async Task Popup_blocking_and_lost_create_response_reconcile_one_investigation_with_an_explicit_open_link()
    {
        var monitoring = new FixtureMonitoringApi(); var ai = new PreparedAiFixture(monitoring) { LoseFirstResponse = true };
        await using var host = await ClientsManagementFixtureHost.StartAsync(services => { services.AddSingleton<IMonitoringApiService>(monitoring); services.AddSingleton<INetclawApiService>(ai); });
        await using var context = await browserFixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync(); await page.AddInitScriptAsync("window.open=()=>null");
        await page.GotoAsync(host.BaseAddress + "/monitoring?tenantId=1");
        var launch = page.GetByTestId("monitoring-ai-investigation").First;
        await launch.ClickAsync(); await Assertions.Expect(page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "creation was not confirmed" })).ToBeVisibleAsync();
        Assert.Equal(1, ai.Creates); Assert.Equal(0, ai.Sends);
        await launch.ClickAsync(); await Assertions.Expect(page.GetByTestId("monitoring-open-investigation")).ToHaveAttributeAsync("href", new Regex("^/ai/ops[?]tenantId=1&investigation="));
        Assert.Equal(1, ai.Creates); Assert.Equal(0, ai.Sends); Assert.Single(context.Pages);
    }

    private sealed class PreparedAiFixture(FixtureMonitoringApi monitoring) : INetclawApiService
    {
        private InvestigationSnapshotDto? _snapshot;
        public int Creates { get; private set; }
        public int Sends { get; private set; }
        public InvestigationCreateRequest? Request { get; private set; }
        public bool LoseFirstResponse { get; set; }
        public Task<IReadOnlyList<NetclawTenantDto>> TenantsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NetclawTenantDto>>([new(1, "SQL tenant", true, false)]);
        public Task<NetclawConnectionDto> ConnectionAsync(int tenantId, CancellationToken ct = default) => Task.FromResult(new NetclawConnectionDto(Guid.NewGuid(), tenantId, 1, true, "https://netclaw.example", true, null, null, null));
        public Task<IReadOnlyList<InvestigationClientDto>> ClientsAsync(int tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InvestigationClientDto>>([]);
        public Task<IReadOnlyList<InvestigationSummaryDto>> ListAsync(int tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InvestigationSummaryDto>>(_snapshot is null ? [] : [_snapshot.Investigation]);
        public Task<InvestigationSnapshotDto> CreateAsync(int tenantId, InvestigationCreateRequest request, CancellationToken ct = default)
        {
            if (_snapshot is not null) { Assert.Equal(Request!.RequestId, request.RequestId); Assert.Equal(Request.PreparedAlert, request.PreparedAlert); Assert.Equal(Request.AgentIds, request.AgentIds); Assert.Equal(Request.Title, request.Title); return Task.FromResult(_snapshot); }
            Request = request; Creates++;
            _snapshot = new(new(Guid.NewGuid(), tenantId, request.Title, InvestigationState.Idle, request.AgentIds, request.OccurrenceId, null, DateTimeOffset.UtcNow, "operator"), 1, null, [],
                [new(monitoring.Active.Series.AgentId, "outside-first-page-host", null, [], null)], [])
            { Prepared = new(request.PreparedAlert!, "Investigate CPU 97% on outside-first-page-host. Evidence observed now (UTC).", request.RequestId, DateTimeOffset.UtcNow) };
            if (LoseFirstResponse) { LoseFirstResponse = false; throw new HttpRequestException("Synthetic lost creation response"); }
            return Task.FromResult(_snapshot);
        }
        public Task<InvestigationSnapshotDto> GetAsync(int tenantId, Guid id, long after = 0, CancellationToken ct = default) => Task.FromResult(_snapshot!);
        public Task<InvestigationSnapshotDto> SendAsync(int tenantId, Guid id, InvestigationMessageRequest request, CancellationToken ct = default)
        {
            if (Sends == 0) { Sends++; _snapshot = _snapshot! with { Prepared = _snapshot.Prepared! with { Started = true }, LastSequence = 2, Events = [new(2, "operator", request.Text, "operator", null, null, DateTimeOffset.UtcNow, request.MessageId)] }; }
            return Task.FromResult(_snapshot!);
        }
        public Task<NetclawTestResult> PairAsync(int t, NetclawPairRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetclawTestResult> TestAsync(int t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetclawConnectionDto> EnabledAsync(int t, NetclawEnabledRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ApproveAsync(int t, Guid id, string call, string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InvestigationSnapshotDto> ReconcileAsync(int t, Guid id, CancellationToken ct = default) => Task.FromResult(_snapshot!);
        public Task<InvestigationSnapshotDto> ArchiveAsync(int t, Guid id, InvestigationArchiveRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
