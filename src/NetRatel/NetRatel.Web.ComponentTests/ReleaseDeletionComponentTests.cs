using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Forms;
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
    public void LocalImportsRemainVisibleWhenUpstreamDiscoveryIsEmptyOrUnavailable(bool unavailable)
    {
        _artifacts.UpstreamWarning = unavailable ? "GitHub rate limit reached; retry later." : null;
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=local-release-imports]").TextContent.Should().Contain(Version).And.Contain("Published");
            cut.Find("[data-testid=local-release-imports] button").TextContent.Should().Contain("Check source");
            DeleteButton(cut).TextContent.Should().Contain("Delete local release and files");
            if (unavailable)
                cut.Markup.Should().Contain("GitHub rate limit reached").And.Contain("GitHub discovery is unavailable");
            else
                cut.Markup.Should().Contain("No matching GitHub client releases");
        });
    }

    [Fact]
    public async Task BlockedDeletionShowsTheExactPlanAndDoesNotCallDelete()
    {
        _artifacts.Blockers = ["Tenant Canary pins this release for rollback."];
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => DeleteButton(cut).Should().NotBeNull());
        var operation = DeleteButton(cut).ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() =>
        {
            dialogs.Markup.Should().Contain("Local deletion blocked").And.Contain(Version)
                .And.Contain("linux-x64, win-x64").And.Contain("2 release records, 1 imports")
                .And.Contain("3.0 MiB reclaimable").And.Contain("4 historical attempts")
                .And.Contain("Tenant Canary pins this release for rollback");
            _artifacts.DeleteCalls.Should().Be(0);
        });
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Close").Click();
        await operation;
        _artifacts.PreviewVersions.Should().Equal(Version);
        _artifacts.DeleteCalls.Should().Be(0);
        DeleteButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Failed")]
    [InlineData("Deleted")]
    public async Task ConfirmedDeletionReportsItsActualCleanupStateAndPreservesTheCurrentChannel(string state)
    {
        _artifacts.DeletionState = state;
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => DeleteButton(cut).Should().NotBeNull());
        var channel = cut.FindComponents<MudSelect<string>>().Single(component => component.Instance.Label == "Channel");
        await channel.InvokeAsync(() => channel.Instance.ValueChanged.InvokeAsync("prerelease"));
        var operation = DeleteButton(cut).ClickAsync(new MouseEventArgs());
        dialogs.WaitForAssertion(() =>
        {
            dialogs.Markup.Should().Contain($"Delete local release {Version}?").And.Contain("3.0 MiB reclaimable")
                .And.Contain("Upstream releases and installed clients are unaffected");
            _artifacts.DeleteCalls.Should().Be(0, "preview and opening confirmation are read-only");
        });
        dialogs.FindAll("button").Single(button => button.TextContent.Trim() == "Delete local release and files").Click();
        await operation;
        cut.WaitForAssertion(() =>
        {
            _artifacts.DeleteCalls.Should().Be(1);
            _artifacts.DeletedVersions.Should().Equal(Version);
            var status = cut.Find("[data-testid=local-release-deletion-status]").TextContent;
            if (state == "Deleted")
                status.Should().Contain("owned files were deleted").And.Contain("Automatic re-import is suppressed");
            else
                status.Should().Contain($"cleanup is {state.ToLowerInvariant()}").And.Contain("Required cleanup has not finished")
                    .And.NotContain("owned files were deleted");
            cut.FindComponents<MudSelect<string>>().Single(component => component.Instance.Label == "Channel")
                .Instance.Value.Should().Be("prerelease");
            cut.Find(".mud-tab-active").TextContent.Should().Contain("GitHub releases");
            if (state is "Pending" or "Failed")
                cut.FindAll("button").Should().Contain(button => button.TextContent.Trim() == "Retry cleanup");
        });
    }

    private static AngleSharp.Dom.IElement DeleteButton(IRenderedComponent<ClientsMgmt> cut) =>
        cut.FindAll("[data-testid=local-release-imports] button")
            .Single(button => button.TextContent.Trim() == "Delete local release and files");

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
        public string DeletionState { get; set; } = "Deleted";
        public int DeleteCalls { get; private set; }
        public List<string> PreviewVersions { get; } = [];
        public List<string> DeletedVersions { get; } = [];
        private ClientReleaseDeletionStatusModel? _receipt;
        private static readonly Guid ImportId = Guid.Parse("6c874e17-645a-492b-909c-77d88e742a7a");

        public Task<GitHubClientReleasePageModel> GetGitHubReleasesAsync(string channel, int page, bool refresh, CancellationToken ct = default) =>
            Task.FromResult(new GitHubClientReleasePageModel { Page = page, Warning = UpstreamWarning, RefreshedAtUtc = DateTimeOffset.UtcNow });
        public Task<ClientReleaseAutomationModel> GetReleaseAutomationAsync(CancellationToken ct = default) =>
            Task.FromResult(new ClientReleaseAutomationModel { CheckEveryHours = 24, Revision = 1 });
        public Task<List<ClientReleaseImportModel>> GetReleaseImportsAsync(CancellationToken ct = default) =>
            Task.FromResult(_receipt?.State == "Deleted" ? [] : new List<ClientReleaseImportModel> { new() { Id = ImportId, GitHubReleaseId = 123,
                Version = Version, Tag = $"v{Version}", State = 5, PublishedAtUtc = DateTimeOffset.UtcNow, CreatedAtUtc = DateTimeOffset.UtcNow } });
        public Task<ClientReleaseDeletionPlanModel> PreviewLocalReleaseDeletionAsync(string version, CancellationToken ct = default)
        {
            PreviewVersions.Add(version);
            return Task.FromResult(new ClientReleaseDeletionPlanModel { Version = version, RuntimeIds = ["linux-x64", "win-x64"],
                ReleaseIds = [Guid.Parse("62b1b92a-9703-45d5-8975-59bf93f3142a"), Guid.Parse("d3c5da1b-2b5f-4f9f-b280-ecc80c62b8ac")],
                ImportIds = [ImportId], ReclaimableBytes = 3 * 1024 * 1024, HistoricalAttempts = 4, Blockers = Blockers });
        }
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
        public Task<ClientArtifactPageModel> GetArtifactsAsync(int page, int pageSize, string? rid = null, string? search = null, CancellationToken ct = default) => Task.FromResult(new ClientArtifactPageModel { Page = page, PageSize = pageSize });
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
