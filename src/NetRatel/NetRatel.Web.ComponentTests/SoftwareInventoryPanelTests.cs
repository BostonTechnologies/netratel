using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.SoftwareInventory;
using NetRatel.Web.Components.SoftwareInventory;
using NetRatel.Web.Services.SoftwareInventory;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class SoftwareInventoryPanelTests : AsyncBunitContext
{
    private readonly Api _api = new();
    private static readonly Guid Agent = Guid.NewGuid();
    public SoftwareInventoryPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<ISoftwareInventoryApiService>(_api);
    }
    [Fact]
    public void Fresh_inventory_uses_cache_and_explicit_refresh_forces_a_new_request()
    {
        var cut = Render<SoftwareInventoryPanel>(parameters => parameters.Add(panel => panel.TenantId, 7).Add(panel => panel.AgentId, Agent).Add(panel => panel.AutoRefreshOnOpen, true));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='inventory-row']").Should().ContainSingle());
        _api.Forces.Should().BeEmpty();
        cut.Find("[data-testid='inventory-refresh']").Click();
        cut.WaitForAssertion(() => _api.Forces.Should().Equal(true));
        cut.Find("[data-testid='inventory-scan-status']").TextContent.Should().Contain("Queued");
    }
    [Fact]
    public void Stale_inventory_requests_one_non_forced_refresh_and_retains_cached_rows()
    {
        _api.Status = _api.Status with { LastSuccessfulScanUtc = DateTimeOffset.UtcNow.AddMinutes(-31), LatestScan = new(Guid.NewGuid(), "Partial", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 1, ["rpm_scan_failed"]) };
        var cut = Render<SoftwareInventoryPanel>(parameters => parameters.Add(panel => panel.TenantId, 7).Add(panel => panel.AgentId, Agent).Add(panel => panel.AutoRefreshOnOpen, true));
        cut.WaitForAssertion(() => _api.Forces.Should().Equal(false));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='inventory-row']").Should().ContainSingle());
        cut.Find("[data-testid='inventory-counts']").TextContent.Should().Contain("1 installed entries").And.Contain("1 distinct devices");
    }
    [Fact]
    public async Task Tenant_change_cancels_and_fences_a_late_automatic_refresh_response()
    {
        _api.Status = _api.Status with { LastSuccessfulScanUtc = DateTimeOffset.UtcNow.AddMinutes(-31) };
        _api.PendingRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render<SoftwareInventoryPanel>(parameters => parameters.Add(panel => panel.TenantId, 7).Add(panel => panel.AgentId, Agent).Add(panel => panel.AutoRefreshOnOpen, true));
        cut.WaitForAssertion(() => _api.Forces.Should().ContainSingle());
        cut.Render(parameters => parameters.Add(panel => panel.TenantId, 8));
        cut.WaitForAssertion(() =>
        {
            _api.RefreshToken.IsCancellationRequested.Should().BeTrue();
            cut.FindAll("[data-testid='inventory-row']").Should().ContainSingle();
        });
        _api.PendingRefresh.SetResult(_api.Status with { LatestScan = new(Guid.NewGuid(), "Error", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, ["old_tenant_error"]) });
        await cut.InvokeAsync(() => Task.CompletedTask);
        cut.Markup.Should().NotContain("old_tenant_error");
    }
    private sealed class Api : ISoftwareInventoryApiService
    {
        public SoftwareInventoryStatus Status { get; set; } = new(7, Agent, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7), null, new(), true);
        public List<bool> Forces { get; } = [];
        public TaskCompletionSource<SoftwareInventoryStatus>? PendingRefresh { get; set; }
        public CancellationToken RefreshToken { get; private set; }
        public Task<SoftwareInventoryStatus> StatusAsync(int tenant, Guid agent, CancellationToken ct) => Task.FromResult(Status with
        { TenantId = tenant, AgentId = agent, LastSuccessfulScanUtc = tenant == 7 ? Status.LastSuccessfulScanUtc : DateTimeOffset.UtcNow });
        public Task<SoftwareInventoryStatus> RefreshAsync(int tenant, Guid agent, bool force, CancellationToken ct)
        {
            Forces.Add(force);
            RefreshToken = ct;
            if (PendingRefresh is not null) return PendingRefresh.Task;
            return Task.FromResult(Status with { LatestScan = new(Guid.NewGuid(), "Pending", DateTimeOffset.UtcNow, null, 0, 0, []) });
        }
        public Task<SoftwarePage<SoftwareInstallationDto>> InstallationsAsync(int tenant, SoftwareInventoryQuery query, CancellationToken ct) => Task.FromResult(new SoftwarePage<SoftwareInstallationDto>(
            [new(Agent, "Example client", "identity", new("Example Tool", "1", "Example", "x64", "example", "rpm"), "linux", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)], 1, 0, 50, 1));
    }
}
