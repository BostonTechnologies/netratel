using MudBlazor;
using NetRatel.Web.Services;

namespace NetRatel.Web.Components.Pages.Clients.ClientsMgmt;

public partial class ClientsMgmt
{
    private bool _localDeleteBusy;
    private string? _localDeleteMessage;
    private List<ClientReleaseDeletionStatusModel> _localDeletions = [];
    private readonly Dictionary<string, ClientReleaseSourceStatusModel> _sourceStates = [];
    private async Task CheckSourceAsync(string version)
    {
        try { _sourceStates[version] = await Artifacts.CheckLocalReleaseSourceAsync(version, _importPolling.Token); }
        catch (OperationCanceledException) when (_importPolling.IsCancellationRequested) { }
        catch (HttpRequestException)
        {
            _sourceStates[version] = new() { State = "Unknown", Message = "Source check failed. Verified local packages remain available; try again." };
        }
    }
}
