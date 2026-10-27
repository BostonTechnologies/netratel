using Microsoft.AspNetCore.Components;
using MudBlazor;
using NetRatel.Web.Services;

namespace NetRatel.Web.Components.Pages.Clients.ClientsMgmt;

public partial class ClientsMgmt
{
    [Inject] public IClientUpdateActivityStream ActivityStream { get; set; } = null!;
    private CancellationTokenSource? _activityCancellation;
    private Task? _activityRefreshTask;
    private readonly SemaphoreSlim _activityReads = new(1, 1);
    private long _activityGeneration;
    private bool _activityRefreshPending;
    private bool _activityBackendConnected;
    private bool _activitySnapshotCurrent;
    private string _activityStatus = "Paused";
    private string? _activityError;
    private DateTimeOffset? _activityUpdatedAt;
    private TableGroupDefinition<ClientUpdateHistoryItemModel>? _activityGroup;
    private IReadOnlyList<ClientUpdateHistoryItemModel> _activityItems = [];
    private int _activityTotal;
    private long _activitySnapshotGeneration = -1;
    private int _activitySnapshotPage;
    private int _activitySnapshotPageSize;
    private bool HasCurrentActivitySnapshot => _activitySnapshotGeneration == _activityGeneration &&
        _activitySnapshotPage == (_attemptTable?.CurrentPage ?? 0) && _activitySnapshotPageSize == (_attemptTable?.RowsPerPage ?? 10);
    private string ActivityCountFormat => _activityError is null ? "{first_item}-{last_item} of {all_items}" :
        HasCurrentActivitySnapshot ? "{first_item}-{last_item} of {all_items} (last loaded)" : "Count unavailable";

    private readonly Dictionary<string, long> _activityExpandedGroups = [];
    private readonly HashSet<Guid> _expandedActivityAttempts = [];
    private readonly Dictionary<Guid, ElementReference> _activityDetailLaunchers = [];
    private Guid? _restoreActivityDetailsFocus;
    private long _activityGroupOrder;
    private string ActivityGroupKey(object key) => _attemptGrouping + "\0" + key;
    private bool IsActivityGroupExpanded(object key) => _activityExpandedGroups.ContainsKey(ActivityGroupKey(key));
    private void ToggleActivityGroup(object key)
    {
        var group = ActivityGroupKey(key);
        if (!_activityExpandedGroups.Remove(group))
        {
            _activityExpandedGroups[group] = ++_activityGroupOrder;
            if (_activityExpandedGroups.Count > 256)
                _activityExpandedGroups.Remove(_activityExpandedGroups.MinBy(pair => pair.Value).Key);
        }
    }

    private string ActivityRowStyle(ClientUpdateHistoryItemModel item, int _) =>
        AttemptGroup?.Selector is { } selector && !IsActivityGroupExpanded(selector(item)) ? "display:none" : string.Empty;

    private void ToggleActivityDetails(Guid attemptId) => SetActivityDetailsExpanded(attemptId, !_expandedActivityAttempts.Contains(attemptId));

    private void SetActivityDetailsExpanded(Guid attemptId, bool expanded)
    {
        if (expanded) _expandedActivityAttempts.Add(attemptId);
        else if (_expandedActivityAttempts.Remove(attemptId)) _restoreActivityDetailsFocus = attemptId;
    }

    private static string ActivityTime(DateTimeOffset? at) => at is { } value
        ? value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'") : "not recorded";

    private static string InstallationSummary(ClientUpdateHistoryItemModel item) => (item.ReportedState ?? item.Status) switch
    {
        "RolledBack" => "Installation: previous version restored",
        "RollbackUnverified" => "Installation: rollback unverified",
        "FailedPreActivation" => "Installation: activation not completed",
        "Accepted" => item.ConfirmationId is not null ? "Installation: target confirmed at gateway" : "Installation: confirmation detail unavailable",
        _ => "Installation: in progress"
    };

    private static string ServerReportSummary(ClientUpdateHistoryItemModel item) => item.ReconciliationState is not null
        ? "Server report: conflicts with accepted confirmation"
        : item.Status is "RolledBack" or "RollbackUnverified" or "FailedPreActivation" ? "Server report: received"
        : item.Status == "Accepted" ? "Server confirmation: recorded" : "Server report: awaiting terminal result";

    private static string OperatorAction(ClientUpdateHistoryItemModel item) => item.ReconciliationState is not null
        ? "Action: reconcile the running version and installation evidence before resuming automatic updates."
        : item.SuspendedAtUtc is not null
            ? "Action: review the installation outcome; resume affects future automatic updates only."
            : "Action: no operator intervention recorded as required.";

    private async Task StartActivityAsync()
    {
        if (_activeTab != 3 || _automationDisposed || _activityCancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        _activityCancellation = cancellation;
        var generation = ++_activityGeneration;
        _activitySnapshotCurrent = false;
        try
        {
            await ActivityStream.StartAsync(_attemptTenantId,
            notification => InvokeAsync(() =>
            {
                if (generation != _activityGeneration || _activeTab != 3 || _automationDisposed) return;
                var previousStatus = _activityStatus;
                _activityBackendConnected = notification.Connected;
                if (!notification.Connected) _activityStatus = "Reconnecting";
                if (notification.Kind is "ready" or "invalidate" || notification.Connected && !_activitySnapshotCurrent)
                {
                    _activitySnapshotCurrent = false;
                    QueueActivityRefresh(generation);
                }
                // Invalidations change no displayed rows until the authoritative
                // refresh completes. Keep event bursts from rendering the entire page.
                if (previousStatus != _activityStatus) StateHasChanged();
            }), status => _ = InvokeAsync(() =>
            {
                if (generation != _activityGeneration || _automationDisposed) return;
                var statusChanged = _activityStatus != status;
                _activityStatus = status;
                if (status != "Live")
                {
                    _activitySnapshotCurrent = false;
                    _activityBackendConnected = false;
                }
                if (statusChanged) StateHasChanged();
            }), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || generation != _activityGeneration)
        {
            // A later scope can cancel this start while it waits for the previous
            // transport to drain. Superseded UI input is a normal cancellation.
        }
    }

    private async Task StopActivityAsync()
    {
        var stoppedGeneration = ++_activityGeneration;
        var cancellation = _activityCancellation;
        _activityCancellation = null;
        cancellation?.Cancel();
        _activityRefreshPending = false;
        // Detach the cancelled loop before awaiting transport shutdown. A reentrant
        // scope change can start and own a new coalescer during that await.
        _activityRefreshTask = null;
        _activityBackendConnected = false;
        _activitySnapshotCurrent = false;
        _activityStatus = "Paused";
        _attemptsLoading = false;
        await ActivityStream.StopAsync();
        // Do not await an InvokeAsync refresh on the renderer while navigating/disposal.
        // Cancellation + generation fencing prevents its delayed result from applying.
        if (stoppedGeneration == _activityGeneration) _activityRefreshTask = null;
        cancellation?.Dispose();
    }

    private async Task ActivityScopeChangedAsync()
    {
        await StopActivityAsync();
        _activityUpdatedAt = null;
        _activityError = null;
        await StartActivityAsync();
        if (_attemptTable is not null)
        {
            _attemptTable.NavigateTo(0);
            await _attemptTable.ReloadServerData();
        }
    }

    private void QueueActivityRefresh(long generation)
    {
        if (_activityCancellation is null || generation != _activityGeneration) return;
        _activityRefreshPending = true;
        _activityRefreshTask ??= CoalesceActivityRefreshAsync(generation, _activityCancellation.Token);
    }

    private async Task CoalesceActivityRefreshAsync(long generation, CancellationToken ct)
    {
        try
        {
            while (_activityRefreshPending && !ct.IsCancellationRequested && generation == _activityGeneration)
            {
                await Task.Delay(200, ct);
                ct.ThrowIfCancellationRequested();
                if (generation != _activityGeneration || _activeTab != 3 || _automationDisposed) return;
                _activityRefreshPending = false;
                await InvokeAsync(async () =>
                {
                    if (generation != _activityGeneration || _activeTab != 3 || _attemptTable is null) return;
                    await _attemptTable.ReloadServerData();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (generation == _activityGeneration)
            {
                _activityRefreshTask = null;
                // An event arriving at completion still receives a refresh.
                if (_activityRefreshPending) QueueActivityRefresh(generation);
            }
        }
    }

    private Task RefreshActivityAsync() => _attemptTable?.ReloadServerData() ?? Task.CompletedTask;

    private async Task<TableData<ClientUpdateHistoryItemModel>> LoadAttemptsAsync(TableState state, CancellationToken ct)
    {
        var generation = _activityGeneration;
        using var scopeRead = CancellationTokenSource.CreateLinkedTokenSource(ct, _activityCancellation?.Token ?? CancellationToken.None);
        var readToken = scopeRead.Token;
        var search = EmptyToNull(_attemptSearch);
        var status = EmptyToNull(_attemptStatus);
        var runtime = EmptyToNull(_attemptRid);
        var tenant = _attemptTenantId;
        var version = EmptyToNull(_attemptVersion);
        await _activityReads.WaitAsync(readToken);
        try
        {
            readToken.ThrowIfCancellationRequested();
            if (_automationDisposed || generation != _activityGeneration) return ActivitySnapshot();
            _attemptsLoading = true;
            var page = await Artifacts.GetUpdateHistoryAsync(state.Page, state.PageSize, search, status, null, runtime, tenant, version, readToken);
            readToken.ThrowIfCancellationRequested();
            if (_automationDisposed || generation != _activityGeneration) return ActivitySnapshot();
            // Purge/filter membership changes can remove the final page. Clamp only then.
            var lastPage = Math.Max(0, (page.Total - 1) / state.PageSize);
            if (state.Page > lastPage)
            {
                _attemptTable?.NavigateTo(lastPage);
                page = await Artifacts.GetUpdateHistoryAsync(lastPage, state.PageSize, search, status, null, runtime, tenant, version, readToken);
                readToken.ThrowIfCancellationRequested();
                if (_automationDisposed || generation != _activityGeneration) return ActivitySnapshot();
            }
            _activityItems = page.Items;
            var currentAttempts = page.Items.Select(item => item.AttemptId).ToHashSet();
            _expandedActivityAttempts.IntersectWith(currentAttempts);
            foreach (var attemptId in _activityDetailLaunchers.Keys.Where(id => !currentAttempts.Contains(id)).ToArray())
                _activityDetailLaunchers.Remove(attemptId);
            _activityTotal = page.Total;
            _activitySnapshotGeneration = generation;
            _activitySnapshotPage = page.Page;
            _activitySnapshotPageSize = page.PageSize;
            _activityUpdatedAt = DateTimeOffset.Now;
            _activityError = null;
            _activitySnapshotCurrent = true;
            if (_activeTab == 3 && _activityBackendConnected && _activitySnapshotCurrent) _activityStatus = "Live";
            return ActivitySnapshot();
        }
        catch (Exception exception) when (exception is HttpRequestException or NetRatel.Web.Services.Authentication.ReauthRequiredException or System.Text.Json.JsonException ||
            exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            if (_automationDisposed || generation != _activityGeneration) return ActivitySnapshot();
            _activityError = HasCurrentActivitySnapshot
                ? "Activity refresh failed. Showing the last loaded page; use Refresh to retry."
                : "Activity refresh failed. No snapshot is available for these filters and page; use Refresh to retry.";
            _activityStatus = exception is NetRatel.Web.Services.Authentication.ReauthRequiredException or
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                ? "Paused" : "Reconnecting";
            if (_activityStatus == "Paused") _activityBackendConnected = false;
            _activitySnapshotCurrent = false;
            return ActivitySnapshot();
        }
        finally
        {
            if (generation == _activityGeneration) _attemptsLoading = false;
            _activityReads.Release();
        }
    }

    private TableData<ClientUpdateHistoryItemModel> ActivitySnapshot() => HasCurrentActivitySnapshot
        ? new() { Items = _activityItems, TotalItems = _activityTotal }
        : new() { Items = [], TotalItems = 0 };
}
