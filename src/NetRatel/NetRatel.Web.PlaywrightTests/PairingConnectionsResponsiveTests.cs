using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NetRatel.Shared.SystemPairing;
using NetRatel.Web.Services.Pairing;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "BrowserAcceptance")]
public sealed class PairingConnectionsResponsiveTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1600, 900)]
    [InlineData(683, 384)]
    [InlineData(390, 844)]
    public async Task Final_mapping_form_has_readable_tenant_choices_and_reachable_actions(int width, int height)
    {
        var api = new PairingFixture();
        await using var host = await ClientsManagementFixtureHost.StartAsync(services => services.AddSingleton<IPairingApiClient>(api));
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(host.BaseAddress + "/account/integration-credentials", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("integration-credentials-client-ready").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await page.Locator("html[data-netratel-theme]").WaitForAsync();
        if (width < 1280)
        {
            var scrim = page.Locator(".mud-drawer-overlay--open .mud-overlay-scrim");
            if (await scrim.CountAsync() > 0) await scrim.ClickAsync(new() { Position = new() { X = width - 10, Y = height / 2f } });
            await Assertions.Expect(page.GetByTestId("app-navigation-drawer")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("mud-drawer--closed"));
        }
        await Assertions.Expect(page.GetByTestId("pairing-final-form")).ToHaveCountAsync(0);
        await page.GetByTestId("connection-view").ClickAsync();
        var form = page.GetByTestId("pairing-final-form");
        await Assertions.Expect(form).ToBeVisibleAsync();
        var tenant = form.GetByRole(AriaRole.Combobox, new() { Name = "NetRatel tenant", Exact = true });
        var organization = form.GetByRole(AriaRole.Combobox, new() { Name = "RatelDesk tenant / organization", Exact = true });
        await Assertions.Expect(tenant).ToContainTextAsync("Monitoring tenant");
        await Assertions.Expect(organization).ToContainTextAsync("Support organization");
        var positions = await form.EvaluateAsync<double[]>("e => {const a=e.querySelector('div[data-testid=pairing-netratel-tenant]').getBoundingClientRect(); const b=e.querySelector('div[data-testid=pairing-rateldesk-organization]').getBoundingClientRect(); return [a.top,a.right,b.top,b.left]}");
        if (width > 600) { Assert.True(Math.Abs(positions[0] - positions[2]) < 3); Assert.True(positions[1] <= positions[3]); }
        else Assert.True(positions[2] > positions[0]);
        await Assertions.Expect(form.GetByRole(AriaRole.Checkbox, new() { Name = "Run automation — RatelDesk → NetRatel", Exact = true })).Not.ToBeCheckedAsync();
        await Assertions.Expect(form.GetByRole(AriaRole.Combobox, new() { Name = "RatelDesk customer", Exact = true })).ToHaveCountAsync(0);
        await form.GetByRole(AriaRole.Checkbox, new() { Name = "Create incidents — NetRatel → RatelDesk", Exact = true }).CheckAsync();
        await Assertions.Expect(form.GetByRole(AriaRole.Combobox, new() { Name = "RatelDesk customer", Exact = true })).ToBeVisibleAsync();
        foreach (var control in new[] { page.GetByTestId("pairing-save") })
        {
            await control.ScrollIntoViewIfNeededAsync(); await Assertions.Expect(control).ToBeVisibleAsync();
            Assert.True(await control.EvaluateAsync<bool>("e => {const r=e.getBoundingClientRect();return r.left>=-1&&r.right<=innerWidth+1&&r.width>0}"));
        }
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"));
        var evidence = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"pairing-final-form-{width}x{height}.png"), FullPage = false, Animations = ScreenshotAnimations.Disabled });
        Assert.Empty(errors); Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    private sealed class PairingFixture : IPairingApiClient
    {
        private static readonly PairingConnectionDto Pair = new("pair", "pair", null,
            new(PairingProtocol.Contract, "rateldesk", Guid.NewGuid().ToString("D"), "Support desk", "https://desk.example.test", "https://api.desk.example.test", null), "Systems paired");
        public Task<PairingConnectionDto[]> ListAsync(CancellationToken ct = default) => Task.FromResult(new[] { Pair });
        public Task<PairingDirectories> DirectoryAsync(string pairId, CancellationToken ct = default) => Task.FromResult(new PairingDirectories(
            [new("17", "Monitoring tenant")], [new("org", "Support organization")], [new("customer", "Example customer", "org")]));
        public Task<PairingCodeResponse> GenerateCodeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PairingConnectionDto> ConnectAsync(PairingConnectRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PairingConnectionDto> SaveAsync(PairingMapping mapping, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PairingTestResult> TestAsync(PairingConnectionDto connection, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(PairingConnectionDto connection, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
