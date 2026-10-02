using System.Text.Json;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.RatelDesk;
using NetRatel.Web.Services.RatelDesk;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class RatelDeskConnectorResponsiveTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private ClientsManagementFixtureHost? _fixture;

    [Theory]
    [InlineData(1280, 800, "light", 1d)]
    [InlineData(1280, 800, "dark", 1d)]
    [InlineData(390, 844, "system", 1d)]
    [InlineData(195, 422, "light", 2d)]
    [InlineData(640, 400, "dark", 2d)]
    public async Task Actual_routes_and_shell_keep_explicit_mapping_controls_reachable(int width, int height, string theme, double dpr)
    {
        await using var context = await browserFixture.Browser.NewContextAsync(new()
        { ViewportSize = new() { Width = width, Height = height }, DeviceScaleFactor = (float)dpr, ColorScheme = ColorScheme.Light });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await OpenAsync(page);
        await SetThemeAsync(page, theme);
        var drawer = page.GetByTestId("app-navigation-drawer");
        var closed = await drawer.EvaluateAsync<bool>("e => e.classList.contains('mud-drawer--closed')");
        if (width >= 1280 && closed || width < 1280 && !closed) await page.GetByTestId("navigation-toggle").ClickAsync();
        await page.WaitForFunctionAsync("() => document.documentElement.scrollWidth <= innerWidth");
        foreach (var label in new[] { "Name", "Approved HTTPS origin", "Organization ID", "Customer ID", "Assignment ID (optional)", "Category IDs (optional, comma separated GUIDs)", "Information priority", "Warning priority", "Error priority", "Critical priority" })
            await ReachableAsync(page.GetByLabel(label, new() { Exact = true }), width, height);
        foreach (var id in new[] { "connector-tenant", "connector-save", "connector-credential", "connector-test", "connector-dry-run" })
            await ReachableAsync(page.GetByTestId(id), width, height);
        await page.GetByTestId("connector-test").ClickAsync();
        await Assertions.Expect(page.GetByTestId("connector-notice")).ToContainTextAsync("read-only lookups");
        await page.GetByTestId("connector-dry-run").ClickAsync();
        await Assertions.Expect(page.GetByTestId("connector-preview")).ToContainTextAsync("no incident sent");
        Assert.Equal(1, _fixture!.ConnectorsData.TestCalls); Assert.Equal(1, _fixture.ConnectorsData.PreviewCalls);
        await AssertNoOverflowAsync(page); Assert.Empty(errors);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        var root = EvidenceRoot(); Directory.CreateDirectory(root);
        var name = $"connectors-{width}x{height}-{theme}-dpr{dpr}";
        await page.EvaluateAsync("() => scrollTo(0,0)");
        await page.ScreenshotAsync(new() { Path = Path.Combine(root, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        await page.ScreenshotAsync(new() { Path = Path.Combine(root, name + "-viewport.png"), Animations = ScreenshotAnimations.Disabled });
        var geometry = await page.EvaluateAsync<JsonElement>("""
            () => {
              const bounds=e=>{const r=e.getBoundingClientRect();return{x:r.x,y:r.y,width:r.width,height:r.height}};
              return {cssWidth:innerWidth,cssHeight:innerHeight,dpr:devicePixelRatio,rootFont:getComputedStyle(document.documentElement).fontSize,
              documentWidth:document.documentElement.scrollWidth,tenant:document.querySelector('[data-testid=connector-tenant]').value,
              bodyFont:getComputedStyle(document.body).fontSize,headingFont:getComputedStyle(document.querySelector('.rd-connectors h1')).fontSize,
              appMain:bounds(document.querySelector('[data-testid=app-main-content]')),main:bounds(document.querySelector('.rd-connectors')),form:bounds(document.querySelector('.rd-grid')),
              fields:[...document.querySelectorAll('.rd-connectors fieldset input:not([type=checkbox]),.rd-connectors fieldset select,.rd-connectors textarea')]
                .map(e=>({tag:e.tagName,label:e.getAttribute('aria-label')||e.closest('label')?.firstChild?.textContent?.trim()||'Credential',...bounds(e)}))};
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, name + ".json"), geometry.GetRawText());
        Assert.Equal(width, geometry.GetProperty("cssWidth").GetInt32()); Assert.Equal(dpr, geometry.GetProperty("dpr").GetDouble()); Assert.Equal("16px", geometry.GetProperty("rootFont").GetString());
        Assert.Equal("16px", geometry.GetProperty("bodyFont").GetString());
        if (width <= 260)
        {
            foreach (var field in geometry.GetProperty("fields").EnumerateArray())
                Assert.True(field.GetProperty("width").GetDouble() >= 140, $"Narrow field {field.GetProperty("label").GetString()} has insufficient editable width.");
            Assert.True(double.Parse(geometry.GetProperty("headingFont").GetString()!.Replace("px", ""), System.Globalization.CultureInfo.InvariantCulture) >= 20);
        }
        await page.GetByLabel("Organization ID", new() { Exact = true }).ScrollIntoViewIfNeededAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(root, name + "-mapping-viewport.png"), Animations = ScreenshotAnimations.Disabled });
        if (theme == "system")
        {
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
            await page.Locator("html[data-netratel-theme=light]").WaitForAsync(); await AssertNoOverflowAsync(page);
        }
    }

    [Fact]
    public async Task Password_rotation_dirty_conflict_pending_write_and_tenant_change_preserve_the_boundary()
    {
        var fixture = _fixture!;
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000); await OpenAsync(page);
        var password = page.GetByTestId("connector-credential");
        await password.FillAsync(FixtureRatelDeskConnectorApiService.SyntheticCredential);
        await Assertions.Expect(page.GetByTestId("connector-rotate")).ToBeEnabledAsync();
        Assert.Equal("password", await password.GetAttributeAsync("type"));
        Assert.False((await page.Locator("body").InnerTextAsync()).Contains(FixtureRatelDeskConnectorApiService.SyntheticCredential, StringComparison.Ordinal));
        Assert.DoesNotContain(FixtureRatelDeskConnectorApiService.SyntheticCredential, await page.ContentAsync());
        await page.GetByTestId("connector-rotate").ClickAsync();
        await Assertions.Expect(page.GetByTestId("connector-notice")).ToContainTextAsync("stored protected");
        await Assertions.Expect(password).ToHaveValueAsync("");
        Assert.True(fixture.ConnectorsData.ReceivedExpectedCredential);
        Assert.DoesNotContain(FixtureRatelDeskConnectorApiService.SyntheticCredential, fixture.ConnectorsData.GetResponseJson);
        Assert.DoesNotContain(FixtureRatelDeskConnectorApiService.SyntheticCredential, page.Url);
        Assert.False(await page.EvaluateAsync<bool>("secret => JSON.stringify(history.state).includes(secret)", FixtureRatelDeskConnectorApiService.SyntheticCredential));

        await page.GetByTestId("connector-name").FillAsync("Retain this explicit mapping");
        await page.GetByTestId("connector-name").PressAsync("Tab"); fixture.ConnectorsData.FailNextSave = true;
        await page.GetByTestId("connector-save").ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "revision changed" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("connector-name")).ToHaveValueAsync("Retain this explicit mapping");
        await page.GetByTestId("connector-tenant").SelectOptionAsync("23");
        await page.GetByRole(AriaRole.Button, new() { Name = "Keep editing", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("connector-name")).ToHaveValueAsync("Retain this explicit mapping");
        fixture.ConnectorsData.PreparePendingSave();
        await page.GetByTestId("connector-save").ClickAsync(); await fixture.ConnectorsData.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assertions.Expect(page.GetByTestId("connector-save")).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByTestId("connector-tenant")).ToBeDisabledAsync();
        Assert.Equal(2, fixture.ConnectorsData.SaveCalls); Assert.Equal(17, fixture.ConnectorsData.LastSaveTenant);
        fixture.ConnectorsData.CompletePendingSave();
        await Assertions.Expect(page.GetByTestId("connector-save")).ToBeEnabledAsync();
        await page.GetByTestId("connector-tenant").SelectOptionAsync("23");
        await Assertions.Expect(page.GetByTestId("connector-name")).ToHaveValueAsync("Tenant 23 connector");
        await Assertions.Expect(password).ToHaveValueAsync("");
        Assert.DoesNotContain("Retain this explicit mapping", await page.ContentAsync());
        Assert.DoesNotContain(FixtureRatelDeskConnectorApiService.SyntheticCredential, await page.ContentAsync());
        Assert.Equal("/flows?tenantId=23", await page.GetByRole(AriaRole.Link, new() { Name = "Return to Flows" }).GetAttributeAsync("href"));
        var root = EvidenceRoot(); Directory.CreateDirectory(root);
        await page.EvaluateAsync("() => scrollTo(0,0)");
        await page.ScreenshotAsync(new() { Path = Path.Combine(root, "connectors-rotation-tenant.png"), FullPage = true });
        await File.WriteAllTextAsync(Path.Combine(root, "connectors-boundaries.json"), JsonSerializer.Serialize(new { fixture.ConnectorsData.SaveCalls, fixture.ConnectorsData.LastSaveTenant, fixture.ConnectorsData.RotateCalls, receiverIncidentPosts = 0, credentialInGet = false, credentialInHistory = false }));
        Assert.DoesNotContain(fixture.StartupServerDiagnostics, m => m.Contains(FixtureRatelDeskConnectorApiService.SyntheticCredential, StringComparison.Ordinal));
    }

    private async Task OpenAsync(IPage page)
    {
        await page.GotoAsync(_fixture!.BaseAddress + "/flows/connectors?tenantId=17", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("connector-revisions").WaitForAsync(new() { Timeout = 90_000 });
    }
    private static async Task ReachableAsync(ILocator control, int width, int height)
    {
        await control.ScrollIntoViewIfNeededAsync(); await control.FocusAsync();
        var r = (await control.BoundingBoxAsync())!;
        Assert.True(r.Width > 0 && r.X >= -1 && r.X + r.Width <= width + 1);
        Assert.True(r.Y < height && r.Y + r.Height > 0);
    }
    private static async Task AssertNoOverflowAsync(IPage page) => Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth"));
    private static async Task SetThemeAsync(IPage page, string theme)
    {
        await page.EvaluateAsync("() => scrollTo(0,0)");
        var mobile = await page.GetByTestId("mobile-overflow").IsVisibleAsync();
        await page.GetByTestId(mobile ? "mobile-overflow" : "theme-preference-menu").ClickAsync();
        await page.GetByTestId((mobile ? "mobile-theme-option-" : "theme-option-") + theme).ClickAsync();
        if (theme == "system") await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await page.Locator("html[data-netratel-theme=" + (theme == "system" ? "dark" : theme) + "]").WaitForAsync();
    }
    private static string EvidenceRoot() => Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") is { Length: > 0 } configured ? configured : Path.Combine(AppContext.BaseDirectory, "TestResults", "playwright");
    public async ValueTask InitializeAsync() => _fixture = await ClientsManagementFixtureHost.StartAsync();
    public async ValueTask DisposeAsync() { if (_fixture is not null) await _fixture.DisposeAsync(); }
}

internal sealed class FixtureRatelDeskConnectorApiService : IRatelDeskConnectorApiService
{
    public const string SyntheticCredential = "rdk_synthetic_browser_password_never_in_receipts";
    private readonly Dictionary<int, RatelDeskConnectorDto> _connectors = new() { [17] = Create(17), [23] = Create(23) };
    private TaskCompletionSource<bool>? _pendingSave;
    public TaskCompletionSource<bool> SaveStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool FailNextSave, ReceivedExpectedCredential;
    public int SaveCalls, LastSaveTenant, RotateCalls, TestCalls, PreviewCalls;
    public string GetResponseJson => JsonSerializer.Serialize(_connectors.Values);
    public void PreparePendingSave() { SaveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); _pendingSave = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void CompletePendingSave() => _pendingSave?.TrySetResult(true);
    public Task<IReadOnlyList<RatelDeskConnectorTenantDto>> GetTenantsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RatelDeskConnectorTenantDto>>([new(17, "Explicit NetRatel tenant 17"), new(23, "Explicit NetRatel tenant 23")]);
    public Task<IReadOnlyList<RatelDeskConnectorDto>> ListAsync(int tenantId, CancellationToken ct) => Task.FromResult<IReadOnlyList<RatelDeskConnectorDto>>([_connectors[tenantId]]);
    public async Task<RatelDeskConnectorDto> SaveAsync(int tenantId, Guid id, SaveRatelDeskConnectorRequest request, CancellationToken ct)
    {
        SaveCalls++; LastSaveTenant = tenantId;
        if (FailNextSave) { FailNextSave = false; throw new RatelDeskConnectorApiException("connector-conflict"); }
        if (_pendingSave is not null) { SaveStarted.TrySetResult(true); await _pendingSave.Task.WaitAsync(ct); _pendingSave = null; }
        return _connectors[tenantId] = _connectors[tenantId] with { Id = id, Configuration = request.Configuration, Revision = request.ExpectedRevision + 1 };
    }
    public Task<RatelDeskConnectorDto> RotateAsync(int tenantId, Guid id, RotateRatelDeskConnectorCredentialRequest request, CancellationToken ct)
    {
        RotateCalls++; ReceivedExpectedCredential = request.Credential == SyntheticCredential;
        return Task.FromResult(_connectors[tenantId] = _connectors[tenantId] with { HasCredential = true, CredentialRevision = request.ExpectedCredentialRevision + 1 });
    }
    public Task<RatelDeskConnectionTestResult> TestAsync(int tenantId, Guid id, CancellationToken ct)
    { TestCalls++; return Task.FromResult(new RatelDeskConnectionTestResult(RatelDeskConnectionTestStatus.MappingValidated, "mapping-validated")); }
    public Task<RatelDeskDryRunResult> DryRunAsync(int tenantId, Guid id, RatelDeskDryRunRequest request, CancellationToken ct)
    {
        PreviewCalls++; var c = _connectors[tenantId].Configuration;
        return Task.FromResult(new RatelDeskDryRunResult(new(request.Title, "Plain sanitized description", request.Priority, c.CustomerId, c.OrganizationId, c.AssignedToId, c.CategoryIds), "fixture-fingerprint"));
    }
    private static RatelDeskConnectorDto Create(int tenant) => new(Guid.Parse($"00000000-0000-0000-0000-{tenant:D12}"), tenant, 1,
        new($"Tenant {tenant} connector", "https://approved-helpdesk.example.test", "explicit-organization-id", "explicit-requester-customer-id", "optional-assignee-id", [Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")], new(), true), true, 1, false, RatelDeskConnectorLimits.ReceiverUnavailableCode);
}
