using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Web.Services.Tenants;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class TenantUpdateChannelResponsiveTests(ClientsManagementBrowserFixture browserFixture)
    : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private readonly TenantPolicyFixture _tenants = new();
    private ClientsManagementFixtureHost? _fixture;

    [Theory]
    [Trait("Category", "ManualBrowserAcceptance")]
    [InlineData(1280, 800)]
    [InlineData(390, 844)]
    public async Task Metrics_is_the_fifth_tenant_section_on_desktop_and_phone(int width, int height)
    {
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(_fixture!.BaseAddress + "/tenants");
        await page.GetByRole(AriaRole.Button, new() { Name = "Manage Canary", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Edit Tenant", Exact = true }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        var switcher = dialog.GetByLabel("Tenant section", new() { Exact = true });
        await switcher.WaitForAsync(new() { State = WaitForSelectorState.Attached });
        if (await switcher.IsVisibleAsync())
        {
            await Assertions.Expect(switcher.Locator("option").Nth(4)).ToHaveTextAsync("Metrics");
            await switcher.SelectOptionAsync(new SelectOptionValue { Label = "Metrics" });
        }
        else
        {
            var metrics = dialog.GetByRole(AriaRole.Tab, new() { Name = "Metrics", Exact = true });
            await Assertions.Expect(dialog.GetByRole(AriaRole.Tab).Nth(4)).ToHaveTextAsync("Metrics");
            await metrics.PressAsync("Enter");
        }
        var retention = dialog.GetByRole(AriaRole.Spinbutton).First;
        await Assertions.Expect(retention).ToHaveValueAsync("14");
        await AssertFitsAsync(page, retention, width);
        await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Save metrics settings" })).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel tenant changes" }).ClickAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync();
        Assert.Null(_tenants.LastUpdate);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    [Theory]
    [InlineData(1280, 800)]
    [InlineData(390, 844)]
    public async Task Tenant_channel_and_installer_header_support_keyboard_and_all_themes(int width, int height)
    {
        foreach (var theme in new[] { "light", "dark", "system" })
        {
            _tenants.Reset();
            await using var context = await browserFixture.Browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = width, Height = height },
                ColorScheme = theme == "light" ? ColorScheme.Light : ColorScheme.Dark
            });
            await context.AddInitScriptAsync($"localStorage.setItem('netratel.theme.preference', '{theme}')");
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(30_000);
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(_fixture!.BaseAddress + "/tenants", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var manage = page.GetByRole(AriaRole.Button, new() { Name = "Manage Canary", Exact = true });
            await manage.WaitForAsync(new() { Timeout = 90_000 });
            await page.Locator($"html[data-netratel-theme={(theme == "light" ? "light" : "dark")}]").WaitForAsync();
            var drawer = page.GetByTestId("app-navigation-drawer");
            await Assertions.Expect(drawer).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(
                width < 1280 ? "mud-drawer--closed" : "mud-drawer--open"));
            await manage.FocusAsync();
            await manage.PressAsync("Enter");
            var edit = page.GetByRole(AriaRole.Menuitem, new() { Name = "Edit Tenant", Exact = true });
            await Assertions.Expect(edit).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Enter");
            var dialog = page.GetByRole(AriaRole.Dialog);
            await SelectUpdateSectionAsync(dialog);
            var channel = page.GetByRole(AriaRole.Combobox, new() { Name = "Release channel", Exact = true });
            await Assertions.Expect(channel).ToHaveTextAsync("Stable releases only");
            await Assertions.Expect(page.GetByTestId("tenant-target-version")).ToContainTextAsync("0.4.103-rc.1");
            await channel.FocusAsync();
            await channel.PressAsync("Space");
            await Assertions.Expect(channel).ToHaveAttributeAsync("aria-expanded", "true");
            var prerelease = page.GetByRole(AriaRole.Option, new() { Name = "Stable releases and prereleases", Exact = true });
            var optionId = await prerelease.GetAttributeAsync("id");
            Assert.False(string.IsNullOrWhiteSpace(optionId));
            await channel.PressAsync("ArrowDown");
            await Assertions.Expect(channel).ToHaveAttributeAsync("aria-activedescendant", optionId!);
            await channel.PressAsync("Enter");
            await Assertions.Expect(channel).ToHaveTextAsync("Stable releases and prereleases");
            await AssertFitsAsync(page, channel, width);
            var root = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACTS")
                ?? Path.Combine(Path.GetTempPath(), "netratel-tenant-update-policy");
            Directory.CreateDirectory(root);
            await page.ScreenshotAsync(new() { Path = Path.Combine(root, $"tenant-channel-{width}-{theme}.png"), FullPage = true });
            var save = page.GetByRole(AriaRole.Button, new() { Name = "Save tenant changes", Exact = true });
            await save.FocusAsync();
            await save.PressAsync("Enter");
            await Assertions.Expect(dialog).ToBeHiddenAsync();
            Assert.Equal("prerelease", _tenants.LastUpdate?.AutoUpdateChannel);
            Assert.Equal("0.4.103-rc.1", _tenants.LastUpdate?.AutoUpdateTargetVersion);
            Assert.Equal("Existing description", _tenants.LastUpdate?.Description);

            // A fresh page must display the actual saved channel, independently of global automation.
            await manage.ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = "Edit Tenant", Exact = true }).ClickAsync();
            await SelectUpdateSectionAsync(dialog);
            await Assertions.Expect(channel).ToHaveTextAsync("Stable releases and prereleases");
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel tenant changes", Exact = true }).ClickAsync();
            _tenants.Reset();
            await page.GotoAsync(_fixture.BaseAddress + "/clients/mgmt", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var header = page.GetByTestId("client-management-header");
            await Assertions.Expect(header).ToBeVisibleAsync();
            await Assertions.Expect(header).Not.ToContainTextAsync("Tenant rollout eligibility");
            var tabs = page.GetByTestId("client-management-tabs");
            var before = await tabs.BoundingBoxAsync();
            Assert.NotNull(before);
            await tabs.GetByRole(AriaRole.Tab, new() { Name = "Activity", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByTestId("attempt-table")).ToBeVisibleAsync();
            var after = await tabs.BoundingBoxAsync();
            Assert.NotNull(after);
            Assert.InRange(Math.Abs(after.Y - before.Y), 0, 1);
            await AssertFitsAsync(page, header, width);
            await page.ScreenshotAsync(new() { Path = Path.Combine(root, $"installer-header-{width}-{theme}.png"), FullPage = true });
            Assert.Empty(errors);
            Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        }
    }

    private static async Task SelectUpdateSectionAsync(ILocator dialog)
    {
        await dialog.WaitForAsync();
        var switcher = dialog.GetByLabel("Tenant section", new() { Exact = true });
        await switcher.WaitForAsync(new() { State = WaitForSelectorState.Attached });
        if (await switcher.IsVisibleAsync()) await switcher.SelectOptionAsync(new SelectOptionValue { Label = "Auto update" });
        else await dialog.GetByRole(AriaRole.Tab, new() { Name = "Auto Update", Exact = true }).ClickAsync();
    }

    private static async Task AssertFitsAsync(IPage page, ILocator control, int width)
    {
        await control.ScrollIntoViewIfNeededAsync();
        var bounds = await control.BoundingBoxAsync();
        Assert.NotNull(bounds);
        Assert.True(bounds.X >= -1 && bounds.X + bounds.Width <= width + 1);
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth"));
    }

    public async ValueTask InitializeAsync()
    {
        _fixture = await ClientsManagementFixtureHost.StartAsync(services => services.AddSingleton<ITenantApiService>(_tenants));
        _fixture.Data.PublishedPrereleases.Add(new()
        { Version = "0.4.103-rc.1", RuntimeId = "linux-x64", Channel = "prerelease", Enabled = true });
        var automation = await _fixture.Data.GetReleaseAutomationAsync();
        automation.DownloadPrerelease = true;
        automation.PublishAutomatically = true;
        automation.DeployPrereleaseAutomatically = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_fixture is not null) await _fixture.DisposeAsync();
    }

    private sealed class TenantPolicyFixture : ITenantApiService
    {
        private TenantDto _tenant = CreateTenant();
        internal UpdateTenantRequest? LastUpdate { get; private set; }
        internal void Reset() { _tenant = CreateTenant(); LastUpdate = null; }
        private static TenantDto CreateTenant() => new(7, "Canary", "Existing description", "Existing location",
            ["example.invalid"], "Existing contact", "contact@example.invalid", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { AutoUpdateChannel = "stable", AutoUpdateTargetVersion = "0.4.103-rc.1" };
        public Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantDto>>([_tenant]);
        public Task UpdateTenantAsync(int tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default)
        {
            LastUpdate = request;
            _tenant = _tenant with { AutoUpdateChannel = request.AutoUpdateChannel!, AutoUpdateTargetVersion = request.AutoUpdateTargetVersion };
            return Task.CompletedTask;
        }
        public Task CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
