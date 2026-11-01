using MudBlazor;
using NetRatel.Web.Services;

namespace NetRatel.Web.Components.Pages.Clients.ClientsMgmt;

public partial class ClientsMgmt
{
    // Existing tab indices stay stable: activity stream ownership is still tab 3.
    private const int LocalImportsTab = 5;
    private int _githubPageSize = 20;
    private CancellationTokenSource? _githubRead;
    private long _githubReadGeneration;
    private bool _importsLoading;
    private long _importsGeneration;
    private string? _importsError;
    private string? _importActionError;
    private MudTable<ClientReleaseImportModel>? _localImportsTable;
    private string _localImportSearch = string.Empty;

    private IEnumerable<ClientReleaseImportModel> LocalImportItems => _githubImports.Values
        .Where(item => string.IsNullOrWhiteSpace(_localImportSearch) ||
            item.Version.Contains(_localImportSearch.Trim(), StringComparison.OrdinalIgnoreCase) ||
            (item.PublishedAtUtc.HasValue ? "Published" : ImportState(item.State))
                .Contains(_localImportSearch.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(item => item.CreatedAtUtc)
        .ThenByDescending(item => item.GitHubReleaseId);

    private Task OnGitHubPageSizeChanged(int pageSize)
    {
        if (!PageSizes.Contains(pageSize)) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (_githubPageSize == pageSize || _automationDisposed) return Task.CompletedTask;
        _githubPageSize = pageSize;
        _githubPageNumber = 0;
        _githubPage = null;
        return LoadGitHubAsync(false);
    }

    private void OnLocalImportSearchChanged(string value)
    {
        _localImportSearch = value ?? string.Empty;
        _localImportsTable?.NavigateTo(0);
    }

    private void ClampLocalImportPage()
    {
        if (_localImportsTable is not { } table) return;
        var lastPage = Math.Max(0, (LocalImportItems.Count() - 1) / Math.Max(1, table.RowsPerPage));
        if (table.CurrentPage > lastPage) table.NavigateTo(lastPage);
    }
}
