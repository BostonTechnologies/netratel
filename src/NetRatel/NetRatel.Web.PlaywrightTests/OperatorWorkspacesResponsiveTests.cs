using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using System.Collections.Concurrent;
using System.Text.Json;
using NetRatel.Application.Notifications;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Shared.Contracts.SoftwareInventory;
using NetRatel.Web.Services;
using NetRatel.Web.Services.Authentication;
using NetRatel.Web.Services.Notifications;
using NetRatel.Web.Services.SoftwareInventory;
using NetRatel.Web.Services.Terminal;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "BrowserAcceptance")]
public sealed class OperatorWorkspacesResponsiveTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private ClientsManagementFixtureHost? _fixture;
    private readonly ScriptLibraryWorkspaceFixture _scripts = new();

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(360, 640)]
    public async Task Tasks_Fleet_and_Notifications_keep_their_navigation_and_actions_reachable(int width, int height)
    {
        await using var context = await browserFixture.Browser.NewContextAsync(new()
        { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();

        await OpenAsync(page, "/tasks/new", width);
        var taskTabs = page.GetByTestId("tasks-workspace-tabs");
        await Assertions.Expect(taskTabs.Locator("[aria-current=page]")).ToHaveTextAsync("Tasks");
        await taskTabs.GetByRole(AriaRole.Link, new() { Name = "History", Exact = true }).ClickAsync();
        await Assertions.Expect(taskTabs.Locator("[aria-current=page]")).ToHaveTextAsync("History");
        await Assertions.Expect(page.GetByTestId("task-history-count")).ToBeVisibleAsync();

        await OpenAsync(page, "/clients", width);
        await page.GetByTestId("clients-page-interactive").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        var fleet = page.GetByTestId("fleet-workspace-tabs");
        await Assertions.Expect(fleet.GetByRole(AriaRole.Link)).ToHaveCountAsync(4);
        await fleet.GetByRole(AriaRole.Link, new() { Name = "Tenants", Exact = true }).ClickAsync();
        await Assertions.Expect(fleet.Locator("[aria-current=page]")).ToHaveTextAsync("Tenants");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Add Tenant", Exact = true })).ToBeVisibleAsync();
        await fleet.GetByRole(AriaRole.Link, new() { Name = "Installed Software", Exact = true }).ClickAsync();
        var tenant = page.GetByTestId("inventory-tenant-selector").GetByLabel("Tenant", new() { Exact = true });
        await Assertions.Expect(tenant).ToBeEnabledAsync();
        await tenant.FillAsync("zebra");
        await page.Locator(".mud-popover-open").GetByText("Zebra Operations", new() { Exact = true }).WaitForAsync();
        await tenant.PressAsync("ArrowDown");
        await tenant.PressAsync("Enter");
        await Assertions.Expect(tenant).ToHaveValueAsync("Zebra Operations");
        await Assertions.Expect(page.GetByText("Inventory for tenant 97", new() { Exact = true })).ToBeVisibleAsync();
        await CaptureAsync(page, $"fleet-inventory-{width}x{height}");

        await OpenAsync(page, "/notifications", width);
        await Assertions.Expect(page.GetByTestId("notification-event")).ToContainTextAsync("fixture-client decommissioned");
        var table = page.Locator(".notifications-table");
        await Assertions.Expect(table.GetByRole(AriaRole.Columnheader, new() { Name = "Entity", Exact = true })).ToHaveCountAsync(0);
        await Assertions.Expect(table.GetByRole(AriaRole.Columnheader, new() { Name = "Correlation", Exact = true })).ToHaveCountAsync(0);
        var markRead = table.GetByRole(AriaRole.Button, new() { Name = "Mark Read", Exact = true });
        await ReachableAsync(markRead, width, height);
        await CaptureAsync(page, $"notifications-actions-{width}x{height}");
    }

    [Fact]
    public async Task Portrait_client_cards_open_and_close_a_compact_terminal()
    {
        var directory = (_fixture ?? throw new InvalidOperationException("Fixture unavailable")).ClientDirectory;
        directory.EnableTerminalSession = true;
        await using var context = await browserFixture.Browser.NewContextAsync(new()
        { ViewportSize = new() { Width = 360, Height = 640 } });
        var page = await context.NewPageAsync();
        await OpenAsync(page, "/clients", 360);
        await page.GetByTestId("clients-page-interactive").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        var launcher = page.GetByTestId("client-card-item").Locator("button[aria-label='Open terminal']:enabled");
        await launcher.ClickAsync();
        await page.Locator(".mud-popover-open").GetByText("Bash", new() { Exact = true }).ClickAsync();
        var header = page.GetByTestId("client-terminal-session-header");
        await Assertions.Expect(header).ToContainTextAsync("Terminal");
        await Assertions.Expect(page.Locator(".xterm-rows")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".client-terminal-session-status").GetByText("connected", new() { Exact = true })).ToBeVisibleAsync();
        var close = header.GetByRole(AriaRole.Button, new() { Name = "Close terminal", Exact = true });
        await InViewportAsync(close);
        Assert.InRange(await close.EvaluateAsync<int>("element => element.offsetWidth"), 32, 40);
        Assert.InRange(await close.EvaluateAsync<int>("element => element.offsetHeight"), 32, 40);
        Assert.InRange(await header.EvaluateAsync<int>("element => element.offsetHeight"), 36, 90);
        await CaptureAsync(page, "portrait-terminal-header", fullPage: false);
        await close.ClickAsync();
        await Assertions.Expect(header).ToBeHiddenAsync();
        Assert.Equal(1, directory.TerminalCloses);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    [Theory]
    [InlineData(1366, 768, "light", 1d, Traits = new[] { "Viewport", "DesktopLight" })]
    [InlineData(1366, 768, "dark", 1d)]
    [InlineData(390, 844, "dark", 1d)]
    [InlineData(640, 400, "light", 2d)] // 1280×800 physical pixels at 200% browser zoom equivalent.
    public async Task Primary_workspaces_and_dialog_actions_remain_reachable(int width, int height, string theme, double dpr)
    {
        await using var context = await browserFixture.Browser.NewContextAsync(new()
        { ViewportSize = new() { Width = width, Height = height }, DeviceScaleFactor = (float)dpr, ColorScheme = ColorScheme.Light });
        await context.AddInitScriptAsync($"localStorage.setItem('netratel.theme.preference', '{theme}');");
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(15_000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        var pending = new ConcurrentDictionary<string, string>();
        var failedRequests = new ConcurrentQueue<string>();
        page.Request += (_, request) => pending[request.Url.Split('?')[0]] = request.ResourceType;
        page.RequestFinished += (sender, request) => pending.TryRemove(request.Url.Split('?')[0], out _);
        page.RequestFailed += (sender, request) =>
        {
            var path = request.Url.Split('?')[0]; pending.TryRemove(path, out _);
            failedRequests.Enqueue($"{path}: {request.Failure}");
        };
        try
        {
            await OpenAsync(page, "/ai/prompts", width);
            await page.Locator($"html[data-netratel-theme='{theme}']").WaitForAsync(new() { Timeout = 90_000 });
            await page.GetByTestId("prompt-new").ClickAsync();
            var name = page.GetByTestId("prompt-name");
            await page.Locator(".prompt-editor .monaco-editor").WaitForAsync(new() { Timeout = 90_000 });
            await Assertions.Expect(name).ToHaveValueAsync("");
            await Assertions.Expect(name).ToBeFocusedAsync();
            await Assertions.Expect(page.GetByTestId("prompt-save")).ToBeDisabledAsync();
            if (width >= 1280)
            {
                var list = (await page.Locator(".prompt-list").BoundingBoxAsync())!;
                var detail = (await page.Locator(".prompt-detail").BoundingBoxAsync())!;
                Assert.InRange(list.Width, 260, 300); Assert.InRange(Math.Abs(list.Y - detail.Y), 0, 1);
                await InViewportAsync(page.GetByTestId("prompt-save"));
                await InViewportAsync(page.GetByTestId("prompt-cancel"));
            }
            await ReachableAsync(name, width, height);
            await ReachableAsync(page.GetByTestId("prompt-cancel"), width, height);
            await CaptureAsync(page, $"prompts-new-{width}x{height}-{theme}-dpr{dpr}");

            await OpenAsync(page, "/scriptlibrary", width);
            var tree = page.GetByTestId("script-library-tree");
            await tree.GetByText("operations", new() { Exact = true }).WaitForAsync();
            var treeBounds = (await tree.BoundingBoxAsync())!;
            var rowBounds = (await tree.Locator(".mud-treeview-item-content").First.BoundingBoxAsync())!;
            var dividerBounds = (await page.Locator(".script-library-tree-panel > .mud-divider").BoundingBoxAsync())!;
            var filtersBounds = (await page.Locator(".script-library-tree-panel > div:first-child").BoundingBoxAsync())!;
            Assert.InRange(rowBounds.Y - treeBounds.Y, -1, 12);
            Assert.InRange(dividerBounds.Height, 0, 2);
            Assert.InRange(rowBounds.Y - filtersBounds.Y - filtersBounds.Height, 0, 28);
            await CaptureAsync(page, $"scripts-short-tree-{width}x{height}-{theme}-dpr{dpr}");
            await tree.GetByText("operations", new() { Exact = true }).ClickAsync();
            await page.GetByTestId("script-library-new").ClickAsync();
            var scriptName = page.GetByTestId("new-script-name");
            await Assertions.Expect(page.GetByTestId("new-script-placement")).ToContainTextAsync("/operations/");
            await scriptName.FillAsync("Review script");
            await page.GetByTestId("new-script-new-folder").ClickAsync();
            await page.GetByTestId("new-script-folder-path").FillAsync("/operations/windows/");
            await Assertions.Expect(page.GetByTestId("new-script-placement")).ToContainTextAsync("/operations/windows/");
            await ReachableAsync(page.GetByTestId("new-script-create"), width, height);
            await CaptureAsync(page, $"scripts-new-{width}x{height}-{theme}-dpr{dpr}");
            await page.GetByTestId("new-script-create").ClickAsync();
            await Assertions.Expect(page.GetByTestId("new-script-create")).ToHaveCountAsync(0);
            await Assertions.Expect(tree.GetByText("Review script", new() { Exact = true })).ToBeVisibleAsync();
            await page.GetByTestId("script-library-editor").Locator(".monaco-editor").WaitForAsync(new() { Timeout = 90_000 });
            await Assertions.Expect(page.GetByTestId("script-library-editor").GetByRole(AriaRole.Textbox, new() { Name = "Editor content", Exact = true })).ToBeFocusedAsync();
            Assert.Equal("/operations/windows/", _scripts.LastCreate!.FolderPath);

            await OpenAsync(page, "/tasks/new", width);
            await page.GetByTestId("task-command").WaitForAsync();
            await page.GetByLabel("Tenant", new() { Exact = true }).FillAsync("Visual fixture");
            await page.Locator(".mud-popover-open").GetByText("Visual fixture", new() { Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Select gateway-agent-01", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByTestId("task-selected-target")).ToContainTextAsync("gateway-agent-01");
            var command = page.GetByTestId("task-command");
            await Assertions.Expect(command).ToHaveAttributeAsync("rows", "2");
            await command.FillAsync("Write-Output 'review'\nWrite-Output 'multiline preserved'");
            await page.GetByRole(AriaRole.Button, new() { Name = "Add parameter", Exact = true }).ClickAsync();
            await page.Locator(".task-parameter-row").Last.GetByLabel("Key", new() { Exact = true }).FillAsync("mode");
            await page.Locator(".task-parameter-row").Last.GetByLabel("Value", new() { Exact = true }).FillAsync("review");
            await page.GetByTestId("task-expand-editor").ClickAsync();
            await Assertions.Expect(command).ToHaveAttributeAsync("rows", "12");
            await page.GetByTestId("task-expand-editor").ClickAsync();
            await Assertions.Expect(command).ToHaveValueAsync("Write-Output 'review'\nWrite-Output 'multiline preserved'");
            if (width >= 1280)
            {
                await InViewportAsync(page.Locator(".task-parameter-row").Last);
                await InViewportAsync(page.GetByTestId("task-launch"));
            }
            await ReachableAsync(page.GetByTestId("task-launch"), width, height);
            await CaptureAsync(page, $"task-parameters-{width}x{height}-{theme}-dpr{dpr}");
            if (width >= 1280 && theme == "light")
            {
                await page.GetByTestId("task-launch").ClickAsync();
                await Assertions.Expect(page.GetByTestId("task-launch-status")).ToContainTextAsync("Completed");
                await CaptureAsync(page, $"task-completed-{width}x{height}-{theme}-dpr{dpr}");
            }

            await OpenAsync(page, "/jobs", width);
            var run = page.GetByRole(AriaRole.Button, new() { Name = "Run Nightly backup", Exact = true });
            await run.WaitForAsync();
            await ReachableAsync(run, width, height);
            foreach (var action in new[] { "Edit", "Clone", "Delete" })
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = action + " Nightly backup", Exact = true })).ToBeVisibleAsync();
            await CaptureAsync(page, $"jobs-definitions-{width}x{height}-{theme}-dpr{dpr}");
            await page.GetByRole(AriaRole.Button, new() { Name = "New Job", Exact = true }).ClickAsync();
            var dialog = page.Locator(".jobs-edit-dialog");
            await dialog.WaitForAsync();
            await ReachableAsync(dialog.GetByRole(AriaRole.Button, new() { Name = "Create and add steps", Exact = true }), width, height);
            await CaptureAsync(page, $"jobs-new-{width}x{height}-{theme}-dpr{dpr}");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Edit Nightly backup", Exact = true }).ClickAsync();
            await dialog.WaitForAsync();
            await Assertions.Expect(dialog.GetByLabel("Job Name", new() { Exact = true })).ToHaveValueAsync("Nightly backup");
            await ReachableAsync(dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }), width, height);
            await CaptureAsync(page, $"jobs-edit-{width}x{height}-{theme}-dpr{dpr}");
            Assert.Empty(errors);
            Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        }
        catch
        {
            await CaptureAsync(page, $"workspace-failure-{width}x{height}-{theme}-dpr{dpr}", validateOverflow: false);
            var root = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright");
            var performance = await page.EvaluateAsync<JsonElement>("""
                () => {
                  const matchedRules = e => {
                    const matches = [];
                    const visit = (rules, source) => {
                      for (const rule of rules) {
                        if (rule.selectorText && rule.style && /(?:height|flex[\w-]*|margin[\w-]*|padding[\w-]*|display|justify-content|align-items)\s*:/.test(rule.style.cssText)) {
                          try { if (e.matches(rule.selectorText)) matches.push({source, selector: rule.selectorText, styles: rule.style.cssText}); } catch {}
                        }
                        if (rule.cssRules) visit(rule.cssRules, source);
                      }
                    };
                    for (const sheet of document.styleSheets) {
                      try { visit(sheet.cssRules, sheet.href ? new URL(sheet.href).pathname : 'inline'); } catch {}
                    }
                    return matches.slice(-24);
                  };
                  const describe = e => {
                    if (!e) return null;
                    const r=e.getBoundingClientRect(), s=getComputedStyle(e);
                    return {tag:e.tagName, classes:e.className, rect:{x:r.x,y:r.y,width:r.width,height:r.height},
                      display:s.display, position:s.position, height:s.height, minHeight:s.minHeight, maxHeight:s.maxHeight,
                      flex:s.flex, flexBasis:s.flexBasis, flexGrow:s.flexGrow, flexShrink:s.flexShrink, flexDirection:s.flexDirection,
                      justifyContent:s.justifyContent, alignItems:s.alignItems,
                      marginTop:s.marginTop, marginBottom:s.marginBottom, paddingTop:s.paddingTop, paddingBottom:s.paddingBottom,
                      matchedRules:matchedRules(e)};
                  };
                  const panel=document.querySelector('.script-library-tree-panel');
                  const row=panel?.querySelector('.mud-treeview-item-content');
                  const ancestors=[];
                  for(let e=row; e && ancestors.length<8; e=e.parentElement) ancestors.push(describe(e));
                  return {readyState: document.readyState,
                  activeElement: {tag: document.activeElement?.tagName, classes: document.activeElement?.className,
                    role: document.activeElement?.getAttribute('role'), label: document.activeElement?.getAttribute('aria-label'),
                    inScriptMonaco: !!document.activeElement?.closest('[data-testid="script-library-editor"] .monaco-editor')},
                  scriptTree: {panel:describe(panel), children:panel ? Array.from(panel.children).map(describe) : [],
                    rootTree:describe(panel?.querySelector('.mud-treeview')), firstRow:describe(row), ancestors},
                  resources: performance.getEntriesByType('resource').slice(-60).map(e => ({
                  path: new URL(e.name).origin + new URL(e.name).pathname, duration: e.duration, responseEnd: e.responseEnd
                }))}; }
                """);
            await File.WriteAllTextAsync(Path.Combine(root, $"workspace-failure-{width}x{height}-{theme}-dpr{dpr}.json"),
                JsonSerializer.Serialize(new { performance, pending = pending.ToArray(), failedRequests = failedRequests.ToArray(), errors,
                    serverDiagnostics = _fixture!.StartupServerDiagnostics.TakeLast(20).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
    }

    private async Task OpenAsync(IPage page, string route, int width)
    {
        // Match the existing fixture's bounded cold-start allowance; interactions retain the 15s default.
        var fixture = _fixture ?? throw new InvalidOperationException("Fixture unavailable");
        IResponse? response;
        try
        {
            response = await page.GotoAsync(fixture.BaseAddress + route, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
        }
        catch (PlaywrightException error)
        {
            var diagnostics = string.Join(Environment.NewLine, fixture.StartupServerDiagnostics.TakeLast(8));
            throw new InvalidOperationException($"Fixture route {route} did not open. {diagnostics}", error);
        }
        Assert.True(response?.Ok == true, $"Fixture route {route} returned {response?.Status}.");
        await page.GetByTestId("app-main-content").WaitForAsync(new() { Timeout = 90_000 });
        var drawer = page.GetByTestId("app-navigation-drawer");
        var closed = await drawer.EvaluateAsync<bool>("e => e.classList.contains('mud-drawer--closed')");
        if ((width >= 1280 && closed) || (width < 1280 && !closed)) await page.GetByTestId("navigation-toggle").ClickAsync();
        if (width >= 1280) await Assertions.Expect(drawer).ToBeVisibleAsync();
    }
    private static Task InViewportAsync(ILocator control) => Assertions.Expect(control).ToBeInViewportAsync(new() { Ratio = 1 });
    private static async Task ReachableAsync(ILocator control, int width, int height)
    {
        await control.EvaluateAsync("e => e.scrollIntoView({block:'center',inline:'nearest'})");
        var bounds = (await control.BoundingBoxAsync())!;
        Assert.InRange(bounds.X, -1, width); Assert.True(bounds.X + bounds.Width <= width + 1);
        Assert.InRange(bounds.Y, -1, height); Assert.True(bounds.Y + bounds.Height <= height + 1);
        Assert.True(await control.EvaluateAsync<bool>("e => {const r=e.getBoundingClientRect(),hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);return hit===e||e.contains(hit)||(e.matches(':disabled')&&hit?.contains(e));}"), "Workspace control must be reachable without a covering app bar or drawer.");
    }
    private static async Task CaptureAsync(IPage page, string name, bool validateOverflow = true, bool fullPage = true)
    {
        if (validateOverflow) Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"), "Workspace must not overflow horizontally.");
        var root = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright");
        Directory.CreateDirectory(root);
        await page.ScreenshotAsync(new() { Path = Path.Combine(root, name + ".png"), FullPage = fullPage, Animations = ScreenshotAnimations.Disabled });
    }
    public async ValueTask InitializeAsync() => _fixture = await ClientsManagementFixtureHost.StartAsync(services =>
    {
        NewTaskWorkspaceFixture.AddServices(services);
        _scripts.Register(services);
        JobsWorkspaceFixture.Configure(services);
        services.AddSingleton<IPromptLibraryApiService, PromptWorkspaceFixture>();
        services.AddSingleton<ISoftwareInventoryApiService, InventoryWorkspaceFixture>();
        services.AddSingleton<INetRatelNotificationApiClient, NotificationWorkspaceFixture>();
        services.AddScoped<NetRatelNotificationStreamService>();
        services.AddHttpContextAccessor();
        services.AddSingleton<ISystemTokenService, SystemTokenService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<OperatorApiCredentialState>();
        services.AddScoped<OperatorApiCredentialProvider>();
        services.AddScoped<AkkaAuthorityFanoutClient>();
        services.AddScoped<ITerminalService, TerminalService>();
    });
    public async ValueTask DisposeAsync() { if (_fixture is not null) await _fixture.DisposeAsync(); }

    private sealed class InventoryWorkspaceFixture : ISoftwareInventoryApiService
    {
        private static readonly SoftwareInventoryTenant[] Tenants =
            [.. Enumerable.Range(1, 40).Select(id => new SoftwareInventoryTenant(id, $"Fixture tenant {id:D2}")), new(97, "Zebra Operations")];

        public Task<SoftwarePage<SoftwareInventoryTenant>> TenantsAsync(int page, CancellationToken ct, string? search = null, int pageSize = 100)
        {
            var matches = Tenants.Where(tenant => tenant.Name.Contains(search ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
            return Task.FromResult(new SoftwarePage<SoftwareInventoryTenant>(matches.Skip(page * pageSize).Take(pageSize).ToArray(), matches.Length, page, pageSize));
        }

        public Task<SoftwarePage<SoftwarePrevalenceDto>> AggregateAsync(int tenant, SoftwareInventoryQuery query, CancellationToken ct) =>
            Task.FromResult(new SoftwarePage<SoftwarePrevalenceDto>([new($"Inventory for tenant {tenant}", "1", "Fixture", "rpm", 1, 1)], 1, 0, 50));
        public Task<SoftwareInventorySummary> SummaryAsync(int tenant, CancellationToken ct) => Task.FromResult(new SoftwareInventorySummary(1, 1, 0, 0, 0, 0, 1, 1, DateTimeOffset.UtcNow));
        public Task<SoftwareInventoryStatus> StatusAsync(int tenant, Guid agent, CancellationToken ct) => throw new NotSupportedException();
        public Task<SoftwareInventoryStatus> RefreshAsync(int tenant, Guid agent, bool force, CancellationToken ct) => throw new NotSupportedException();
        public Task<SoftwarePage<SoftwareInstallationDto>> InstallationsAsync(int tenant, SoftwareInventoryQuery query, CancellationToken ct) =>
            Task.FromResult(new SoftwarePage<SoftwareInstallationDto>([], 0, 0, 50));
    }

    private sealed class NotificationWorkspaceFixture : INetRatelNotificationApiClient
    {
        private const string Agent = "11111111-2222-4333-8444-555555555555";
        private static readonly NetRatelNotificationDto Row = new()
        {
            Id = Guid.Parse("11111111-2222-4333-8444-999999999999"), TenantId = "1",
            EventType = "DomainEvent.Orchestration.Agent.WentOffline", Source = "Agent",
            EntityId = Agent, CorrelationId = "synthetic-notification",
            ClientName = "fixture-client", Message = $"Agent {Agent} decommissioned after a long diagnostic description about a scheduled change in a synthetic tenant.",
            Severity = NetRatelNotificationSeverity.Warning, Status = "Published", OccurredUtc = DateTimeOffset.UtcNow
        };

        public Task<PagedResult<NetRatelNotificationDto>> GetPageAsync(int page = 1, int pageSize = 20, string? eventType = null,
            string? correlationId = null, string? entityId = null, string? status = null, DateTimeOffset? from = null, DateTimeOffset? to = null,
            string? searchTerm = null, string? source = null, NetRatelNotificationSeverity? severity = null, CancellationToken ct = default) =>
            Task.FromResult(new PagedResult<NetRatelNotificationDto>([Row], page, pageSize, 1));
        public Task<NetRatelNotificationDto?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<NetRatelNotificationDto?>(id == Row.Id ? Row : null);
        public Task<IReadOnlyList<NetRatelNotificationDto>> GetUnreadErrorsAsync(int take = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NetRatelNotificationDto>>([]);
        public Task<NetRatelNotificationSummaryDto> GetSummaryAsync(CancellationToken ct = default) => Task.FromResult(new NetRatelNotificationSummaryDto { TotalCount = 1, UnreadCount = 1 });
        public Task<int> MarkReadBulkAsync(IEnumerable<Guid> ids, CancellationToken ct = default) => Task.FromResult(ids.Count());
        public Task RetryAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class PromptWorkspaceFixture : IPromptLibraryApiService
    {
        private static readonly Guid Id = Guid.Parse("b56d22a5-2a52-407d-943e-8933c054b01a");
        private static AiPromptDetailDto Detail => new(new(Id, "Offline client investigation", "Check current evidence before escalation", 3, false, false,
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow, "fixture-operator", "fixture-operator"), "# Offline client\nReview connectivity and the most recent telemetry.");
        public Task<IReadOnlyList<AiPromptTenantDto>> TenantsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AiPromptTenantDto>>([new(7, "Visual fixture", true)]);
        public Task<AiPromptListDto> ListAsync(int tenantId, string? search = null, bool includeArchived = false, CancellationToken ct = default) => Task.FromResult(new AiPromptListDto([Detail.Summary], new(0, null), false));
        public Task<AiPromptDetailDto> GetAsync(int tenantId, Guid id, long? revision = null, CancellationToken ct = default) => Task.FromResult(Detail);
        public Task<AiPromptDetailDto> CreateAsync(int tenantId, AiPromptSaveRequest request, CancellationToken ct = default) => Task.FromResult(Detail with { Body = request.Body, Summary = Detail.Summary with { Name = request.Name } });
        public Task<AiPromptDetailDto> UpdateAsync(int tenantId, Guid id, AiPromptSaveRequest request, CancellationToken ct = default) => Task.FromResult(Detail with { Body = request.Body, Summary = Detail.Summary with { Name = request.Name, Revision = request.ExpectedRevision + 1 } });
        public Task<AiPromptDetailDto> ArchiveAsync(int tenantId, Guid id, AiPromptArchiveRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Rendered review does not archive prompts.");
        public Task<AiPromptDefaultDto> SetDefaultAsync(int tenantId, AiPromptSetDefaultRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Rendered review does not change defaults.");
    }
}
