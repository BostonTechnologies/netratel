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
    [InlineData(1280, 800)]
    [InlineData(390, 844)]
    public async Task Final_mapping_form_has_readable_tenant_choices_and_reachable_actions(int width, int height)
    {
        var api = new PairingFixture();
        await using var host = await ClientsManagementFixtureHost.StartAsync(services => services.AddSingleton<IPairingApiClient>(api));
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync();
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(host.BaseAddress + "/account/integration-credentials", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var form = page.GetByTestId("pairing-final-form");
        await Assertions.Expect(form).ToBeVisibleAsync();
        var tenant = form.GetByTestId("pairing-netratel-tenant");
        var organization = form.GetByTestId("pairing-rateldesk-organization");
        await Assertions.Expect(tenant).ToContainTextAsync("Monitoring tenant");
        await Assertions.Expect(organization).ToContainTextAsync("Support organization");
        var positions = await form.EvaluateAsync<double[]>("e => {const a=e.querySelector('[data-testid=pairing-netratel-tenant]').getBoundingClientRect(); const b=e.querySelector('[data-testid=pairing-rateldesk-organization]').getBoundingClientRect(); return [a.top,a.right,b.top,b.left]}");
        if (width > 600) { Assert.True(Math.Abs(positions[0] - positions[2]) < 3); Assert.True(positions[1] <= positions[3]); }
        else Assert.True(positions[2] > positions[0]);
        await Assertions.Expect(form.GetByTestId("pairing-run-automation").Locator("input")).Not.ToBeCheckedAsync();
        await Assertions.Expect(form.GetByTestId("pairing-rateldesk-customer")).ToHaveCountAsync(0);
        await form.GetByTestId("pairing-create-incidents").Locator("input").CheckAsync();
        await Assertions.Expect(form.GetByTestId("pairing-rateldesk-customer")).ToBeVisibleAsync();
        foreach (var control in new[] { form.GetByTestId("pairing-save"), page.GetByTestId("connection-delete") })
        {
            await control.ScrollIntoViewIfNeededAsync(); await Assertions.Expect(control).ToBeVisibleAsync();
            Assert.True(await control.EvaluateAsync<bool>("e => {const r=e.getBoundingClientRect();return r.left>=-1&&r.right<=innerWidth+1&&r.width>0}"));
        }
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"));
        var evidence = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"pairing-final-form-{width}x{height}.png"), FullPage = true });
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
