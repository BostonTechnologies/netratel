using Microsoft.AspNetCore.Components;
using MudBlazor;
using NetRatel.Web.Services;

namespace NetRatel.Web.Components.Pages.Clients.ClientsMgmt;

public partial class ClientsMgmt
{
    private const int ActivityTab = 1;
    private const int SuspendedTab = 2;
    private ClientReleaseWorkspacePageModel? _workspace;
    private string? _workspaceSearch;
    private string _workspaceRuntime = "", _workspaceLocalState = "";
    private readonly HashSet<string> _expandedVersions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClientReleaseDeletionPlanModel> _cleanupPlans = [];
    private readonly Dictionary<string, string> _detailErrors = [];
    private readonly Dictionary<string, MudIconButton> _releaseLaunchers = [];
    private string? _restoreReleaseFocus;
    private bool _cleanupDialogOpen, _disableAllRuntimes;
    private List<ClientReleaseDeletionPlanModel> _cleanupPreview = [];
    private List<ClientReleaseBatchResultModel> _batchResults = [];
    private (string Version, ClientReleaseDeletionBlockerModel Blocker)? _retirementTarget;
    private string _retirementReason = "";
    private bool _acknowledgeUnknownOutcome;

    private static IEnumerable<string> WorkspaceRuntimes(ClientReleaseWorkspaceRowModel row) => row.Packages.Select(x => x.Rid)
        .Concat(row.Releases.Select(x => x.RuntimeId)).Concat(row.Imports.SelectMany(x => x.Assets).Select(x => x.RuntimeId))
        .Concat(row.Remote?.ClientAssets.Select(x => x.RuntimeId) ?? []).Distinct().Order();
    private static bool CanUsePackage(ClientReleaseWorkspaceRowModel row, ClientArtifactSummaryModel package) =>
        row.Cleanup?.Suppressed != true && row.Imports.Where(x => x.Assets.Any(a => a.RuntimeId == package.Rid))
            .All(x => x.State == 5 && x.PublishedAtUtc.HasValue);

    private async Task EnableRuntimeAsync(ClientUpdateReleaseModel release)
    {
        if (_importActionBusy) return;
        var approved = await Dialogs.ShowMessageBoxAsync("Enable runtime updates", $"Enable {release.RuntimeId} for version {release.Version} under the existing tenant channel policies? The server rechecks the approved local archive.", yesText: "Enable runtime", cancelText: "Cancel");
        if (approved != true) return;
        _importActionBusy = true;
        try { await Artifacts.EnableReleaseAsync(release.ReleaseId, _importPolling.Token); await LoadGitHubAsync(false); }
        catch (HttpRequestException ex) { _importsError = ex.Message; }
        finally { _importActionBusy = false; }
    }

    private async Task SearchWorkspaceAsync(string value) { _workspaceSearch = value; _githubPageNumber = 0; _workspace = null; await LoadGitHubAsync(false); }
    private async Task FilterWorkspaceRuntimeAsync(string value) { _workspaceRuntime = value; _githubPageNumber = 0; _workspace = null; await LoadGitHubAsync(false); }
    private async Task FilterWorkspaceLocalStateAsync(string value) { _workspaceLocalState = value; _githubPageNumber = 0; _workspace = null; await LoadGitHubAsync(false); }
    private async Task ToggleReleaseDetailsAsync(string version) => await SetReleaseExpandedAsync(version, !_expandedVersions.Contains(version));
    private async Task SetReleaseExpandedAsync(string version, bool expanded)
    {
        if (!expanded) { _expandedVersions.Remove(version); _restoreReleaseFocus = version; return; }
        _expandedVersions.Add(version);
        await LoadCleanupDetailsAsync(version);
    }
    private async Task LoadCleanupDetailsAsync(string version)
    {
        try
        {
            var plan = await Artifacts.PreviewLocalReleaseDeletionAsync(version, _importPolling.Token);
            if (_automationDisposed) return;
            _cleanupPlans[version] = plan;
            _detailErrors.Remove(version);
        }
        catch (OperationCanceledException) when (_importPolling.IsCancellationRequested) { }
        catch (HttpRequestException) { _detailErrors[version] = "Local asset details could not load. Refresh dependencies to retry."; }
    }
    private void SelectVersion(string version, bool selected)
    {
        if (!selected) _selectedVersions.Remove(version);
        else if (_selectedVersions.Count < 100) _selectedVersions.Add(version);
        else _localDeleteMessage = "A reviewed cleanup batch is limited to 100 versions. Purge or clear this selection first.";
    }
    private void SelectWorkspacePage()
    {
        foreach (var row in _workspace?.Items ?? [])
            if (row.HasLocal || row.Cleanup?.State is "Pending" or "Failed") SelectVersion(row.Version, true);
    }
    private void SelectAllFilteredVersions()
    {
        if (_workspace is null || _workspace.SelectableVersions.Length > 100) return;
        foreach (var version in _workspace.SelectableVersions) SelectVersion(version, true);
    }
    private Task PreviewSelectedCleanupAsync() => PreviewCleanupAsync(_selectedVersions.Order(StringComparer.Ordinal).ToArray());
    private Task DeleteLocalReleaseAsync(string version) => PreviewCleanupAsync([version]);
    private async Task PreviewCleanupAsync(string[] versions)
    {
        if (_localDeleteBusy || _automationDisposed || versions.Length == 0) return;
        _localDeleteBusy = true;
        _localDeleteMessage = null;
        try
        {
            var plans = await Artifacts.PreviewReleaseBatchAsync(versions, _importPolling.Token);
            if (_automationDisposed) return;
            _cleanupPreview = plans; _batchResults = []; _disableAllRuntimes = false;
            foreach (var plan in plans) { _cleanupPlans[plan.Version] = plan; _selectedVersions.Add(plan.Version); }
            _cleanupDialogOpen = true;
        }
        catch (OperationCanceledException) when (_importPolling.IsCancellationRequested) { }
        catch (HttpRequestException ex) { _localDeleteMessage = ex.Message; }
        finally { _localDeleteBusy = false; }
    }
    private bool CleanupEligible(ClientReleaseDeletionPlanModel plan) => plan.Blockers.All(message =>
        _disableAllRuntimes && message == "Disable every runtime release for this version before deletion.");
    private async Task ConfirmCleanupAsync()
    {
        if (_localDeleteBusy || _automationDisposed) return;
        var eligible = _cleanupPreview.Where(CleanupEligible).ToArray();
        if (eligible.Length == 0) return;
        _localDeleteBusy = true;
        try
        {
            _batchResults = await Artifacts.DeleteReleaseBatchAsync(eligible, _disableAllRuntimes, _importPolling.Token);
            if (_automationDisposed) return;
            foreach (var result in _batchResults.Where(x => x.State == "Deleted")) _selectedVersions.Remove(result.Version);
            var complete = _batchResults.Count(x => x.State == "Deleted");
            _localDeleteMessage = $"{complete} version(s) deleted; {_batchResults.Count - complete} pending, failed or blocked. " +
                "Unresolved selections are retained. Automatic re-import stays suppressed for purged versions.";
            await LoadGitHubAsync(false);
            foreach (var plan in eligible) await LoadCleanupDetailsAsync(plan.Version);
        }
        catch (OperationCanceledException) when (_importPolling.IsCancellationRequested) { }
        catch (HttpRequestException ex)
        { _localDeleteMessage = ex.Message + " The result may be incomplete; refresh cleanup receipts before retrying the same versions."; }
        finally { _localDeleteBusy = false; }
    }
    private void CloseCleanupDialog()
    {
        if (_localDeleteBusy || _automationDisposed) return;
        _cleanupDialogOpen = false; _retirementTarget = null;
        _restoreReleaseFocus = _cleanupPreview.FirstOrDefault()?.Version;
    }
    private async Task ResolveCleanupBlockerAsync(string version, ClientReleaseDeletionBlockerModel blocker)
    {
        if (_localDeleteBusy || _automationDisposed || !blocker.CanResolve || blocker.ReferenceId is not { } id) return;
        if (blocker.Code == "update_attempt")
        {
            _retirementTarget = (version, blocker); _retirementReason = ""; _acknowledgeUnknownOutcome = false;
            if (!_cleanupDialogOpen) { _cleanupPreview = _cleanupPlans.TryGetValue(version, out var plan) ? [plan] : []; _cleanupDialogOpen = true; }
            return;
        }
        var approved = await Dialogs.ShowMessageBoxAsync("Resolve local cleanup dependency", blocker.Action + "?", yesText: "Confirm", cancelText: "Cancel");
        if (approved != true) return;
        _localDeleteBusy = true;
        try
        {
            if (blocker.Code == "import_active") await Artifacts.CancelReleaseImportAsync(id, _importPolling.Token);
            else if (blocker.Code == "install_grant") await Artifacts.RevokeInstallLinkAsync(id, _importPolling.Token);
            await RefreshCleanupPreviewAsync();
            await LoadCleanupDetailsAsync(version);
        }
        catch (HttpRequestException ex) { _localDeleteMessage = ex.Message; }
        finally { _localDeleteBusy = false; }
    }
    private async Task ConfirmRetirementAsync()
    {
        if (_localDeleteBusy || _automationDisposed || _retirementTarget is not { } target || !_acknowledgeUnknownOutcome || _retirementReason.Trim().Length is < 10 or > 512) return;
        _localDeleteBusy = true;
        try
        {
            await Artifacts.RetireReleaseAttemptAsync(target.Version, target.Blocker.ReferenceId!.Value, _retirementReason.Trim(), _importPolling.Token);
            _retirementTarget = null;
            await RefreshCleanupPreviewAsync();
            await LoadCleanupDetailsAsync(target.Version);
        }
        catch (HttpRequestException ex) { _localDeleteMessage = ex.Message; }
        finally { _localDeleteBusy = false; }
    }
    private async Task RefreshCleanupPreviewAsync()
    {
        if (_cleanupPreview.Count == 0) return;
        _cleanupPreview = await Artifacts.PreviewReleaseBatchAsync(_cleanupPreview.Select(x => x.Version).ToArray(), _importPolling.Token);
        foreach (var plan in _cleanupPreview) _cleanupPlans[plan.Version] = plan;
    }
    private async Task InspectCleanupAttemptAsync(string version, ClientReleaseDeletionBlockerModel blocker)
    {
        CloseCleanupDialog(); _attemptVersion = version; _attemptTenantId = blocker.TenantId;
        await OnTabChanged(ActivityTab);
    }
}
