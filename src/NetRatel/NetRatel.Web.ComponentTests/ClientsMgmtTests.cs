using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Web.Components.Pages.Clients.ClientsMgmt;
using NetRatel.Web.Services;
using NetRatel.Web.Services.Tenants;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientsMgmtTests : AsyncBunitContext
{
    private static readonly Guid ReleaseId = Guid.Parse("4df89355-f24b-4df6-a954-87d0ebcf6bf0");
    private static readonly Guid AgentId = Guid.Parse("628a2aa8-45bc-4fab-a424-afc26a3851dd");
    private readonly StubClientArtifactsService _artifacts = new();

    public ClientsMgmtTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<IClientArtifactsService>(_artifacts);
        Services.AddSingleton<ITenantApiService>(new StubTenantApiService());
    }

    [Fact]
    public async Task Renders_GitHub_Catalogue_And_Lazy_Management_Tabs_With_Paging_And_Friendly_Identity()
    {
        var cut = Render<ClientsMgmt>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("GitHub releases");
            cut.Markup.Should().Contain("Instance release automation");
            cut.Markup.Should().Contain("Verification required");
            cut.Markup.Should().Contain("Artifacts");
            cut.Markup.Should().Contain("Auto-update Releases");
            cut.Markup.Should().Contain("Update Attempts");
            cut.Markup.Should().Contain("Suspended Agents");
            _artifacts.ArtifactPageRequests.Should().Be(0);
            _artifacts.ReleasePageRequests.Should().Be(0);
        });

        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Trim() == "Artifacts").Click());
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Search artifacts");
            _artifacts.ArtifactPageRequests.Should().BeGreaterThan(0);
        });

        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Contains("Auto-update Releases")).Click());
        cut.WaitForAssertion(() => _artifacts.ReleasePageRequests.Should().BeGreaterThan(0));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(x => x.TextContent.Trim() == "Disable").Click());
        cut.WaitForAssertion(() => _artifacts.DisabledReleaseId.Should().Be(ReleaseId));

        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Contains("Update Attempts")).Click());
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("All states");
            cut.Markup.Should().Contain("Group current page");
            cut.Markup.Should().Contain("Canary");
            _artifacts.AttemptPageRequests.Should().BeGreaterThan(0);
        });
        await cut.InvokeAsync(() => cut.Find("button.mud-table-row-expander").Click());
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("activation_timeout");
            cut.Markup.Should().Contain("canary-host");
        });

        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Contains("Suspended Agents")).Click());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("operator-review"));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(x => x.TextContent.Trim() == "Resume future updates").Click());
        cut.WaitForAssertion(() => _artifacts.ResumedAgent.Should().Be((7, AgentId)));
    }

    private sealed class StubClientArtifactsService : IClientArtifactsService
    {
        private ClientReleaseAutomationModel _automation = new();
        public Task<ClientReleaseAutomationModel> GetReleaseAutomationAsync(CancellationToken ct = default) =>
            Task.FromResult(_automation);
        public Task<ClientReleaseAutomationModel> SaveReleaseAutomationAsync(ClientReleaseAutomationModel settings, CancellationToken ct = default)
        {
            _automation = settings;
            _automation.Revision++;
            return Task.FromResult(_automation);
        }
        public Task<ClientReleaseAutomationModel> CheckReleasesNowAsync(CancellationToken ct = default)
        {
            _automation.NextCheckAtUtc = DateTimeOffset.UtcNow;
            return Task.FromResult(_automation);
        }

        public Task<GitHubClientReleasePageModel> GetGitHubReleasesAsync(string channel, int page, bool refresh, CancellationToken ct = default) =>
            Task.FromResult(new GitHubClientReleasePageModel { Page = page, Items = [new GitHubClientReleaseModel
            {
                Id = 123,
                Tag = "v0.4.102",
                Version = "0.4.102",
                Name = "Fixture release",
                DetailsUrl = "https://github.com/BostonTechnologies/netratel/releases/tag/v0.4.102",
                PublicationState = "Verification required",
                PublishedAtUtc = DateTimeOffset.UtcNow
            }] });
        private readonly ClientUpdateReleaseModel _release = new()
        {
            ReleaseId = ReleaseId,
            RuntimeId = "win-x64",
            Version = "0.4.102",
            Channel = "stable",
            Sha256 = new string('a', 64),
            Enabled = true,
            PublishedAt = DateTimeOffset.UtcNow
        };

        public Guid? DisabledReleaseId { get; private set; }
        public (int TenantId, Guid AgentId)? ResumedAgent { get; private set; }
        public int ArtifactPageRequests { get; private set; }
        public int ReleasePageRequests { get; private set; }
        public int AttemptPageRequests { get; private set; }

        public Task<List<ClientArtifactSummaryModel>> ListAsync(string? rid, CancellationToken ct = default) =>
            Task.FromResult(new List<ClientArtifactSummaryModel>
            {
                new() { Rid = "win-x64", Version = "0.4.102", Sha256 = new string('a', 64), Size = 1024 }
            });

        public Task<ClientArtifactPageModel> GetArtifactsAsync(
            int page,
            int pageSize,
            string? rid = null,
            string? search = null,
            CancellationToken ct = default)
        {
            ArtifactPageRequests++;
            return Task.FromResult(new ClientArtifactPageModel
            {
                Total = 1,
                Page = page,
                PageSize = pageSize,
                Items = [new ClientArtifactSummaryModel { Rid = "win-x64", Version = "0.4.102", Sha256 = new string('a', 64), Size = 1024 }]
            });
        }

        public Task<List<ClientUpdateReleaseModel>> ListUpdateReleasesAsync(string? rid, CancellationToken ct = default) =>
            Task.FromResult(new List<ClientUpdateReleaseModel> { _release });

        public Task<ClientUpdateReleasePageModel> GetUpdateReleasePageAsync(
            int page,
            int pageSize,
            string? search = null,
            string? runtimeId = null,
            string? version = null,
            string? channel = null,
            bool? enabled = null,
            CancellationToken ct = default)
        {
            ReleasePageRequests++;
            return Task.FromResult(new ClientUpdateReleasePageModel
            {
                Total = 1,
                Page = page,
                PageSize = pageSize,
                Items = [_release]
            });
        }

        public Task<List<ClientUpdateAttemptModel>> ListUpdateAttemptsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<ClientUpdateAttemptModel>
            {
                new() { TenantId = 7, TenantName = "Canary", AgentId = AgentId, AgentName = "canary-agent", RuntimeId = "win-x64", Version = "0.4.102", Status = "RolledBack", FailureCode = "activation_timeout" }
            });

        public Task<ClientUpdateHistoryPageModel> GetUpdateHistoryAsync(
            int page,
            int pageSize,
            string? search = null,
            string? status = null,
            int? releaseId = null,
            string? runtimeId = null,
            int? tenantId = null,
            string? version = null,
            CancellationToken ct = default) =>
            GetHistoryPageAsync(page, pageSize);

        private Task<ClientUpdateHistoryPageModel> GetHistoryPageAsync(int page, int pageSize)
        {
            AttemptPageRequests++;
            return Task.FromResult(new ClientUpdateHistoryPageModel
            {
                Total = 1,
                Page = page,
                PageSize = pageSize,
                Items =
                [
                    new ClientUpdateHistoryItemModel
                    {
                        AttemptId = Guid.NewGuid(),
                        TenantId = 7,
                        TenantName = "Canary",
                        AgentId = AgentId,
                        ClientDisplayName = "Canary agent",
                        ClientHostName = "canary-host",
                        RuntimeId = "win-x64",
                        TargetVersion = "0.4.102",
                        Status = "RolledBack",
                        FailureCode = "activation_timeout",
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    }
                ]
            });
        }

        public Task<List<AgentClientUpdateStateModel>> ListUpdateStatesAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<AgentClientUpdateStateModel>
            {
                new() { AgentId = AgentId, TenantId = 7, TenantName = "Canary", AgentName = "canary-agent", ClientHostName = "canary-host", SuspendedAtUtc = DateTimeOffset.UtcNow, SuspensionReason = "operator-review" }
            });

        public Task<AgentClientUpdateStatePageModel> GetSuspendedAgentsAsync(
            int page,
            int pageSize,
            string? search = null,
            int? tenantId = null,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentClientUpdateStatePageModel
            {
                Total = 1,
                Page = page,
                PageSize = pageSize,
                Items =
                [
                    new AgentClientUpdateStateModel
                    {
                        AgentId = AgentId,
                        TenantId = 7,
                        TenantName = "Canary",
                        AgentName = "canary-agent",
                        ClientHostName = "canary-host",
                        SuspendedAtUtc = DateTimeOffset.UtcNow,
                        SuspensionReason = "operator-review"
                    }
                ]
            });

        public Task DisableReleaseAsync(Guid releaseId, CancellationToken ct = default)
        {
            DisabledReleaseId = releaseId;
            _release.Enabled = false;
            return Task.CompletedTask;
        }

        public Task ResumeAgentAsync(int tenantId, Guid agentId, CancellationToken ct = default)
        {
            ResumedAgent = (tenantId, agentId);
            return Task.CompletedTask;
        }

        public Task DownloadAsync(string rid, string version, CancellationToken ct = default) => Task.CompletedTask;
        public Task DownloadClientPackageAsync(ClientPackageDownloadRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task DownloadDeploymentScriptAsync(ClientScriptGenerateRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string rid, string version, CancellationToken ct = default) => Task.CompletedTask;
        public Task OpenUploadDialogAsync(string initialRid, Func<Task> onUploaded) => Task.CompletedTask;
        public Task UploadAsync(string rid, string version, string? notes, IBrowserFile file, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubTenantApiService : ITenantApiService
    {
        public Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantDto>>(
                [new TenantDto(7, "Canary", null, null, [], null, null, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);

        public Task CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateTenantAsync(int tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
