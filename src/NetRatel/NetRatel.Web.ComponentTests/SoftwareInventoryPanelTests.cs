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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Installation_rows_expand_existing_details_without_extra_collapsed_rows(bool clientScoped)
    {
        var first = _api.Items[0] with
        {
            Package = _api.Items[0].Package with { InstallLocation = "/opt/example", InstallDate = "2026-11-01", Scope = "machine" }
        };
        var second = clientScoped
            ? first with { Identity = "other-package", Package = first.Package with { Name = "Other Tool" } }
            : first with { AgentId = Guid.NewGuid(), Client = "Another client" };
        _api.Items = [first, second];
        var cut = Render<SoftwareInventoryPanel>(parameters => parameters.Add(panel => panel.TenantId, 7)
            .Add(panel => panel.AgentId, clientScoped ? Agent : (Guid?)null));
        if (!clientScoped) cut.FindAll("[role='tab']").Single(tab => tab.TextContent.Trim() == "Installations").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='inventory-row']").Should().HaveCount(2));
        cut.FindAll("tbody > tr").Should().HaveCount(2);
        cut.FindAll("[data-testid='inventory-package-details']").Should().BeEmpty();
        cut.FindAll("[data-testid='inventory-details-toggle']").Should().OnlyContain(button => button.GetAttribute("aria-expanded") == "false");

        cut.FindAll("[data-testid='inventory-row']")[0].Closest("tr")!.Click();
        var details = cut.Find("[data-testid='inventory-package-details']");
        details.QuerySelectorAll("section").Select(section => section.GetAttribute("aria-label")).Should()
            .Equal("Package identity", "Installation", "Client and platform", "Collection and freshness");
        details.TextContent.Should().Contain("Example Tool").And.Contain("/opt/example").And.Contain("2026-11-01")
            .And.Contain("Example client").And.Contain("linux").And.Contain("x64").And.Contain("machine");
        details.Closest("td")!.GetAttribute("colspan").Should().Be(clientScoped ? "4" : "6");
        cut.FindAll("tbody > tr").Should().HaveCount(3);
        cut.FindAll("[data-testid='inventory-details-toggle']")[0].GetAttribute("aria-controls").Should().Be(details.Id);
        cut.FindAll("[data-testid='inventory-details-toggle']")[0].GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("[data-testid='inventory-details-toggle']")[1].GetAttribute("aria-expanded").Should().Be("false");

        cut.FindAll("[data-testid='inventory-details-toggle']")[1].Click();
        cut.FindAll("[data-testid='inventory-package-details']").Should().HaveCount(2);
        cut.FindAll("[data-testid='inventory-details-toggle']")[0].Click();
        cut.Find("[data-testid='inventory-package-details']").TextContent.Should().Contain(clientScoped ? "Other Tool" : "Another client");
        cut.FindAll("[data-testid='inventory-details-toggle']")[1].Click();
        cut.FindAll("[data-testid='inventory-package-details']").Should().BeEmpty();
        cut.FindAll("tbody > tr").Should().HaveCount(2);

        cut.FindAll("[data-testid='inventory-details-toggle']")[0].Click();
        cut.Render(parameters => parameters.Add(panel => panel.TenantId, 8));
        if (!clientScoped) cut.FindAll("[role='tab']").Single(tab => tab.TextContent.Trim() == "Installations").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='inventory-details-toggle']").Should().HaveCount(2)
            .And.OnlyContain(button => button.GetAttribute("aria-expanded") == "false"));
        cut.FindAll("[data-testid='inventory-package-details']").Should().BeEmpty();
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
    public void Cached_inventory_is_visible_before_a_slow_refresh_post_finishes()
    {
        _api.Status = _api.Status with { LastSuccessfulScanUtc = DateTimeOffset.UtcNow.AddMinutes(-31) };
        _api.PendingRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render<SoftwareInventoryPanel>(parameters => parameters.Add(panel => panel.TenantId, 7).Add(panel => panel.AgentId, Agent).Add(panel => panel.AutoRefreshOnOpen, true));
        cut.WaitForAssertion(() =>
        {
            _api.Forces.Should().Equal(false);
            _api.PendingRefresh.Task.IsCompleted.Should().BeFalse();
            cut.FindAll("[data-testid='inventory-row']").Should().ContainSingle();
            cut.Find("[data-testid='inventory-row']").TextContent.Should().Contain("Example Tool");
        });
        _api.PendingRefresh.SetResult(_api.Status with { LatestScan = new(Guid.NewGuid(), "Pending", DateTimeOffset.UtcNow, null, 0, 0, []) });
    }
    [Fact]
    public void Cached_inventory_does_not_wait_for_scan_status()
    {
        _api.PendingStatus = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render<SoftwareInventoryPanel>(parameters => parameters.Add(panel => panel.TenantId, 7).Add(panel => panel.AgentId, Agent));
        cut.WaitForAssertion(() =>
        {
            _api.PendingStatus.Task.IsCompleted.Should().BeFalse();
            cut.FindAll("[data-testid='inventory-row']").Should().ContainSingle();
        });
        _api.PendingStatus.SetResult(_api.Status);
    }
    [Fact]
    public void First_scan_shows_receiving_state_not_an_empty_search_result()
    {
        _api.Empty = true;
        _api.Status = _api.Status with { LastSuccessfulScanUtc = null, LatestScan = new(Guid.NewGuid(), "Collecting", DateTimeOffset.UtcNow, null, 1, 4, [], DateTimeOffset.UtcNow) };
        var cut = Render<SoftwareInventoryPanel>(parameters => parameters.Add(panel => panel.TenantId, 7).Add(panel => panel.AgentId, Agent).Add(panel => panel.AutoRefreshOnOpen, true));
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='inventory-empty']").TextContent.Should().Contain("First inventory is running");
            cut.Find("[data-testid='inventory-scan-status']").TextContent.Should().Contain("Received 1 of 4 chunks");
            cut.Find("[data-testid='inventory-refresh']").HasAttribute("disabled").Should().BeTrue();
        });
        _api.Forces.Should().BeEmpty();
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
        public IReadOnlyList<SoftwareInstallationDto> Items { get; set; } =
            [new(Agent, "Example client", "identity", new("Example Tool", "1", "Example", "x64", "example", "rpm"), "linux", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)];
        public SoftwareInventoryStatus Status { get; set; } = new(7, Agent, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7), null, new(), true);
        public List<bool> Forces { get; } = [];
        public bool Empty { get; set; }
        public TaskCompletionSource<SoftwareInventoryStatus>? PendingRefresh { get; set; }
        public TaskCompletionSource<SoftwareInventoryStatus>? PendingStatus { get; set; }
        public CancellationToken RefreshToken { get; private set; }
        public Task<SoftwareInventoryStatus> StatusAsync(int tenant, Guid agent, CancellationToken ct) => PendingStatus?.Task ?? Task.FromResult(Status with
        { TenantId = tenant, AgentId = agent, LastSuccessfulScanUtc = tenant == 7 ? Status.LastSuccessfulScanUtc : DateTimeOffset.UtcNow });
        public Task<SoftwareInventoryStatus> RefreshAsync(int tenant, Guid agent, bool force, CancellationToken ct)
        {
            Forces.Add(force);
            RefreshToken = ct;
            if (PendingRefresh is not null) return PendingRefresh.Task;
            return Task.FromResult(Status with { LatestScan = new(Guid.NewGuid(), "Pending", DateTimeOffset.UtcNow, null, 0, 0, []) });
        }
        public Task<SoftwarePage<SoftwareInstallationDto>> InstallationsAsync(int tenant, SoftwareInventoryQuery query, CancellationToken ct) => Task.FromResult(new SoftwarePage<SoftwareInstallationDto>(
            Empty ? [] : Items, Empty ? 0 : Items.Count, 0, 50, Empty ? 0 : Items.Select(item => item.AgentId).Distinct().Count()));
        public Task<SoftwarePage<SoftwarePrevalenceDto>> AggregateAsync(int tenant, SoftwareInventoryQuery query, CancellationToken ct) =>
            Task.FromResult(new SoftwarePage<SoftwarePrevalenceDto>([], 0, 0, 50));
        public Task<SoftwareInventorySummary> SummaryAsync(int tenant, CancellationToken ct) =>
            Task.FromResult(new SoftwareInventorySummary(0, 0, 0, 0, 0, 0, 0, 0, null));
    }
}
