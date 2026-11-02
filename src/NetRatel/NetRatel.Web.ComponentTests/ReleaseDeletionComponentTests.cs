using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Client;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Web.Components.Pages.Clients.ClientsMgmt;
using NetRatel.Web.Services;
using NetRatel.Web.Services.Tenants;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ReleaseDeletionComponentTests : AsyncBunitContext
{
    private const string Version = "0.4.102-rc.2";
    private readonly ReleaseFixture _artifacts = new();

    public ReleaseDeletionComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<IClientArtifactsService>(_artifacts);
        Services.AddSingleton<ITenantApiService, TenantFixture>();
        Services.AddSingleton<IClientUpdateActivityStream, InactiveActivityStream>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_imports_remain_in_the_unified_workspace_when_upstream_is_empty_or_unavailable(bool unavailable)
    {
        _artifacts.UpstreamWarning = unavailable ? "GitHub rate limit reached; retry later." : null;
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".mud-tab").Select(x => x.TextContent.Trim()).Should().Equal("GitHub releases", "Activity", "Suspended");
            cut.Find("[data-testid=github-release-table]").TextContent.Should().Contain(Version).And.Contain("Published").And.Contain("Local only");
            if (unavailable) cut.Markup.Should().Contain("GitHub rate limit reached");
            _artifacts.DeleteCalls.Should().Be(0);
        });
        await cut.InvokeAsync(() => cut.Find("[data-testid=release-details-toggle]").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid=release-details-row]").TextContent.Should()
            .Contain("Local assets").And.Contain("Version-wide local cleanup").And.Contain("4 historical attempts"));
    }

    [Fact]
    public async Task Unified_paging_preserves_selection_and_expansion_and_offers_20_50_100()
    {
        _artifacts.CatalogueCount = 125; _artifacts.ImportCount = 121;
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=github-release-name]").Should().HaveCount(20));
        var pager = cut.FindComponent<ClientManagementPager>();
        cut.Find("details[data-testid=release-automation-settings]").HasAttribute("open").Should().BeFalse();
        await cut.InvokeAsync(() => cut.Find("[data-testid=release-details-toggle]").Click());
        await cut.InvokeAsync(() => cut.FindAll("button").Single(x => x.TextContent.Trim() == "Select local versions on this page").Click());
        await pager.InvokeAsync(() => pager.Instance.PageChanged.InvokeAsync(1));
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid=github-release-name]")[0].TextContent.Should().Be("Release 20");
            cut.Find("[data-testid=release-selection-toolbar]").TextContent.Should().Contain("20 versions selected across pages");
        });
        await pager.InvokeAsync(() => pager.Instance.PageChanged.InvokeAsync(0));
        cut.WaitForAssertion(() => cut.Find("[data-testid=release-details-row]").TextContent.Should().Contain(Version));
        foreach (var size in new[] { 50, 100 })
        {
            await pager.InvokeAsync(() => pager.Instance.PageSizeChanged.InvokeAsync(size));
            cut.WaitForAssertion(() => cut.FindAll("[data-testid=github-release-name]").Should().HaveCount(size));
        }
        await pager.InvokeAsync(() => pager.Instance.PageChanged.InvokeAsync(1));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=github-release-name]").Should().HaveCount(25));
    }

    [Fact]
    public async Task Blocked_cleanup_keeps_selection_and_displays_a_supported_resolution_without_mutation()
    {
        _artifacts.Blockers = ["Tenant Canary pins this release for rollback."];
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => DeleteButton(cut).Should().NotBeNull());
        await cut.InvokeAsync(() => DeleteButton(cut).Click());
        dialogs.WaitForAssertion(() =>
        {
            dialogs.Markup.Should().Contain("Review local release cleanup").And.Contain(Version)
                .And.Contain("3.0 MiB").And.Contain("4 historical attempts").And.Contain("Tenant Canary pins");
            dialogs.Find("[data-testid=confirm-local-purge]").HasAttribute("disabled").Should().BeTrue();
            _artifacts.DeleteCalls.Should().Be(0);
        });
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(x => x.TextContent.Trim() == "Close").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid=release-selection-toolbar]").TextContent.Should().Contain("1 versions selected"));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Failed")]
    [InlineData("Deleted")]
    public async Task Reviewed_cleanup_reports_each_actual_state_and_retains_unresolved_selection(string state)
    {
        _artifacts.DeletionState = state;
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => DeleteButton(cut).Should().NotBeNull());
        await cut.InvokeAsync(() => DeleteButton(cut).Click());
        dialogs.WaitForAssertion(() => dialogs.Find("[data-testid=confirm-local-purge]").Should().NotBeNull());
        _artifacts.DeleteCalls.Should().Be(0);
        await dialogs.InvokeAsync(() => dialogs.Find("[data-testid=confirm-local-purge]").Click());
        dialogs.WaitForAssertion(() =>
        {
            _artifacts.DeleteCalls.Should().Be(1);
            _artifacts.DeletedVersions.Should().Equal(Version);
            dialogs.Markup.Should().Contain(state);
            if (state == "Deleted") dialogs.Markup.Should().Contain("Owned files removed");
            else dialogs.Markup.Should().Contain("No completed reclamation is claimed");
        });
        if (state != "Deleted") cut.Find("[data-testid=release-selection-toolbar]").TextContent.Should().Contain("1 versions selected");
    }

    [Fact]
    public async Task Mixed_bulk_cleanup_submits_only_eligible_versions_and_counts_unsubmitted_blockers()
    {
        _artifacts.ImportCount = 2;
        _artifacts.VersionBlockers["0.4.103"] = ["A tenant rollout pin still references this version."];
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=github-release-name]").Should().HaveCount(2));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(x => x.TextContent.Trim() == "Select local versions on this page").Click());
        await cut.InvokeAsync(() => cut.Find("button[aria-label='Preview purge of selected local versions']").Click());
        dialogs.WaitForAssertion(() => dialogs.Markup.Should().Contain("1 eligible").And.Contain("1 blocked"));
        await dialogs.InvokeAsync(() => dialogs.Find("[data-testid=confirm-local-purge]").Click());
        cut.WaitForAssertion(() =>
        {
            _artifacts.DeletedVersions.Should().Equal(Version);
            cut.Find("[data-testid=local-release-deletion-status]").TextContent.Should().Contain("1 version(s) deleted; 1 pending, failed or blocked");
            cut.Find("[data-testid=release-selection-toolbar]").TextContent.Should().Contain("1 versions selected across pages");
        });
    }

    [Fact]
    public async Task Disposed_preview_cannot_open_a_cleanup_dialog_or_delete()
    {
        _artifacts.PendingPreview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = Render<MudDialogProvider>();
        var host = Render<ReleaseHost>();
        var cut = host.FindComponent<ClientsMgmt>();
        cut.WaitForAssertion(() => DeleteButton(cut).Should().NotBeNull());
        var pending = cut.InvokeAsync(() => DeleteButton(cut).ClickAsync(new()));
        cut.WaitForAssertion(() => _artifacts.PreviewVersions.Should().Equal(Version));
        await host.InvokeAsync(host.Instance.HideClients);
        _artifacts.PendingPreview.SetResult(_artifacts.CreatePlan(Version));
        await pending;
        _artifacts.DeleteCalls.Should().Be(0);
        dialogs.Markup.Should().NotContain("Review local release cleanup");
    }

    private static AngleSharp.Dom.IElement DeleteButton(IRenderedComponent<ClientsMgmt> cut) =>
        cut.Find($"button[aria-label='Preview local purge for {Version}']");

    private sealed class ReleaseHost : ComponentBase
    {
        private bool _showClients = true;
        public ReleaseHost() { }
        public void HideClients() { _showClients = false; StateHasChanged(); }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (_showClients) { builder.OpenComponent<ClientsMgmt>(0); builder.CloseComponent(); }
        }
    }

    private sealed class InactiveActivityStream : IClientUpdateActivityStream
    {
        public Task StartAsync(int? tenantId, Func<ClientUpdateActivityEvent, Task> onNotification, Action<string> onStatus, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TenantFixture : ITenantApiService
    {
        public Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantDto>>([new(7, "Canary", null, null, [], null, null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        public Task CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateTenantAsync(int tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ReleaseFixture : IClientArtifactsService
    {
        public string? UpstreamWarning { get; set; }
        public string[] Blockers { get; set; } = [];
        public Dictionary<string, string[]> VersionBlockers { get; } = [];
        public string DeletionState { get; set; } = "Deleted";
        public int DeleteCalls { get; private set; }
        public int ImportCount { get; set; } = 1;
        public int CatalogueCount { get; set; }
        public int LastArtifactPageSize { get; private set; }
        public List<string> PreviewVersions { get; } = [];
        public List<string> DeletedVersions { get; } = [];
        public TaskCompletionSource<ClientReleaseDeletionPlanModel>? PendingPreview { get; set; }
        private ClientReleaseDeletionStatusModel? _receipt;
        private static readonly Guid ImportId = Guid.Parse("6c874e17-645a-492b-909c-77d88e742a7a");

        public async Task<ClientReleaseWorkspacePageModel> GetReleaseWorkspaceAsync(int page, int pageSize, string? search,
            string channel, string? runtime, string? localState, bool refresh, CancellationToken ct = default)
        {
            var imports = await GetReleaseImportsAsync(ct);
            var count = Math.Max(CatalogueCount, imports.Count);
            var rows = Enumerable.Range(0, count).Select(index => new ClientReleaseWorkspaceRowModel
            {
                Version = index == 0 ? Version : $"0.4.{102 + index}", Name = CatalogueCount > index ? $"Release {index}" : index == 0 ? Version : $"0.4.{102 + index}",
                Remote = CatalogueCount > index ? new() { Id = 123 + index, Version = index == 0 ? Version : $"0.4.{102 + index}" } : null,
                Imports = index < imports.Count ? [imports[index]] : [], Cleanup = index == 0 ? _receipt : null
            }).ToList();
            if (rows.Count == 0 && _receipt is not null) rows.Add(new() { Version = Version, Name = Version, Cleanup = _receipt });
            return new() { Page = page, PageSize = pageSize, Total = rows.Count, Items = rows.Skip(page * pageSize).Take(pageSize).ToList(),
                Warning = UpstreamWarning, SelectableVersions = rows.Where(x => x.HasLocal).Select(x => x.Version).Take(101).ToArray() };
        }
        public async Task<List<ClientReleaseDeletionPlanModel>> PreviewReleaseBatchAsync(string[] versions, CancellationToken ct = default)
        {
            var plans = new List<ClientReleaseDeletionPlanModel>();
            foreach (var version in versions) plans.Add(await PreviewLocalReleaseDeletionAsync(version, ct));
            return plans;
        }
        public async Task<List<ClientReleaseBatchResultModel>> DeleteReleaseBatchAsync(ClientReleaseDeletionPlanModel[] plans, bool disableAllRuntimes, CancellationToken ct = default)
        {
            var results = new List<ClientReleaseBatchResultModel>();
            foreach (var version in plans.Select(x => x.Version))
            {
                var receipt = await DeleteLocalReleaseAsync(version, ct);
                results.Add(new() { Version = version, State = receipt.State, Error = receipt.Error, Receipt = receipt });
            }
            return results;
        }
        public Task<GitHubClientReleasePageModel> GetGitHubReleasesAsync(string channel, int page, bool refresh, CancellationToken ct = default) =>
            Task.FromResult(new GitHubClientReleasePageModel
            {
                Page = page, Warning = UpstreamWarning, RefreshedAtUtc = DateTimeOffset.UtcNow,
                HasMore = (page + 1) * 20 < CatalogueCount,
                Items = Enumerable.Range(0, CatalogueCount).Skip(page * 20).Take(20)
                    .Select(index => new GitHubClientReleaseModel { Id = 123 + index, Name = $"Release {index}", Version = $"0.4.{102 + index}", DetailsUrl = "https://example.com/releases" }).ToList()
            });
        public Task<ClientReleaseAutomationModel> GetReleaseAutomationAsync(CancellationToken ct = default) =>
            Task.FromResult(new ClientReleaseAutomationModel { CheckEveryHours = 24, Revision = 1 });
        public Task<List<ClientReleaseImportModel>> GetReleaseImportsAsync(CancellationToken ct = default) =>
            Task.FromResult(_receipt?.State == "Deleted" ? [] : Enumerable.Range(0, ImportCount).Select(index => new ClientReleaseImportModel
            {
                Id = index == 0 ? ImportId : new Guid(index, 0, 0, new byte[8]), GitHubReleaseId = 123 + index,
                Version = index == 0 ? Version : $"0.4.{102 + index}", Tag = index == 0 ? $"v{Version}" : $"v0.4.{102 + index}",
                State = 5, PublishedAtUtc = DateTimeOffset.UtcNow,
                CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(-index)
            }).ToList());
        public Task<ClientReleaseDeletionPlanModel> PreviewLocalReleaseDeletionAsync(string version, CancellationToken ct = default)
        {
            PreviewVersions.Add(version);
            return PendingPreview?.Task ?? Task.FromResult(CreatePlan(version));
        }
        public ClientReleaseDeletionPlanModel CreatePlan(string version) =>
            new() { Version = version, RuntimeIds = ["linux-x64", "win-x64"],
                ReleaseIds = [Guid.Parse("62b1b92a-9703-45d5-8975-59bf93f3142a"), Guid.Parse("d3c5da1b-2b5f-4f9f-b280-ecc80c62b8ac")],
                ImportIds = [ImportId], ReclaimableBytes = 3 * 1024 * 1024, HistoricalAttempts = 4, Blockers = VersionBlockers.GetValueOrDefault(version) ?? Blockers };
        public Task<ClientReleaseDeletionStatusModel> DeleteLocalReleaseAsync(string version, CancellationToken ct = default)
        {
            DeleteCalls++;
            DeletedVersions.Add(version);
            _receipt = new() { Id = Guid.NewGuid(), Version = version, State = DeletionState, Suppressed = true, UpdatedAtUtc = DateTimeOffset.UtcNow,
                Error = DeletionState == "Failed" ? "Fixture disk cleanup failed." : null };
            return Task.FromResult(_receipt);
        }
        public Task<List<ClientReleaseDeletionStatusModel>> GetLocalReleaseDeletionsAsync(CancellationToken ct = default) =>
            Task.FromResult(_receipt is null ? [] : new List<ClientReleaseDeletionStatusModel> { _receipt });
        public Task<List<ClientArtifactSummaryModel>> ListAsync(string? rid, CancellationToken ct = default) => Task.FromResult(new List<ClientArtifactSummaryModel>());
        public Task<ClientArtifactPageModel> GetArtifactsAsync(int page, int pageSize, string? rid = null, string? search = null, CancellationToken ct = default)
        {
            LastArtifactPageSize = pageSize;
            return Task.FromResult(new ClientArtifactPageModel { Page = page, PageSize = pageSize });
        }
        public Task<List<ClientUpdateReleaseModel>> ListUpdateReleasesAsync(string? rid, CancellationToken ct = default) => Task.FromResult(new List<ClientUpdateReleaseModel>());
        public Task<ClientUpdateReleasePageModel> GetUpdateReleasePageAsync(int page, int pageSize, string? search = null, string? runtimeId = null, string? version = null, string? channel = null, bool? enabled = null, CancellationToken ct = default) => Task.FromResult(new ClientUpdateReleasePageModel { Page = page, PageSize = pageSize });
        public Task<List<ClientUpdateAttemptModel>> ListUpdateAttemptsAsync(CancellationToken ct = default) => Task.FromResult(new List<ClientUpdateAttemptModel>());
        public Task<ClientUpdateHistoryPageModel> GetUpdateHistoryAsync(int page, int pageSize, string? search = null, string? status = null, int? releaseId = null, string? runtimeId = null, int? tenantId = null, string? version = null, CancellationToken ct = default) => Task.FromResult(new ClientUpdateHistoryPageModel { Page = page, PageSize = pageSize });
        public Task<List<AgentClientUpdateStateModel>> ListUpdateStatesAsync(CancellationToken ct = default) => Task.FromResult(new List<AgentClientUpdateStateModel>());
        public Task<AgentClientUpdateStatePageModel> GetSuspendedAgentsAsync(int page, int pageSize, string? search = null, int? tenantId = null, CancellationToken ct = default) => Task.FromResult(new AgentClientUpdateStatePageModel { Page = page, PageSize = pageSize });
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
