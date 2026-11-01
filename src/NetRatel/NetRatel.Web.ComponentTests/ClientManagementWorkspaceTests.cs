using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Web.Components.Pages.Clients.ClientsMgmt;
using NetRatel.Web.Services;
using NetRatel.Web.Services.Tenants;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientManagementWorkspaceTests : AsyncBunitContext
{
    private readonly WorkspaceArtifacts _artifacts = new();

    public ClientManagementWorkspaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<IClientArtifactsService>(_artifacts);
        Services.AddSingleton<ITenantApiService, Tenants>();
        Services.AddSingleton<IClientUpdateActivityStream, Activity>();
    }

    [Fact]
    public async Task Catalogue_size_controls_fetch_and_display_the_selected_size_and_reset_page_without_losing_channel()
    {
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=github-release-name]").Should().HaveCount(20));
        cut.Find("[data-testid=tenant-policy-details]").HasAttribute("open").Should().BeFalse();
        cut.Find("[data-testid=tenant-policy-details]").TextContent.Should().Contain("Global release automation");
        cut.FindAll("[data-testid=github-release-catalogue] [data-testid=local-release-imports]").Should().BeEmpty();
        var channel = cut.FindComponents<MudSelect<string>>().Single(item => item.Instance.Label == "Channel");
        await cut.InvokeAsync(() => channel.Instance.ValueChanged.InvokeAsync("stable"));
        await cut.InvokeAsync(() => cut.FindAll("[data-testid=github-pagination] button").Single(button => button.TextContent.Trim() == "Next").Click());
        cut.WaitForAssertion(() => _artifacts.CatalogueRequests.Last().Should().Be(("stable", 1, 20)));
        foreach (var size in new[] { 50, 100 })
        {
            var select = cut.FindComponents<MudSelect<int>>().Single(item => item.Instance.Label == "Rows per page");
            await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(size));
            cut.WaitForAssertion(() =>
            {
                _artifacts.CatalogueRequests.Last().Should().Be(("stable", 0, size));
                cut.FindAll("[data-testid=github-release-name]").Should().HaveCount(size);
                cut.Find("[data-testid=github-pagination]").TextContent.Should().Contain("Page 1");
            });
        }
    }

    [Fact]
    public async Task Local_imports_have_independent_paging_search_and_refresh_even_when_GitHub_fails()
    {
        _artifacts.FailCatalogue = true;
        var cut = Render<ClientsMgmt>();
        await OpenTabAsync(cut, "Local imports");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=local-release-imports] tbody tr").Should().HaveCount(20));
        var table = cut.FindComponent<MudTable<ClientReleaseImportModel>>();
        foreach (var size in new[] { 50, 100 })
        {
            await SetPageSizeAsync(table, size);
            cut.WaitForAssertion(() => cut.FindAll("[data-testid=local-release-imports] tbody tr").Should().HaveCount(size));
        }
        await cut.InvokeAsync(() => table.Instance.NavigateTo(1));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=local-release-imports] tbody tr").Should().HaveCount(20));
        var search = cut.FindComponents<MudTextField<string>>().Single(item => item.Instance.Label == "Search local imports");
        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("1.2.119"));
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid=local-release-imports] tbody tr").Should().ContainSingle();
            cut.Find("[data-testid=local-release-imports]").TextContent.Should().Contain("1.2.119");
            table.Instance.GetState(item => item.CurrentPage).Should().Be(0);
        });
        _artifacts.FailImports = true;
        await cut.InvokeAsync(() => cut.FindAll("[data-testid=local-imports-panel] button").Single(button => button.TextContent.Trim() == "Refresh imports").Click());
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=local-imports-error]").TextContent.Should().Contain("last loaded data is retained");
            cut.Find("[data-testid=local-release-imports]").TextContent.Should().Contain("1.2.119");
        });
    }

    [Theory]
    [InlineData("Packages")]
    [InlineData("Auto-updates")]
    [InlineData("Activity")]
    [InlineData("Suspended")]
    public async Task Server_backed_tabs_use_the_same_twenty_fifty_hundred_pager(string tab)
    {
        var cut = Render<ClientsMgmt>();
        await OpenTabAsync(cut, tab);
        cut.WaitForAssertion(() => _artifacts.TableRequests[tab].Should().Be((0, 20)));
        // Only the selected lazy tab has mounted a server-table pager at this point.
        var pager = cut.FindComponent<MudTablePager>();
        pager.Instance.PageSizeOptions.Should().Equal(20, 50, 100);
        foreach (var size in new[] { 50, 100 })
        {
            var select = pager.FindComponent<MudSelect<int>>();
            await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(size));
            cut.WaitForAssertion(() => _artifacts.TableRequests[tab].Should().Be((0, size)));
        }
    }

    private static Task OpenTabAsync(IRenderedComponent<ClientsMgmt> cut, string name) =>
        cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(tab => tab.TextContent.Trim() == name).Click());

    private static Task SetPageSizeAsync(IRenderedComponent<MudTable<ClientReleaseImportModel>> table, int size) =>
        table.InvokeAsync(() => table.FindComponent<MudTablePager>().FindComponent<MudSelect<int>>().Instance.ValueChanged.InvokeAsync(size));

    private sealed class Activity : IClientUpdateActivityStream
    {
        public Task StartAsync(int? tenantId, Func<ClientUpdateActivityEvent, Task> onNotification, Action<string> onStatus, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Tenants : ITenantApiService
    {
        public Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantDto>>([new(7, "Example tenant", null, null, [], null, null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        public Task CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateTenantAsync(int tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class WorkspaceArtifacts : IClientArtifactsService
    {
        public bool FailCatalogue { get; set; }
        public bool FailImports { get; set; }
        public List<(string Channel, int Page, int Size)> CatalogueRequests { get; } = [];
        public Dictionary<string, (int Page, int Size)> TableRequests { get; } = [];
        public Task<GitHubClientReleasePageModel> GetGitHubReleasesAsync(string channel, int page, bool refresh, CancellationToken ct = default) =>
            GetGitHubReleasesAsync(channel, page, 20, refresh, ct);
        public Task<GitHubClientReleasePageModel> GetGitHubReleasesAsync(string channel, int page, int pageSize, bool refresh, CancellationToken ct = default)
        {
            CatalogueRequests.Add((channel, page, pageSize));
            if (FailCatalogue) throw new HttpRequestException("Fixture upstream unavailable.");
            return Task.FromResult(new GitHubClientReleasePageModel
            {
                Page = page, HasMore = (page + 1) * pageSize < 120, RefreshedAtUtc = DateTimeOffset.UtcNow,
                Items = Enumerable.Range(0, 120).Skip(page * pageSize).Take(pageSize).Select(index => new GitHubClientReleaseModel
                { Id = index + 1, Name = $"Example release {index}", Version = $"1.2.{index}", PublicationState = "verification required", PublishedAtUtc = DateTimeOffset.UtcNow }).ToList()
            });
        }
        public Task<ClientReleaseAutomationModel> GetReleaseAutomationAsync(CancellationToken ct = default) => Task.FromResult(new ClientReleaseAutomationModel());
        public Task<List<ClientReleaseImportModel>> GetReleaseImportsAsync(CancellationToken ct = default)
        {
            if (FailImports) throw new HttpRequestException("Fixture imports unavailable.");
            return Task.FromResult(Enumerable.Range(0, 120).Select(index => new ClientReleaseImportModel
            { Id = Guid.NewGuid(), GitHubReleaseId = index + 1, Version = $"1.2.{index}", State = 5, CreatedAtUtc = DateTimeOffset.UnixEpoch.AddDays(index), PublishedAtUtc = DateTimeOffset.UnixEpoch }).ToList());
        }
        public Task<ClientArtifactPageModel> GetArtifactsAsync(int page, int pageSize, string? rid = null, string? search = null, CancellationToken ct = default)
        {
            TableRequests["Packages"] = (page, pageSize);
            return Task.FromResult(new ClientArtifactPageModel { Page = page, PageSize = pageSize, Total = 120 });
        }
        public Task<ClientUpdateReleasePageModel> GetUpdateReleasePageAsync(int page, int pageSize, string? search = null, string? runtimeId = null, string? version = null, string? channel = null, bool? enabled = null, CancellationToken ct = default)
        {
            if (enabled != true) TableRequests["Auto-updates"] = (page, pageSize);
            return Task.FromResult(new ClientUpdateReleasePageModel { Page = page, PageSize = pageSize, Total = enabled == true ? 0 : 120 });
        }
        public Task<ClientUpdateHistoryPageModel> GetUpdateHistoryAsync(int page, int pageSize, string? search = null, string? status = null, int? releaseId = null, string? runtimeId = null, int? tenantId = null, string? version = null, CancellationToken ct = default)
        {
            TableRequests["Activity"] = (page, pageSize);
            return Task.FromResult(new ClientUpdateHistoryPageModel { Page = page, PageSize = pageSize, Total = 120 });
        }
        public Task<AgentClientUpdateStatePageModel> GetSuspendedAgentsAsync(int page, int pageSize, string? search = null, int? tenantId = null, CancellationToken ct = default)
        {
            TableRequests["Suspended"] = (page, pageSize);
            return Task.FromResult(new AgentClientUpdateStatePageModel { Page = page, PageSize = pageSize, Total = 120 });
        }
        public Task<List<ClientArtifactSummaryModel>> ListAsync(string? rid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<ClientUpdateReleaseModel>> ListUpdateReleasesAsync(string? rid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<ClientUpdateAttemptModel>> ListUpdateAttemptsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<AgentClientUpdateStateModel>> ListUpdateStatesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task DisableReleaseAsync(Guid releaseId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ResumeAgentAsync(int tenantId, Guid agentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DownloadAsync(string rid, string version, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DownloadClientPackageAsync(ClientPackageDownloadRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DownloadDeploymentScriptAsync(ClientScriptGenerateRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string rid, string version, CancellationToken ct = default) => throw new NotSupportedException();
        public Task OpenUploadDialogAsync(string initialRid, Func<Task> onUploaded) => throw new NotSupportedException();
        public Task UploadAsync(string rid, string version, string? notes, IBrowserFile file, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
