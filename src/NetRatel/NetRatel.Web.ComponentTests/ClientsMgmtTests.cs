using Bunit;
using FluentAssertions;
using System.Net;
using System.Reflection;
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
            cut.Markup.Should().Contain("Release automation");
            cut.Markup.Should().Contain("Verification required");
            cut.Markup.Should().Contain("Packages");
            cut.Markup.Should().Contain("Auto-updates");
            cut.Markup.Should().Contain("Activity");
            cut.Markup.Should().Contain("Suspended");
            cut.Find("button[aria-label='Advanced: upload artifact']").Should().NotBeNull();
            _artifacts.ArtifactPageRequests.Should().Be(0);
            _artifacts.ReleasePageRequests.Should().Be(0);
        });

        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Trim() == "Packages").Click());
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Search artifacts");
            _artifacts.ArtifactPageRequests.Should().BeGreaterThan(0);
        });

        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Contains("Auto-updates")).Click());
        cut.WaitForAssertion(() => _artifacts.ReleasePageRequests.Should().BeGreaterThan(0));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(x => x.TextContent.Trim() == "Disable").Click());
        cut.WaitForAssertion(() => _artifacts.DisabledReleaseId.Should().Be(ReleaseId));

        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Contains("Activity")).Click());
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

        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Contains("Suspended")).Click());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("operator-review"));
        await cut.InvokeAsync(() => cut.FindAll("button").Single(x => x.TextContent.Trim() == "Resume future updates").Click());
        cut.WaitForAssertion(() => _artifacts.ResumedAgent.Should().Be((7, AgentId)));
    }

    [Fact]
    public async Task AutomationDrawer_KeepsDraftSeparateUntilSaved()
    {
        var cut = Render<ClientsMgmt>();

        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-settings-button']").Should().NotBeNull());
        await cut.InvokeAsync(() => cut.Find("[data-testid='automation-settings-button']").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-drawer']").Should().NotBeNull());

        var switches = cut.FindAll("input.mud-switch-input");
        switches.Should().HaveCountGreaterThanOrEqualTo(4);
        await cut.InvokeAsync(() => switches[1].Change(true));
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-unsaved']").Should().NotBeNull());

        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save policy").Click());
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='automation-unsaved']").Should().BeEmpty());
        _artifacts.SavedAutomation.DownloadPrerelease.Should().BeTrue();
    }

    [Fact]
    public async Task AutomationEditor_GuardsDuplicateSaves_AndPreservesDraftAfterFailure()
    {
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-settings-button']").Should().NotBeNull());
        await cut.InvokeAsync(() => cut.Find("[data-testid='automation-settings-button']").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-drawer']").Should().NotBeNull());

        await cut.InvokeAsync(() => cut.FindAll("input.mud-switch-input")[1].Change(true));
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-unsaved']").Should().NotBeNull());

        var pending = _artifacts.QueuePendingSave();
        var saveButton = cut.Find("[data-testid='save-automation-policy']");
        var saveTask = cut.InvokeAsync(() => saveButton.Click());
        await _artifacts.SaveStarted.Task;

        await cut.InvokeAsync(() => saveButton.Click());
        _artifacts.SaveCalls.Should().Be(1);
        cut.FindAll("input.mud-switch-input")[2].GetAttribute("disabled").Should().NotBeNull(
            "editable automation controls are deliberately locked while a save is in flight");

        pending.SetResult(new ClientReleaseAutomationModel
        {
            DownloadPrerelease = true,
            Revision = 1,
            UpdatedBy = "component-test"
        });
        await saveTask;
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='automation-unsaved']").Should().BeEmpty());

        await cut.InvokeAsync(() => cut.FindAll("input.mud-switch-input")[1].Change(false));
        var failedSave = new HttpRequestException("conflict", null, HttpStatusCode.Conflict);
        _artifacts.QueueFailedSave(failedSave);
        await cut.InvokeAsync(() => cut.Find("[data-testid='save-automation-policy']").Click());

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='automation-unsaved']").Should().NotBeNull();
            cut.Markup.Should().Contain("Another operator changed this policy");
        });
        _artifacts.SaveCalls.Should().Be(2);
    }

    [Fact]
    public async Task AutomationEditor_DoesNotExecuteSavedPolicyWithDirtyDraft()
    {
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-settings-button']").Should().NotBeNull());
        await cut.InvokeAsync(() => cut.Find("[data-testid='automation-settings-button']").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-drawer']").Should().NotBeNull());

        await cut.InvokeAsync(() => cut.FindAll("input.mud-switch-input")[1].Change(true));
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-unsaved']").Should().NotBeNull());

        var runButton = cut.Find("[data-testid='run-automation-policy']");
        runButton.GetAttribute("disabled").Should().NotBeNull();
        await cut.InvokeAsync(() => runButton.Click());
        _artifacts.CheckCalls.Should().Be(0);
        _artifacts.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task AutomationEditor_IgnoresOutOfOrderLoads_AndKeepsDirtyDraft()
    {
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-settings-button']").Should().NotBeNull());

        var stale = _artifacts.QueuePendingRead();
        var latest = _artifacts.QueuePendingRead();
        var loadMethod = typeof(ClientsMgmt).GetMethod("LoadAutomationAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        loadMethod.Should().NotBeNull();

        var staleTask = (Task)loadMethod!.Invoke(cut.Instance, null)!;
        var latestTask = (Task)loadMethod.Invoke(cut.Instance, null)!;

        latest.SetResult(new ClientReleaseAutomationModel { CheckEveryHours = 24, Revision = 2, UpdatedBy = "latest" });
        await latestTask;
        stale.SetResult(new ClientReleaseAutomationModel { CheckEveryHours = 12, Revision = 1, UpdatedBy = "stale" });
        await staleTask;
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Checks every 24h");
            var saved = (ClientReleaseAutomationModel?)typeof(ClientsMgmt)
                .GetField("_automationSaved", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(cut.Instance);
            saved!.UpdatedBy.Should().Be("latest");
        });

        await cut.InvokeAsync(() => cut.Find("[data-testid='automation-settings-button']").Click());
        await cut.InvokeAsync(() => cut.FindAll("input.mud-switch-input")[1].Change(true));
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-unsaved']").Should().NotBeNull());

        var ignored = _artifacts.QueuePendingRead();
        var dirtyLoad = (Task)loadMethod.Invoke(cut.Instance, null)!;
        ignored.SetResult(new ClientReleaseAutomationModel { CheckEveryHours = 0, Revision = 9, UpdatedBy = "ignored" });
        await dirtyLoad;
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='automation-unsaved']").Should().NotBeNull();
            var saved = (ClientReleaseAutomationModel?)typeof(ClientsMgmt)
                .GetField("_automationSaved", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(cut.Instance);
            saved!.Revision.Should().Be(2);
        });
    }

    [Fact]
    public async Task AutomationEditor_ExplainsPrerequisiteAndPreventsClosingDirtyDrawer()
    {
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-settings-button']").Should().NotBeNull());
        await cut.InvokeAsync(() => cut.Find("[data-testid='automation-settings-button']").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-drawer']").Should().NotBeNull());

        var switches = cut.FindAll("input.mud-switch-input");
        await cut.InvokeAsync(() => switches[3].Change(true));
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='automation-validation']").TextContent.Should().Contain("prerelease downloads");
            cut.Find("[data-testid='save-automation-policy']").GetAttribute("disabled").Should().NotBeNull();
        });

        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(tab => tab.TextContent.Trim() == "Packages").Click());
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Save or cancel the automation draft before changing tabs.");
            typeof(ClientsMgmt).GetField("_activeTab", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance).Should().Be(0);
        });

        await cut.InvokeAsync(() => cut.Find("[data-testid='close-automation-settings']").Click());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Unsaved automation changes are still open"));

        await cut.InvokeAsync(() => cut.Find("[data-testid='cancel-automation-policy']").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid='close-automation-settings']").Click());
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Unsaved automation changes are still open"));
    }

    private sealed class StubClientArtifactsService : IClientArtifactsService
    {
        private ClientReleaseAutomationModel _automation = new();
        private readonly Queue<Task<ClientReleaseAutomationModel>> _automationReads = new();
        private readonly Queue<Task<ClientReleaseAutomationModel>> _automationSaves = new();
        public TaskCompletionSource<bool> SaveStarted { get; private set; } = NewSignal();
        public ClientReleaseAutomationModel SavedAutomation => _automation;
        public int SaveCalls { get; private set; }
        public int CheckCalls { get; private set; }
        public Task<ClientReleaseAutomationModel> GetReleaseAutomationAsync(CancellationToken ct = default) =>
            _automationReads.Count > 0 ? _automationReads.Dequeue() : Task.FromResult(_automation);
        public Task<ClientReleaseAutomationModel> SaveReleaseAutomationAsync(ClientReleaseAutomationModel settings, CancellationToken ct = default)
        {
            SaveCalls++;
            if (_automationSaves.Count > 0)
            {
                SaveStarted.TrySetResult(true);
                return _automationSaves.Dequeue();
            }

            _automation = settings;
            _automation.Revision++;
            return Task.FromResult(_automation);
        }
        public Task<ClientReleaseAutomationModel> CheckReleasesNowAsync(CancellationToken ct = default)
        {
            CheckCalls++;
            _automation.NextCheckAtUtc = DateTimeOffset.UtcNow;
            return Task.FromResult(_automation);
        }

        public TaskCompletionSource<ClientReleaseAutomationModel> QueuePendingRead()
        {
            var pending = NewSource<ClientReleaseAutomationModel>();
            _automationReads.Enqueue(pending.Task);
            return pending;
        }

        public TaskCompletionSource<ClientReleaseAutomationModel> QueuePendingSave()
        {
            SaveStarted = NewSignal();
            var pending = NewSource<ClientReleaseAutomationModel>();
            _automationSaves.Enqueue(pending.Task);
            return pending;
        }

        public void QueueFailedSave(Exception exception) => _automationSaves.Enqueue(Task.FromException<ClientReleaseAutomationModel>(exception));

        private static TaskCompletionSource<T> NewSource<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static TaskCompletionSource<bool> NewSignal() => NewSource<bool>();

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
