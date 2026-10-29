using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Web.Components.Pages;
using NetRatel.Web.Services.Monitoring;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class OperationsDashboardTests : AsyncBunitContext
{
    public OperationsDashboardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("/js/operator-time.js").Setup<string>("getTimeZone").SetResult("UTC");
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IMonitoringApiService>(new MonitoringTestApi());
        Services.AddSingleton<IOperationsDashboardApiService>(new DashboardApi());
    }

    [Fact]
    public async Task Client_links_retain_the_selected_tenant_and_use_explicit_directory_filters()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/?tenantId=2");
        var cut = Render<Home>();
        cut.WaitForAssertion(() => Assert.Equal("/clients?tenantId=2&status=online",
            cut.Find("[data-testid='dashboard-online-clients']").GetAttribute("href")));
        Assert.Equal("/clients?tenantId=2&status=offline", cut.Find("[data-testid='dashboard-offline-clients']").GetAttribute("href"));
        Assert.Equal($"/clients?tenantId=2&clientId={DashboardApi.AgentId:D}",
            cut.Find(".dashboard-row-actions a:last-child").GetAttribute("href"));
        Assert.Equal($"/monitoring?tenantId=2&clientId={DashboardApi.AgentId:D}",
            cut.Find(".dashboard-row-actions a:first-child").GetAttribute("href"));

        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<int>>().Instance.ValueChanged.InvokeAsync(1));
        cut.WaitForAssertion(() => Assert.Equal("/clients?tenantId=1&status=online",
            cut.Find("[data-testid='dashboard-online-clients']").GetAttribute("href")));
        Assert.Equal($"/clients?tenantId=1&clientId={DashboardApi.AgentId:D}",
            cut.Find(".dashboard-row-actions a:last-child").GetAttribute("href"));
    }

    private sealed class DashboardApi : IOperationsDashboardApiService
    {
        public static readonly Guid AgentId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        public Task<OperationsDashboardDto> GetAsync(int tenantId, int page = 0, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OperationsDashboardDto(tenantId, 1, 1, 1, 1, 0, 0, 0, 1,
                [new(AgentId, new(AgentId, "Sample workstation", "SAMPLE-WS01", "192.0.2.12"), MonitoringSeverity.Warning, 1, 0, 1, 0, 0)],
                page, 25, DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<OperationsRecentJobDto>> GetJobsAsync(int tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OperationsRecentJobDto>>([]);
    }
}
