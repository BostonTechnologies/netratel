using MudBlazor;
using NetRatel.Web.Services;

namespace NetRatel.Web.Components.Pages.Clients.ClientsMgmt;

public partial class ClientsMgmt
{
    private bool _localDeleteBusy;
    private string? _localDeleteMessage;
    private List<ClientReleaseDeletionStatusModel> _localDeletions = [];
    private readonly Dictionary<string, ClientReleaseSourceStatusModel> _sourceStates = [];
    private TableData<ClientUpdateReleaseModel> _lastReleases = new() { Items = [], TotalItems = 0 };
    private string? _releaseLoadError;
    private bool _hasReleaseSnapshot;
    private string? _releaseSnapshotScope;

    private async Task DeleteLocalReleaseAsync(string version)
    {
        if (_localDeleteBusy || _automationDisposed) return;
        _localDeleteBusy = true;
        _localDeleteMessage = null;
        try
        {
            var preview = await Artifacts.PreviewLocalReleaseDeletionAsync(version);
            if (_automationDisposed) return;
            var description = $"Version {version}; runtimes: {string.Join(", ", preview.RuntimeIds)}. " +
                $"{preview.ReleaseIds.Length} release records, {preview.ImportIds.Length} imports, {preview.UploadStagingIds.Length} staged uploads; {FormatBytes(preview.ReclaimableBytes)} reclaimable. " +
                $"{preview.HistoricalAttempts} historical attempts and a deletion receipt are retained. Upstream releases and installed clients are unaffected.";
            if (preview.Blockers.Length != 0)
            {
                await Dialogs.ShowMessageBoxAsync("Local deletion blocked", description + " " + string.Join(" ", preview.Blockers), yesText: "Close");
                return;
            }
            var confirmed = await Dialogs.ShowMessageBoxAsync($"Delete local release {version}?", description,
                yesText: "Delete local release and files", cancelText: "Cancel");
            if (_automationDisposed || confirmed != true) return;
            var receipt = await Artifacts.DeleteLocalReleaseAsync(version);
            if (_automationDisposed) return;
            _localDeleteMessage = receipt.State == "Deleted"
                ? $"Local release {version} and its owned files were deleted. Automatic re-import is suppressed until you deliberately import this version."
                : $"Local release {version} cleanup is {receipt.State.ToLowerInvariant()}. {receipt.Error} Required cleanup has not finished; the server will retry.";
            await RefreshImportsAsync();
            if (_automationDisposed) return;
            if (_releaseTable is not null) await _releaseTable.ReloadServerData();
            if (_artifactTable is not null) await _artifactTable.ReloadServerData();
        }
        catch (HttpRequestException exception) { _localDeleteMessage = exception.Message; }
        finally { _localDeleteBusy = false; }
    }

    private async Task CheckSourceAsync(string version)
    {
        try { _sourceStates[version] = await Artifacts.CheckLocalReleaseSourceAsync(version); }
        catch (HttpRequestException)
        {
            _sourceStates[version] = new() { State = "Unknown", Message = "Source check failed. Verified local packages remain available; try again." };
        }
    }
}
