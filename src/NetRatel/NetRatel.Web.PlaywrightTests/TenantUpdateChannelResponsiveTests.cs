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
    [InlineData(1280, 800)]
    [InlineData(390, 844)]
    public async Task Tenant_channel_and_eligibility_controls_support_keyboard_and_all_themes(int width, int height)
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
            if (width < 1280 && !await drawer.EvaluateAsync<bool>("e => e.classList.contains('mud-drawer--closed')"))
                await page.GetByTestId("navigation-toggle").ClickAsync();
            await manage.FocusAsync();
            await manage.PressAsync("Enter");
            await page.GetByRole(AriaRole.Menuitem, new() { Name = "Edit Tenant", Exact = true }).PressAsync("Enter");
            var dialog = page.GetByRole(AriaRole.Dialog);
            var channel = page.GetByRole(AriaRole.Combobox, new() { Name = "Release channel", Exact = true });
            await Assertions.Expect(channel).ToHaveTextAsync("Stable releases only");
            await Assertions.Expect(page.GetByTestId("tenant-target-version")).ToContainTextAsync("0.4.103-rc.1");
            await channel.FocusAsync();
            await channel.PressAsync("Space");
            await page.GetByRole(AriaRole.Option, new() { Name = "Stable releases and prereleases", Exact = true }).PressAsync("Enter");
            await Assertions.Expect(channel).ToHaveTextAsync("Stable releases and prereleases");
            await AssertFitsAsync(page, channel, width);
            var root = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACTS")
                ?? Path.Combine(Path.GetTempPath(), "netratel-tenant-update-policy");
            Directory.CreateDirectory(root);
            await page.ScreenshotAsync(new() { Path = Path.Combine(root, $"tenant-channel-{width}-{theme}.png"), FullPage = true });
            var save = page.GetByRole(AriaRole.Button, new() { Name = "Save Changes", Exact = true });
            await save.FocusAsync();
            await save.PressAsync("Enter");
            await Assertions.Expect(dialog).ToBeHiddenAsync();
            Assert.Equal("prerelease", _tenants.LastUpdate?.AutoUpdateChannel);
            Assert.Equal("0.4.103-rc.1", _tenants.LastUpdate?.AutoUpdateTargetVersion);
            Assert.Equal("Existing description", _tenants.LastUpdate?.Description);

            // A fresh page must display the actual saved channel, independently of global automation.
            await manage.ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = "Edit Tenant", Exact = true }).ClickAsync();
            await Assertions.Expect(channel).ToHaveTextAsync("Stable releases and prereleases");
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            _tenants.Reset();
            await page.GotoAsync(_fixture.BaseAddress + "/clients/mgmt", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var explanation = page.GetByTestId("tenant-prerelease-excluded");
            await Assertions.Expect(explanation).ToContainTextAsync("0.4.103-rc.1", new() { Timeout = 90_000 });
            await Assertions.Expect(explanation).ToContainTextAsync("Excluded releases create no update attempts");
            await Assertions.Expect(page.GetByTestId("release-automation-settings")).ToContainTextAsync("Prerelease deployment: on");
            var policy = page.GetByRole(AriaRole.Combobox, new() { Name = "Tenant rollout policy", Exact = true });
            await policy.FocusAsync();
            await policy.PressAsync("Tab");
            await AssertFitsAsync(page, policy, width);
            await page.GetByTestId("tenant-update-policy").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(root, $"tenant-eligibility-{width}-{theme}.png"), FullPage = true });
            Assert.Empty(errors);
            Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        }
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
