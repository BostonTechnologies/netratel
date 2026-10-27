using Bunit;
using AwesomeAssertions;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Client;
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
    private static readonly Guid AttemptId = Guid.Parse("7f745b7b-4060-4b88-9ed5-6eab40a6fb5c");
    private readonly StubClientArtifactsService _artifacts = new();
    private readonly StubActivityStream _activity = new();

    public ClientsMgmtTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddSingleton<IClientArtifactsService>(_artifacts);
        Services.AddSingleton<IClientUpdateActivityStream>(_activity);
        Services.AddSingleton<ITenantApiService>(new StubTenantApiService());
    }

    [Theory]
    [InlineData("stable", true)]
    [InlineData("prerelease", false)]
    public void Tenant_policy_identifies_enabled_published_prereleases_excluded_by_the_selected_channel(string channel, bool excluded)
    {
        _artifacts.PublishedPrereleases.Add(new()
        {
            Version = "0.4.103-rc.1", RuntimeId = "linux-x64", Channel = "prerelease", Enabled = true
        });
        _artifacts.PublishedPrereleases.Add(new()
        {
            Version = "0.4.104-rc.1", RuntimeId = "linux-x64", Channel = "prerelease", Enabled = false
        });
        var tenant = new TenantDto(7, "Canary", null, null, [], null, null, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        { AutoUpdateChannel = channel, AutoUpdateTargetVersion = "0.4.103-rc.1" };
        var cut = Render<TenantUpdatePolicySummary>(parameters => parameters.Add(component => component.Tenants, [tenant]));
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Global release automation downloads and publishes packages");
            cut.Markup.Should().Contain("Target version pin: 0.4.103-rc.1");
            cut.FindAll("[data-testid='tenant-prerelease-excluded']").Count.Should().Be(excluded ? 1 : 0);
            if (excluded)
            {
                var explanation = cut.Find("[data-testid='tenant-prerelease-excluded']").TextContent;
                explanation.Should().Contain("0.4.103-rc.1").And.NotContain("0.4.104-rc.1");
                explanation.Should().Contain("even when global prerelease deployment is on");
                explanation.Should().Contain("Excluded releases create no update attempts");
            }
        });
        Assert.Equal<(int, int, string?, bool?)?>((0, 10, "prerelease", true), _artifacts.EligibilityQuery);
        _artifacts.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task GenerationDisplaysTheCreatedSnapshotWhenConfigurationChangesAfterPreview()
    {
        var dialogProvider = Render<MudDialogProvider>();
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => typeof(ClientsMgmt)
            .GetField("_selectedTenantId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cut.Instance).Should().Be(7));
        var open = typeof(ClientsMgmt).GetMethod("OpenScript", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await cut.InvokeAsync(async () => await (Task)open.Invoke(cut.Instance,
            [new ClientArtifactSummaryModel { Rid = "win-x64", Version = "0.4.102" }])!);
        cut.Render();
        dialogProvider.WaitForAssertion(() => dialogProvider.FindComponent<ClientEndpointSummary>()
            .Instance.Endpoints.Should().Be(_artifacts.EndpointPreview));
        _artifacts.GeneratedLinkCalls.Should().Be(0, "preview must not create a link");

        await dialogProvider.InvokeAsync(() => dialogProvider.FindAll("button")
            .Single(button => button.TextContent.Trim() == "Generate link").Click());
        dialogProvider.WaitForAssertion(() =>
        {
            dialogProvider.FindComponent<ClientEndpointSummary>().Instance.Endpoints.Should().Be(_artifacts.GeneratedLink.Endpoints);
            dialogProvider.Markup.Should().Contain("The endpoints differ from the preview");
            dialogProvider.FindComponent<GeneratedInstallScriptPreview>().Instance.Script.Should().Be(_artifacts.GeneratedLink.Script);
            dialogProvider.FindComponents<InstallCodeBlock>().Select(component => component.Instance.Value)
                .Should().Equal(_artifacts.GeneratedLink.InstallCommand, _artifacts.GeneratedLink.PublicUrl);
        });
        _artifacts.GeneratedLinkCalls.Should().Be(1);
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
            _artifacts.ReleasePageRequests.Should().Be(1, "the bounded tenant eligibility summary reads published prereleases");
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
    public async Task AutomationEditor_DoesNotLetAnOlderReloadReplaceANewerSave()
    {
        _artifacts.SavedAutomation.CheckEveryHours = 12;
        _artifacts.SavedAutomation.Revision = 4;
        _artifacts.SavedAutomation.UpdatedBy = "saved-A";
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-settings-button']").Should().NotBeNull());

        var pendingRead = _artifacts.QueuePendingRead();
        var loadMethod = typeof(ClientsMgmt).GetMethod("LoadAutomationAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        loadMethod.Should().NotBeNull();
        var staleLoadTask = (Task)loadMethod!.Invoke(cut.Instance, null)!;

        await cut.InvokeAsync(() => cut.Find("[data-testid='automation-settings-button']").Click());
        await cut.InvokeAsync(() => cut.FindAll("input.mud-switch-input")[1].Change(true));
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-unsaved']").Should().NotBeNull());

        var pendingSave = _artifacts.QueuePendingSave();
        var saveTask = cut.InvokeAsync(() => cut.Find("[data-testid='save-automation-policy']").Click());
        await _artifacts.SaveStarted.Task;
        pendingSave.SetResult(new ClientReleaseAutomationModel
        {
            CheckEveryHours = 12,
            DownloadPrerelease = true,
            Revision = 5,
            UpdatedBy = "saved-B"
        });
        await saveTask;

        pendingRead.SetResult(new ClientReleaseAutomationModel
        {
            CheckEveryHours = 0,
            Revision = 4,
            UpdatedBy = "stale-A"
        });
        await staleLoadTask;
        cut.Render();

        var saved = (ClientReleaseAutomationModel?)typeof(ClientsMgmt)
            .GetField("_automationSaved", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cut.Instance);
        var draft = (ClientReleaseAutomationModel?)typeof(ClientsMgmt)
            .GetField("_automationDraft", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cut.Instance);
        saved!.CheckEveryHours.Should().Be(12);
        saved.DownloadPrerelease.Should().BeTrue();
        saved.Revision.Should().Be(5);
        saved.UpdatedBy.Should().Be("saved-B");
        draft!.CheckEveryHours.Should().Be(12);
        draft.DownloadPrerelease.Should().BeTrue();
        draft.Revision.Should().Be(5);
        draft.UpdatedBy.Should().Be("saved-B");
        typeof(ClientsMgmt).GetField("_automationLoading", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cut.Instance).Should().Be(false);
    }

    [Fact]
    public async Task AutomationEditor_IgnoresAnOlderReloadFailureAfterSave()
    {
        _artifacts.SavedAutomation.CheckEveryHours = 12;
        _artifacts.SavedAutomation.Revision = 4;
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-settings-button']").Should().NotBeNull());

        var pendingRead = _artifacts.QueuePendingRead();
        var loadMethod = typeof(ClientsMgmt).GetMethod("LoadAutomationAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        loadMethod.Should().NotBeNull();
        var staleLoadTask = (Task)loadMethod!.Invoke(cut.Instance, null)!;

        await cut.InvokeAsync(() => cut.Find("[data-testid='automation-settings-button']").Click());
        await cut.InvokeAsync(() => cut.FindAll("input.mud-switch-input")[1].Change(true));
        await cut.InvokeAsync(() => cut.Find("[data-testid='save-automation-policy']").Click());

        pendingRead.SetException(new HttpRequestException("Obsolete reload failure.", null, HttpStatusCode.ServiceUnavailable));
        await staleLoadTask;
        cut.Render();

        var saved = (ClientReleaseAutomationModel?)typeof(ClientsMgmt)
            .GetField("_automationSaved", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cut.Instance);
        saved!.DownloadPrerelease.Should().BeTrue();
        saved.Revision.Should().Be(5);
        typeof(ClientsMgmt).GetField("_automationError", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cut.Instance).Should().BeNull();
        cut.Markup.Should().NotContain("Could not load automation settings");
    }

    [Fact]
    public async Task AutomationEditor_GuardsDuplicateRunWhileConfirmingAndRunning()
    {
        _artifacts.SavedAutomation.PublishAutomatically = true;
        var dialogProvider = Render<MudDialogProvider>();
        var cut = Render<ClientsMgmt>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-settings-button']").Should().NotBeNull());
        await cut.InvokeAsync(() => cut.Find("[data-testid='automation-settings-button']").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid='automation-drawer']").Should().NotBeNull());

        var pendingCheck = _artifacts.QueuePendingCheck();
        var runMethod = typeof(ClientsMgmt).GetMethod("CheckReleasesNowAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        runMethod.Should().NotBeNull();
        var firstRunTask = cut.InvokeAsync(() => cut.Find("[data-testid='run-automation-policy']").Click());
        dialogProvider.WaitForAssertion(() => dialogProvider.Markup.Should().Contain("Run policy"));

        await (Task)runMethod!.Invoke(cut.Instance, null)!;
        _artifacts.CheckCalls.Should().Be(0, "the first invocation is still awaiting confirmation");
        cut.Find("[data-testid='run-automation-policy']").GetAttribute("disabled").Should().NotBeNull();

        var confirmButton = dialogProvider.FindAll("button").Single(button => button.TextContent.Trim() == "Run policy");
        await dialogProvider.InvokeAsync(() => confirmButton.Click());
        await _artifacts.CheckStarted.Task;
        _artifacts.CheckCalls.Should().Be(1);

        await (Task)runMethod.Invoke(cut.Instance, null)!;
        _artifacts.CheckCalls.Should().Be(1, "the first invocation is still running");
        pendingCheck.SetResult(new ClientReleaseAutomationModel
        {
            PublishAutomatically = true,
            Revision = _artifacts.SavedAutomation.Revision,
            NextCheckAtUtc = DateTimeOffset.UtcNow
        });
        await firstRunTask;
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='automation-saving']").Should().BeEmpty());
        _artifacts.CheckCalls.Should().Be(1);
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
            cut.FindAll(".mud-tab").Single(tab => tab.GetAttribute("aria-selected") == "true").TextContent.Trim().Should().Be("GitHub releases");
        });

        await cut.InvokeAsync(() => cut.Find("[data-testid='close-automation-settings']").Click());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Unsaved automation changes are still open"));

        await cut.InvokeAsync(() => cut.Find("[data-testid='cancel-automation-policy']").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid='close-automation-settings']").Click());
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Unsaved automation changes are still open"));
    }

    [Fact]
    public async Task Activity_Reconciles_StatusMembership_Bursts_And_Reconnect_Without_Resetting_Filters_Or_Groups()
    {
        var cut = Render<ClientsMgmt>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".mud-tab").Should().NotBeEmpty());
        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Trim() == "Activity").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => _activity.Starts.Should().Be(1));
        cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Search attempts").Instance.Value.Should().BeNullOrEmpty();
        cut.FindComponents<MudSelect<string>>().Single(field => field.Instance.Label == "Group current page").Instance.Value.Should().Be("Tenant");
        // Parameter reconciliation can echo empty and null with the same effective
        // query. It must not cancel the live session or replace its snapshot scope.
        await cut.InvokeAsync(async () =>
        {
            foreach (var method in new[] { "OnAttemptSearchChanged", "OnAttemptRidChanged", "OnAttemptVersionChanged", "OnAttemptStatusChanged" })
                await (Task)typeof(ClientsMgmt).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cut.Instance, [string.Empty])!;
        });
        _activity.Starts.Should().Be(1);
        await cut.InvokeAsync(() => _activity.SendAsync("ready"));
        await cut.WaitForAssertionAsync(() =>
        {
            cut.Find("[data-testid='activity-live-status']").TextContent.Should().Contain("Live");
            _activity.Starts.Should().Be(1);
            cut.FindAll(".mud-tab").Single(tab => tab.GetAttribute("aria-selected") == "true").TextContent.Trim().Should().Be("Activity");
        });
        await cut.InvokeAsync(() => cut.Find("button.mud-table-row-expander").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("activation_timeout"));
        var before = _artifacts.AttemptPageRequests;
        _artifacts.HistoryStatus = "Accepted";
        _artifacts.HistoryPurged = true;
        await cut.InvokeAsync(async () => { for (var index = 0; index < 30; index++) await _activity.SendAsync("invalidate"); });
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Accepted").And.Contain("Historical · local package deleted"));
        _artifacts.AttemptPageRequests.Should().Be(before + 1, "a burst refreshes one current filtered page");
        cut.Find("button.mud-table-row-expander").GetAttribute("aria-expanded").Should().Be("true", "the expanded group remains open after refresh");
        await cut.InvokeAsync(() => _activity.Disconnect());
        await cut.InvokeAsync(() => cut.Find("[data-testid='activity-refresh']").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='activity-live-status']").TextContent.Should().Contain("Reconnecting", "a manual snapshot does not make a disconnected stream Live"));
        await cut.InvokeAsync(() => _activity.SendAsync("ready"));
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='activity-live-status']").TextContent.Should().Contain("Live"));

        var statusChanged = typeof(ClientsMgmt).GetMethod("OnAttemptStatusChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await cut.InvokeAsync(async () => await (Task)statusChanged.Invoke(cut.Instance, ["Accepted"])!);
        await cut.WaitForAssertionAsync(() => _artifacts.LastHistoryStatus.Should().Be("Accepted"));
        _artifacts.HistoryStatus = "RolledBack";
        await cut.InvokeAsync(() => _activity.SendAsync("invalidate"));
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("No update attempts match the active filters."));
        _artifacts.LastHistoryStatus.Should().Be("Accepted");

        // A missed change enters this status filter while disconnected. Ready always reconciles.
        _artifacts.HistoryStatus = "Accepted";
        await cut.InvokeAsync(() => _activity.SendAsync("ready"));
        await cut.WaitForAssertionAsync(() => cut.Find("button.mud-table-row-expander").GetAttribute("aria-expanded").Should().Be("true", "expansion is retained when the group leaves and reenters the filtered page"));
        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Trim() == "Packages").ClickAsync(new()));
        _activity.Active.Should().BeFalse();
        var inactiveReads = _artifacts.AttemptPageRequests;
        await cut.InvokeAsync(() => _activity.SendAsync("invalidate"));
        _artifacts.AttemptPageRequests.Should().Be(inactiveReads);
    }

    [Fact]
    public async Task Activity_keeps_confirmation_authority_distinct_from_reported_rollback_and_observed_running_version()
    {
        _artifacts.HistoryStatus = "Accepted";
        _artifacts.HistoryReconciliationRequired = true;
        var cut = Render<ClientsMgmt>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".mud-tab").Should().NotBeEmpty());
        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Trim() == "Activity").ClickAsync(new()));
        await cut.InvokeAsync(() => _activity.SendAsync("ready"));
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Reconciliation required"));
        await cut.InvokeAsync(() => cut.Find("button.mud-table-row-expander").ClickAsync(new()));
        await cut.InvokeAsync(() => cut.Find("[data-testid='attempt-details-toggle']").ClickAsync(new()));
        cut.Markup.Should().Contain("Target 0.4.102").And.Contain("Running observed: 0.4.101")
            .And.Contain("Accepted").And.Contain("Installation: previous version restored")
            .And.Contain("Server report: conflicts with accepted confirmation")
            .And.Contain("Automatic updates suspended")
            .And.Contain("2026-10-14 06:12:00 UTC").And.Contain("before resuming automatic updates");
        cut.Find("[aria-label='Client report']").TextContent.Should().Contain("Reported installation").And.Contain("RolledBack");
    }

    [Fact]
    public async Task Activity_details_expand_below_the_summary_and_follow_group_visibility_and_live_refresh()
    {
        var cut = Render<ClientsMgmt>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".mud-tab").Should().NotBeEmpty());
        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Trim() == "Activity").ClickAsync(new()));
        await cut.InvokeAsync(() => _activity.SendAsync("ready"));
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='activity-live-status']").TextContent.Should().Contain("Live"));
        await cut.InvokeAsync(() => cut.Find("button.mud-table-row-expander").ClickAsync(new()));

        cut.FindAll("[data-testid='attempt-details-row']").Should().BeEmpty();
        var toggle = cut.Find("[data-testid='attempt-details-toggle']");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        await cut.InvokeAsync(() => toggle.ClickAsync(new()));
        var summaryRow = cut.Find("[data-testid='attempt-details-toggle']").Closest("tr")!;
        summaryRow.Children.Should().HaveCount(5);
        summaryRow.QuerySelector(".attempt-details").Should().BeNull();
        var detailsRow = cut.Find("[data-testid='attempt-details-row']");
        summaryRow.NextElementSibling!.GetAttribute("data-testid").Should().Be("attempt-details-row");
        detailsRow.QuerySelector("td")!.GetAttribute("colspan").Should().Be("5");
        detailsRow.TextContent.Should().Contain(AttemptId.ToString()).And.Contain("Installed versions").And.Contain("Server confirmation");

        await cut.InvokeAsync(() => cut.Find("button.mud-table-row-expander").ClickAsync(new()));
        cut.Find("[data-testid='attempt-details-row']").GetAttribute("style").Should().Contain("display:none");
        await cut.InvokeAsync(() => cut.Find("button.mud-table-row-expander").ClickAsync(new()));
        var reads = _artifacts.AttemptPageRequests;
        _artifacts.HistoryStatus = "Accepted";
        await cut.InvokeAsync(() => _activity.SendAsync("invalidate"));
        await cut.WaitForAssertionAsync(() =>
        {
            _artifacts.AttemptPageRequests.Should().BeGreaterThan(reads);
            cut.Find("[data-testid='attempt-details-toggle']").GetAttribute("aria-expanded").Should().Be("true");
            cut.Find("[data-testid='attempt-details-row']").TextContent.Should().Contain(AttemptId.ToString());
        });
        await cut.InvokeAsync(() => cut.Find("[data-testid='attempt-details-row'] .mud-expand-panel-header").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid='attempt-details-row']").Should().BeEmpty());
        cut.Find("[data-testid='attempt-details-toggle']").GetAttribute("aria-expanded").Should().Be("false");
    }

    [Fact]
    public async Task Activity_preserves_the_current_page_and_clamps_only_when_the_final_page_disappears()
    {
        _artifacts.HistoryTotal = 21;
        var cut = Render<ClientsMgmt>();
        await cut.WaitForAssertionAsync(() => cut.FindAll(".mud-tab").Should().NotBeEmpty());
        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Trim() == "Activity").ClickAsync(new()));
        await cut.InvokeAsync(() => _activity.SendAsync("ready"));
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='activity-live-status']").TextContent.Should().Contain("Live"));
        var table = (MudTable<ClientUpdateHistoryItemModel>)typeof(ClientsMgmt).GetField("_attemptTable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance)!;
        await cut.InvokeAsync(() => table.NavigateTo(1));
        await cut.WaitForAssertionAsync(() => _artifacts.LastHistoryPage.Should().Be(1));
        var before = _artifacts.AttemptPageRequests;
        await cut.InvokeAsync(() => _activity.SendAsync("invalidate"));
        await cut.WaitForAssertionAsync(() => { _artifacts.AttemptPageRequests.Should().BeGreaterThan(before); _artifacts.LastHistoryPage.Should().Be(1); });
        table.CurrentPage.Should().Be(1);
        _artifacts.HistoryTotal = 1;
        await cut.InvokeAsync(() => _activity.SendAsync("invalidate"));
        await cut.WaitForAssertionAsync(() =>
        {
            table.CurrentPage.Should().Be(0);
            _artifacts.LastHistoryPage.Should().Be(0);
        });
    }

    [Fact]
    public async Task Activity_older_stop_completion_does_not_duplicate_the_replacement_scopes_burst_refresh()
    {
        var cut = Render<ClientsMgmt>();
        async Task AwaitScheduledRefreshAsync()
        {
            // Notification callbacks schedule the coalescer. Await its actual
            // reload outside the renderer before asserting its displayed snapshot.
            Task? refresh = null;
            await cut.InvokeAsync(() =>
            {
                refresh = (Task?)typeof(ClientsMgmt).GetField("_activityRefreshTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance);
            });
            if (refresh is not null) await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await cut.WaitForAssertionAsync(() => cut.FindAll(".mud-tab").Should().NotBeEmpty());
        await cut.InvokeAsync(() => cut.FindAll(".mud-tab").Single(x => x.TextContent.Trim() == "Activity").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => _activity.Starts.Should().Be(1));
        await cut.InvokeAsync(() => _activity.SendAsync("ready"));
        await AwaitScheduledRefreshAsync();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='activity-live-status']").TextContent.Should().Contain("Live"));
        var pendingStop = _activity.HoldNextStop();
        var stop = typeof(ClientsMgmt).GetMethod("StopActivityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var olderStop = cut.InvokeAsync(async () => await (Task)stop.Invoke(cut.Instance, null)!);
        try
        {
            await pendingStop.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            _artifacts.HistoryStatus = "Accepted";
            var change = typeof(ClientsMgmt).GetMethod("OnAttemptStatusChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await cut.InvokeAsync(async () => await (Task)change.Invoke(cut.Instance, ["Accepted"])!);
            _activity.Starts.Should().Be(2);
            _activity.Active.Should().BeTrue();
            var before = _artifacts.AttemptPageRequests;
            await cut.InvokeAsync(() => _activity.SendAsync("ready"));
            pendingStop.Drain.TrySetResult();
            await olderStop;
            await cut.InvokeAsync(async () => { for (var index = 0; index < 30; index++) await _activity.SendAsync("invalidate"); });
            await AwaitScheduledRefreshAsync();
            await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='activity-live-status']").TextContent.Should().Contain("Live"));
            _artifacts.AttemptPageRequests.Should().Be(before + 1, "the older shutdown cannot erase the replacement's coalescer ownership");
            _artifacts.LastHistoryStatus.Should().Be("Accepted");
            _activity.Active.Should().BeTrue();

            // A third immediate scope change cancels an earlier Start waiting for
            // transport shutdown. That old input must not fault the UI callback.
            var queuedStart = _activity.HoldNextStart();
            try
            {
                var supersededChange = cut.InvokeAsync(async () => await (Task)change.Invoke(cut.Instance, ["Downloading"])!);
                await queuedStart.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await cut.InvokeAsync(async () => await (Task)change.Invoke(cut.Instance, ["Accepted"])!);
                await supersededChange;
                _activity.Starts.Should().Be(4);
                await cut.InvokeAsync(() => _activity.SendAsync("ready"));
                await AwaitScheduledRefreshAsync();
                await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='activity-live-status']").TextContent.Should().Contain("Live"));
                _artifacts.LastHistoryStatus.Should().Be("Accepted");
                _activity.Active.Should().BeTrue();
            }
            finally { queuedStart.Drain.TrySetResult(); }
        }
        finally
        {
            pendingStop.Drain.TrySetResult();
            await olderStop;
        }
    }

    private sealed class StubActivityStream : IClientUpdateActivityStream
    {
        private Func<ClientUpdateActivityEvent, Task>? _handler;
        private Action<string>? _status;
        private readonly Queue<PendingStop> _stops = [];
        private readonly Queue<PendingStop> _starts = [];
        public sealed record PendingStop(TaskCompletionSource Started, TaskCompletionSource Drain);
        public PendingStop HoldNextStop()
        {
            var stop = new PendingStop(new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
            _stops.Enqueue(stop);
            return stop;
        }
        public PendingStop HoldNextStart()
        {
            var start = new PendingStop(new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
            _starts.Enqueue(start);
            return start;
        }
        public int Starts { get; private set; }
        public bool Active => _handler is not null;
        public async Task StartAsync(int? tenantId, Func<ClientUpdateActivityEvent, Task> onNotification, Action<string> onStatus, CancellationToken ct = default)
        {
            Starts++;
            if (_starts.Count > 0)
            {
                var start = _starts.Dequeue();
                start.Started.TrySetResult();
                await start.Drain.Task.WaitAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            _handler = onNotification; _status = onStatus; onStatus("Reconnecting");
        }
        public void Disconnect() => _status?.Invoke("Reconnecting");
        public Task SendAsync(string kind) => _handler?.Invoke(new(kind, Guid.NewGuid(), true)) ?? Task.CompletedTask;
        public async Task StopAsync()
        {
            _handler = null; _status = null;
            if (_stops.Count > 0)
            {
                var stop = _stops.Dequeue();
                stop.Started.TrySetResult();
                await stop.Drain.Task;
            }
        }
        public ValueTask DisposeAsync() { _handler = null; _status = null; return ValueTask.CompletedTask; }
    }

    private sealed class StubClientArtifactsService : IClientArtifactsService
    {
        internal List<ClientUpdateReleaseModel> PublishedPrereleases { get; } = [];
        internal (int Page, int PageSize, string? Channel, bool? Enabled)? EligibilityQuery { get; private set; }
        public ClientInstallEndpointSummary EndpointPreview { get; } = new(
            "https://web.example.invalid", "https://preview-api.example.invalid", "https://preview-api.example.invalid",
            "branding-site-url:administrator", "client-artifacts-public-base-url", "shared-api-origin");
        public ClientInstallLinkResultModel GeneratedLink { get; } = new()
        {
            Id = Guid.Parse("fca61d16-7f70-4f96-b226-f15cc36a209c"), TenantId = 7, RuntimeId = "win-x64", ArtifactVersion = "0.4.102",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1), MaxUses = 1, RemainingUses = 1,
            InstallCommand = "fixture first line\nfixture second line", PublicUrl = "https://web.example.invalid/clients/install/fixture.ps1",
            Script = "Write-Output 'fixture'\n",
            Endpoints = new("https://web.example.invalid", "https://created-api.example.invalid", "https://created-gateway.example.invalid",
                "branding-site-url:administrator", "client-artifacts-public-base-url", "client-artifacts-public-gateway-base-url")
        };
        public int GeneratedLinkCalls { get; private set; }
        public Task<ClientInstallEndpointSummary?> PreviewInstallEndpointsAsync(int tenantId, CancellationToken ct = default) =>
            Task.FromResult<ClientInstallEndpointSummary?>(EndpointPreview);
        public Task<ClientInstallLinkResultModel> GenerateInstallLinkAsync(ClientScriptGenerateRequest request, CancellationToken ct = default)
        {
            GeneratedLinkCalls++;
            return Task.FromResult(GeneratedLink);
        }
        private ClientReleaseAutomationModel _automation = new();
        private readonly Queue<Task<ClientReleaseAutomationModel>> _automationReads = new();
        private readonly Queue<Task<ClientReleaseAutomationModel>> _automationSaves = new();
        private readonly Queue<Task<ClientReleaseAutomationModel>> _automationChecks = new();
        public TaskCompletionSource<bool> SaveStarted { get; private set; } = NewSignal();
        public TaskCompletionSource<bool> CheckStarted { get; private set; } = NewSignal();
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
            CheckStarted.TrySetResult(true);
            if (_automationChecks.Count > 0)
            {
                return _automationChecks.Dequeue();
            }

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

        public TaskCompletionSource<ClientReleaseAutomationModel> QueuePendingCheck()
        {
            CheckStarted = NewSignal();
            var pending = NewSource<ClientReleaseAutomationModel>();
            _automationChecks.Enqueue(pending.Task);
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
        public string HistoryStatus { get; set; } = "RolledBack";
        public bool HistoryPurged { get; set; }
        public bool HistoryReconciliationRequired { get; set; }
        public int HistoryTotal { get; set; } = 1;
        public int LastHistoryPage { get; private set; }
        public string? LastHistoryStatus { get; private set; }

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
            if (channel == "prerelease" && enabled == true)
            {
                EligibilityQuery = (page, pageSize, channel, enabled);
                return Task.FromResult(new ClientUpdateReleasePageModel
                { Page = page, PageSize = pageSize, Total = PublishedPrereleases.Count, Items = PublishedPrereleases });
            }
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
            GetHistoryPageAsync(page, pageSize, status);

        private Task<ClientUpdateHistoryPageModel> GetHistoryPageAsync(int page, int pageSize, string? status)
        {
            AttemptPageRequests++;
            LastHistoryStatus = status;
            LastHistoryPage = page;
            if (!string.IsNullOrWhiteSpace(status) && status != HistoryStatus) return Task.FromResult(new ClientUpdateHistoryPageModel { Page = page, PageSize = pageSize });
            return Task.FromResult(new ClientUpdateHistoryPageModel
            {
                Total = HistoryTotal,
                Page = page,
                PageSize = pageSize,
                Items =
                [
                    new ClientUpdateHistoryItemModel
                    {
                        AttemptId = AttemptId,
                        TenantId = 7,
                        TenantName = "Canary",
                        AgentId = AgentId,
                        ClientDisplayName = "Canary agent",
                        ClientHostName = "canary-host",
                        RuntimeId = "win-x64",
                        TargetVersion = "0.4.102",
                        Status = HistoryStatus,
                        LocalPackagePurged = HistoryPurged,
                        FailureCode = "activation_timeout",
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        FromVersion = "0.4.101",
                        ObservedRunningVersion = HistoryReconciliationRequired ? "0.4.101" : null,
                        ObservedAtUtc = HistoryReconciliationRequired ? new DateTimeOffset(2026, 10, 14, 8, 12, 0, TimeSpan.FromHours(2)) : null,
                        ConfirmationId = HistoryReconciliationRequired ? Guid.NewGuid() : null,
                        ConfirmedAtUtc = HistoryReconciliationRequired ? DateTimeOffset.UtcNow.AddMinutes(-5) : null,
                        ReconciliationState = HistoryReconciliationRequired ? "accepted_authority_conflict" : null,
                        ReportedState = HistoryReconciliationRequired ? "RolledBack" : null,
                        ReportedFailureCode = HistoryReconciliationRequired ? "acknowledgement_timeout" : null,
                        ReportedAtUtc = HistoryReconciliationRequired ? DateTimeOffset.UtcNow : null,
                        SuspendedAtUtc = HistoryReconciliationRequired ? DateTimeOffset.UtcNow : null,
                        SuspensionReason = HistoryReconciliationRequired ? "accepted_authority_conflict" : null
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
