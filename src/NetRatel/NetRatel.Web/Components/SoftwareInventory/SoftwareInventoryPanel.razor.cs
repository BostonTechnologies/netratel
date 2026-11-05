using System.Text.Json;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NetRatel.Shared.Contracts.SoftwareInventory;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Services.SoftwareInventory;

namespace NetRatel.Web.Components.SoftwareInventory;

public partial class SoftwareInventoryPanel
{
    [Parameter] public int TenantId { get; set; }
    [Parameter] public Guid? AgentId { get; set; }
    [Parameter] public bool AutoRefreshOnOpen { get; set; }

    private SoftwareInventoryStatus? _status;
    private SoftwareInventorySummary? _summary;
    private SoftwarePage<SoftwareInstallationDto>? _installations;
    private SoftwarePage<SoftwarePrevalenceDto>? _aggregate;
    private SoftwarePage<SoftwareChangeDto>? _changes;
    private SoftwarePage<SoftwareInventoryClientDto>? _clients;
    private SoftwareInventoryClientDto? _selectedClient;
    private readonly HashSet<(Guid AgentId, string Identity)> _expandedPackages = [];
    private readonly string _detailsIdPrefix = $"inventory-package-{Guid.NewGuid():N}";
    private CancellationTokenSource _scope = new();
    private CancellationTokenSource? _load;
    private (int, Guid?) _active = (-1, null);
    private long _generation;
    private bool _loading, _refreshing, _disposed;
    private string? _error, _statusError, _summaryError;
    private string _search = "", _name = "", _version = "", _publisher = "", _os = "", _source = "", _filterStatus = "", _sort = "name", _view = "installations";
    private bool _descending;
    private int _page, _pageSize = 50;
    private DateTimeOffset _lastProgress = DateTimeOffset.UtcNow;
    private Task? _poll;
    private static readonly string[] Sources = ["registry", "appx", "dpkg", "rpm", "pacman", "snap", "flatpak"];
    private static readonly string[] Statuses = ["fresh", "stale", "Pending", "Collecting", "Complete", "Partial", "Error"];
    private bool ActiveScan => _status?.LatestScan?.Status is "Pending" or "Collecting";
    private bool Stalled => _status?.LatestScan?.Status == "Collecting" && DateTimeOffset.UtcNow - _lastProgress >= TimeSpan.FromMinutes(1);
    private bool HasFilters => new[] { _search, _name, _version, _publisher, _os, _source, _filterStatus }.Any(text => !string.IsNullOrWhiteSpace(text)) || _selectedClient is not null;
    private bool SoftwareView => _view is "installations" or "aggregate";
    private int EffectivePageSize => _view == "changes" ? 50 : _pageSize;
    private int Total => _view switch { "aggregate" => _aggregate?.Total ?? 0, "changes" => _changes?.Total ?? 0, "devices" => _clients?.Total ?? 0, _ => _installations?.Total ?? 0 };
    private int PageCount => Math.Max(1, (Total + EffectivePageSize - 1) / EffectivePageSize);
    private static string Time(DateTimeOffset? time) => time?.UtcDateTime.ToString("dd MMM yyyy, HH:mm:ss 'UTC'") ?? "Not yet available";
    private static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static string Reported(string? text) => string.IsNullOrWhiteSpace(text) ? "Not reported" : text;
    private static string ClientLabel(Guid id, string? label) => !string.IsNullOrWhiteSpace(label) && !Guid.TryParse(label, out _) ? label : "Client-" + id.ToString("N")[..8];
    private static string StatusLabel(string status) => status switch
    {
        "NeverScanned" => "Never scanned", "Pending" => "Queued", "Collecting" => "Receiving inventory",
        "Complete" => "Complete", "Partial" => "Incomplete scan", "Error" => "Needs attention", _ => status
    };
    private static Color StatusColor(string status) => status switch
    {
        "Complete" => Color.Success, "Pending" or "Collecting" => Color.Info,
        "Error" or "Partial" => Color.Warning, _ => Color.Default
    };
    private static string ErrorLabel(string error) => error switch
    {
        "scan_expired" => "The scan exceeded its five-minute deadline. Retry after checking the agent connection.",
        "collection_timeout" => "A package collector did not finish within two minutes.",
        _ => error.Replace('_', ' ')
    };
    private string ExportUrl() => SoftwareInventoryApiService.QueryUrl(TenantId, "export.csv", Query());
    private bool IsPackageExpanded(SoftwareInstallationDto item) => _expandedPackages.Contains((item.AgentId, item.Identity));
    private string PackageDetailsId(SoftwareInstallationDto item) => $"{_detailsIdPrefix}-{item.AgentId:N}-{item.Identity}";
    private void TogglePackageDetails(SoftwareInstallationDto? item)
    {
        if (item is null) return;
        var key = (item.AgentId, item.Identity);
        if (!_expandedPackages.Add(key)) _expandedPackages.Remove(key);
    }
    private SoftwareInventoryQuery Query() => new(_page, EffectivePageSize, Optional(_search), Optional(_name), Optional(_version),
        Optional(_publisher), Optional(_os), Optional(_source), AgentId ?? _selectedClient?.AgentId, Optional(_filterStatus), _sort, _descending);

    protected override async Task OnParametersSetAsync()
    {
        if (_active == (TenantId, AgentId) || TenantId <= 0) return;
        _scope.Cancel(); _scope.Dispose(); _scope = new();
        _load?.Cancel();
        _active = (TenantId, AgentId);
        _expandedPackages.Clear();
        _status = null; _summary = null; _installations = null; _aggregate = null; _changes = null; _clients = null;
        _page = 0; _error = null; _statusError = null; _summaryError = null; _selectedClient = null;
        _refreshing = false; _loading = false;
        _search = _name = _version = _publisher = _os = _source = _filterStatus = "";
        _view = AgentId.HasValue ? "installations" : "aggregate";
        var token = _scope.Token;
        // Independent requests: neither a slow status lookup nor a refresh POST may hold cached rows hostage.
        var rows = LoadAsync(token);
        var details = AgentId is { } agent ? InitializeClientAsync(TenantId, agent, token) : LoadSummaryAsync(TenantId, token);
        await Task.WhenAll(rows, details);
    }

    private async Task InitializeClientAsync(int tenant, Guid agent, CancellationToken token)
    {
        try
        {
            var status = await Api.StatusAsync(tenant, agent, token);
            token.ThrowIfCancellationRequested();
            UpdateStatus(status);
            await InvokeAsync(StateHasChanged);
            if (AutoRefreshOnOpen && status.CanRefresh && !ActiveScan &&
                (status.LastSuccessfulScanUtc is null || status.LastSuccessfulScanUtc <= DateTimeOffset.UtcNow.AddMinutes(-30)))
                await RefreshAsync(false, tenant, agent, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        { if (!token.IsCancellationRequested) _statusError = "Cannot check scan status. Saved inventory remains available; status will reconnect automatically."; }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                _poll = PollAsync(tenant, agent, token);
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private void UpdateStatus(SoftwareInventoryStatus status)
    {
        if (status.LatestScan?.CollectionId != _status?.LatestScan?.CollectionId ||
            status.LatestScan?.ReceivedChunks != _status?.LatestScan?.ReceivedChunks || status.LatestScan?.Status != _status?.LatestScan?.Status)
            _lastProgress = DateTimeOffset.UtcNow;
        _status = status;
        _statusError = null;
    }

    private async Task LoadSummaryAsync(int tenant, CancellationToken token)
    {
        try
        {
            var summary = await Api.SummaryAsync(tenant, token);
            token.ThrowIfCancellationRequested();
            _summary = summary; _summaryError = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        { if (!token.IsCancellationRequested) _summaryError = "Coverage totals are unavailable. The inventory table can still be used."; }
        finally { if (!token.IsCancellationRequested) await InvokeAsync(StateHasChanged); }
    }

    private async Task LoadAsync(CancellationToken token)
    {
        _load?.Cancel(); _load?.Dispose();
        _load = CancellationTokenSource.CreateLinkedTokenSource(token);
        var requestToken = _load.Token;
        var generation = ++_generation;
        var tenant = TenantId;
        var view = _view;
        _loading = true; _error = null;
        try
        {
            var query = Query();
            query.Validate();
            if (view == "aggregate")
            {
                var result = await Api.AggregateAsync(tenant, query, requestToken);
                requestToken.ThrowIfCancellationRequested();
                if (generation == _generation) _aggregate = result;
            }
            else if (view == "changes")
            {
                var result = await Api.ChangesAsync(tenant, query.AgentId, query.Page, requestToken);
                requestToken.ThrowIfCancellationRequested();
                if (generation == _generation) _changes = result;
            }
            else if (view == "devices")
            {
                var result = await Api.ClientsAsync(tenant, new(query.Page, query.PageSize, query.Search), requestToken);
                requestToken.ThrowIfCancellationRequested();
                if (generation == _generation) _clients = result;
            }
            else
            {
                var result = await Api.InstallationsAsync(tenant, query, requestToken);
                requestToken.ThrowIfCancellationRequested();
                if (generation == _generation)
                {
                    _installations = result;
                    _expandedPackages.IntersectWith(result.Items.Select(item => (item.AgentId, item.Identity)));
                }
            }
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ArgumentException or JsonException)
        {
            if (!requestToken.IsCancellationRequested && generation == _generation)
                _error = error is ArgumentException ? error.Message : "Unable to load this inventory view. Retry without closing the dialog.";
        }
        finally
        {
            if (!token.IsCancellationRequested && generation == _generation)
            { _loading = false; await InvokeAsync(StateHasChanged); }
        }
    }

    private async Task ApplyAsync() { _page = 0; await LoadAsync(_scope.Token); }
    private async Task SearchChangedAsync(string value) { _search = value ?? ""; await ApplyAsync(); }
    private async Task SelectClientAsync(SoftwareInventoryClientDto? client) { _selectedClient = client; await ApplyAsync(); }
    private async Task ChangePageSizeAsync(int size) { _pageSize = size; await ApplyAsync(); }
    private async Task SortAsync(string sort) { _descending = _sort == sort && !_descending; _sort = sort; await ApplyAsync(); }
    private async Task PreviousAsync() { if (_page > 0) { _page--; await LoadAsync(_scope.Token); } }
    private async Task NextAsync() { if (_page + 1 < PageCount) { _page++; await LoadAsync(_scope.Token); } }
    private async Task ChangeViewAsync(string view)
    {
        _view = view; _page = 0;
        if (view is "changes" or "devices") { _search = _name = _version = _publisher = _os = _source = _filterStatus = ""; }
        if (view == "devices") _selectedClient = null;
        await LoadAsync(_scope.Token);
    }
    private async Task ResetAsync()
    {
        _search = _name = _version = _publisher = _os = _source = _filterStatus = "";
        _selectedClient = null;
        await ApplyAsync();
    }
    private async Task ReloadAsync()
    {
        var token = _scope.Token;
        await Task.WhenAll(LoadAsync(token), AgentId.HasValue ? Task.CompletedTask : LoadSummaryAsync(TenantId, token));
    }
    private async Task<IEnumerable<SoftwareInventoryClientDto>> SearchClientsAsync(string? search, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _scope.Token);
        var clients = await Api.ClientsAsync(TenantId, new(PageSize: 25, Search: Optional(search ?? "")), linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        return clients.Items;
    }
    private async Task ShowInstallationsAsync(SoftwarePrevalenceDto product)
    {
        _view = "installations"; _search = ""; _name = product.Name; _version = product.Version;
        _publisher = product.Publisher; _source = product.Source;
        await ApplyAsync();
    }
    private async Task OpenClientAsync(Guid agent, string label)
    {
        var tenant = TenantId;
        var token = _scope.Token;
        var parameters = new DialogParameters<SoftwareInventoryDialog>
        {
            { dialog => dialog.TenantId, tenant }, { dialog => dialog.AgentId, agent }, { dialog => dialog.HostLabel, ClientLabel(agent, label) }
        };
        var dialog = await Dialogs.ShowAsync<SoftwareInventoryDialog>("Software Inventory", parameters,
            new DialogOptions { FullScreen = true, CloseOnEscapeKey = true });
        await dialog.Result;
        if (!token.IsCancellationRequested) await ReloadAsync();
    }
    private Task ForceRefreshAsync() => AgentId is { } agent && !_refreshing && !ActiveScan
        ? RefreshAsync(true, TenantId, agent, _scope.Token) : Task.CompletedTask;

    private async Task RefreshAsync(bool force, int tenant, Guid agent, CancellationToken token)
    {
        _refreshing = true;
        try
        {
            var status = await Api.RefreshAsync(tenant, agent, force, token);
            token.ThrowIfCancellationRequested();
            UpdateStatus(status);
            if (!ActiveScan) await LoadAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        { if (!token.IsCancellationRequested) _statusError = "The scan request could not be confirmed. Status checks will reconnect; saved inventory is unchanged."; }
        finally { if (!token.IsCancellationRequested) { _refreshing = false; await InvokeAsync(StateHasChanged); } }
    }

    private async Task PollAsync(int tenant, Guid agent, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(ActiveScan || _status is null || _statusError is not null ? 2 : 15), token);
                try
                {
                    var status = await Api.StatusAsync(tenant, agent, token);
                    token.ThrowIfCancellationRequested();
                    await InvokeAsync(async () =>
                    {
                        if (token.IsCancellationRequested) return;
                        var completed = status.LastSuccessfulScanUtc != _status?.LastSuccessfulScanUtc;
                        var terminal = ActiveScan && status.LatestScan?.Status is not ("Pending" or "Collecting");
                        UpdateStatus(status);
                        if (completed || terminal) await LoadAsync(token);
                        StateHasChanged();
                    });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
                {
                    if (!token.IsCancellationRequested)
                    {
                        await InvokeAsync(() => { _statusError = "Scan status is temporarily unavailable. Reconnecting; saved inventory is still shown."; StateHasChanged(); });
                        await Task.Delay(TimeSpan.FromSeconds(8), token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scope.Cancel(); _load?.Cancel(); _load?.Dispose(); _scope.Dispose();
    }
}
