using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Web.Services.Monitoring;

namespace NetRatel.Web.PlaywrightTests;

/// <summary>Small opt-in rendered review in the existing authenticated application fixture.</summary>
[Collection(PlaywrightCollection.Name)]
[Trait("Category", "ManualAcceptance")]
public sealed class OperationsDashboardAcceptanceTests(ClientsManagementBrowserFixture browserFixture)
    : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private ClientsManagementFixtureHost? _host;
    private readonly DashboardFixture _dashboard = new();

    [Theory]
    [InlineData(1366, 768, "light")]
    [InlineData(1366, 768, "dark")]
    [InlineData(390, 844, "light")]
    [InlineData(390, 844, "dark")]
    public async Task Dashboard_has_readable_monitoring_identity_and_retains_truthful_counts_when_refresh_fails(int width, int height, string theme)
    {
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height }, TimezoneId = "Africa/Johannesburg" });
        var page = await context.NewPageAsync();
        await page.GotoAsync(_host!.BaseAddress + "/?tenantId=1");
        await page.GetByTestId("dashboard-alert-table").WaitForAsync();
        var mobile = await page.GetByTestId("mobile-overflow").IsVisibleAsync();
        var menu = page.GetByTestId(mobile ? "mobile-overflow" : "theme-preference-menu");
        await menu.ClickAsync();
        await page.GetByTestId($"{(mobile ? "mobile-theme-option" : "theme-option")}-{theme}").ClickAsync();
        await page.Locator($"html[data-netratel-theme='{theme}']").WaitForAsync();
        if (width >= 1366 && await page.GetByTestId("app-navigation-drawer").EvaluateAsync<bool>("element => element.classList.contains('mud-drawer--closed')"))
            await page.GetByTestId("navigation-toggle").ClickAsync();
        await Assertions.Expect(page.GetByTestId("dashboard-alert-table")).ToContainTextAsync("Finance workstation");
        await Assertions.Expect(page.GetByTestId("dashboard-alert-table")).ToContainTextAsync("FIN-WS01");
        await Assertions.Expect(page.GetByTestId("dashboard-alert-table")).ToContainTextAsync("192.0.2.12");
        await Assertions.Expect(page.GetByTestId("dashboard-alert-table")).ToContainTextAsync("All acknowledged");
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"));
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        var artifacts = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(artifacts);
        await page.ScreenshotAsync(new() { Path = Path.Combine(artifacts, $"dashboard-after-{theme}-{width}x{height}.png"), FullPage = true });
        _dashboard.Fail = true;
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh dashboard", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("dashboard-data-error")).ToContainTextAsync("last successful update");
        await Assertions.Expect(page.GetByTestId("dashboard-alert-table")).ToContainTextAsync("Finance workstation");
        Assert.DoesNotContain("No active alert", await page.GetByTestId("operations-dashboard").InnerTextAsync());
    }

    public async ValueTask InitializeAsync() => _host = await ClientsManagementFixtureHost.StartAsync(services =>
    {
        services.AddSingleton<IOperationsDashboardApiService>(_dashboard);
    });

    public async ValueTask DisposeAsync() { if (_host is not null) await _host.DisposeAsync(); }

    private sealed class DashboardFixture : IOperationsDashboardApiService
    {
        public bool Fail { get; set; }
        public Task<IReadOnlyList<OperationsRecentJobDto>> GetJobsAsync(int tenantId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OperationsRecentJobDto>>([]);
        public Task<OperationsDashboardDto> GetAsync(int tenantId, int page = 0, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new HttpRequestException("Dashboard data is unavailable. Retry to refresh it.");
            var agentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var client = new OperationsAlertClientDto(agentId, new(agentId, "Finance workstation", "FIN-WS01", "192.0.2.12"), MonitoringSeverity.Warning, 1, 1, 1, 0, 0);
            return Task.FromResult(new OperationsDashboardDto(tenantId, 183, 17, 1, 1, 2, 3, 0, 1, ImmutableArray.Create(client), page, 25, DateTimeOffset.UtcNow));
        }
    }

}
