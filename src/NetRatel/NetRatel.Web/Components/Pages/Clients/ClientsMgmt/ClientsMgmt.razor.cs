using Microsoft.AspNetCore.Components;
using MudBlazor;
using NetRatel.Web.Services;

namespace NetRatel.Web.Components.Pages.Clients.ClientsMgmt;

public partial class ClientsMgmt
{
    [Inject] public IClientUpdateActivityStream ActivityStream { get; set; } = null!;
    [Inject] public TimeProvider ActivityClock { get; set; } = null!;

    private static readonly TimeSpan ActivityRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActivityEditQuietPeriod = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _activityReads = new(1, 1);
    private readonly HashSet<string> _activityPopups = [];
    private CancellationTokenSource? _activityCancellation;
    private CancellationTokenSource? _activityRead;
    private Task? _activityRefreshTask;
    private long _activityGeneration;
    private long? _activityLastRead;
    private long? _activityLastEdit;
    private bool _activityRefreshPending;
    private bool _activityBackendConnected;
    private bool _activityHasSnapshot;
    private string _activityStatus = "Paused";
    private string? _activityError;
    private DateTimeOffset? _activityUpdatedAt;
    private TableGroupDefinition<ActivityRow>? _activityGroup;
    private IReadOnlyList<ActivityRow> _activityItems = [];
    private int _activityPage;
    private int? _activityRecoveryPage;
    private int _activityPageSize = 20;
    private int _activityTotal;

    // MudTable keys each main row by its item. Keep that key when its evidence changes.
    private sealed record ActivityRow(ClientUpdateHistoryItemModel Item)
    {
        public bool Equals(ActivityRow? other) => Item.AttemptId == other?.Item.AttemptId;
        public override int GetHashCode() => Item.AttemptId.GetHashCode();
    }

    private readonly Dictionary<string, long> _activityExpandedGroups = [];
    private readonly HashSet<Guid> _expandedActivityAttempts = [];
    private readonly Dictionary<Guid, ElementReference> _activityDetailLaunchers = [];
    private Guid? _restoreActivityDetailsFocus;
    private long _activityGroupOrder;
    private string ActivityGroupKey(object? key) => _attemptGrouping + "\0" + key;
    private bool IsActivityGroupExpanded(object? key) => _activityExpandedGroups.ContainsKey(ActivityGroupKey(key));
    private void ToggleActivityGroup(object? key)
    {
        var group = ActivityGroupKey(key);
        if (!_activityExpandedGroups.Remove(group))
        {
            _activityExpandedGroups[group] = ++_activityGroupOrder;
            if (_activityExpandedGroups.Count > 256)
                _activityExpandedGroups.Remove(_activityExpandedGroups.MinBy(pair => pair.Value).Key);
        }
    }

    private string ActivityRowStyle(ActivityRow item, int _) =>
        AttemptGroup?.Selector is { } selector && !IsActivityGroupExpanded(selector(item)) ? "display:none" : string.Empty;

    private void ToggleActivityDetails(Guid attemptId) => SetActivityDetailsExpanded(attemptId, !_expandedActivityAttempts.Contains(attemptId));

    private void SetActivityDetailsExpanded(Guid attemptId, bool expanded)
    {
        if (expanded) _expandedActivityAttempts.Add(attemptId);
        else if (_expandedActivityAttempts.Remove(attemptId)) _restoreActivityDetailsFocus = attemptId;
    }

    private static string ActivityTime(DateTimeOffset? at) => at is { } value
        ? value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'") : "not recorded";

    private static string InstallationSummary(ClientUpdateHistoryItemModel item) => item.RetiredAtUtc != null ? "Installation: unknown physical outcome; authority retired" : (item.ReportedState ?? item.Status) switch
    {
        "RolledBack" => "Installation: previous version restored",
        "RollbackUnverified" => "Installation: rollback unverified",
        "FailedPreActivation" => "Installation: activation not completed",
        "Accepted" => item.ConfirmationId is not null ? "Installation: target confirmed at gateway" : "Installation: confirmation detail unavailable",
        _ => "Installation: in progress"
    };

    private static string ServerReportSummary(ClientUpdateHistoryItemModel item) => item.RetiredAtUtc != null ? "Server authority: retired; outcome remains unknown" : item.ReconciliationState is not null
        ? "Server report: conflicts with accepted confirmation"
        : item.Status is "RolledBack" or "RollbackUnverified" or "FailedPreActivation" ? "Server report: received"
        : item.Status == "Accepted" ? "Server confirmation: recorded" : "Server report: awaiting terminal result";

    private static string OperatorAction(ClientUpdateHistoryItemModel item) => item.RetiredAtUtc != null ? "Decommissioned update authority retired; original admission and result evidence retained. Late work is fenced." : item.ReconciliationState is not null
        ? "Action: reconcile the running version and installation evidence before resuming automatic updates."
        : item.SuspendedAtUtc is not null
            ? "Action: review the installation outcome; resume affects future automatic updates only."
            : "Action: no operator intervention recorded as required.";

    private async Task StartActivityAsync()
    {
        if (_activeTab != ActivityTab || _automationDisposed || _activityCancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        _activityCancellation = cancellation;
        var generation = ++_activityGeneration;
        try
        {
            await ActivityStream.StartAsync(_attemptTenantId, notification => InvokeAsync(() =>
            {
                if (!IsActivityCurrent(generation)) return;
                _activityBackendConnected = notification.Connected;
                var previousStatus = _activityStatus;
                if (!notification.Connected) _activityStatus = "Reconnecting";
                if (notification.Kind is "ready" or "invalidate")
                {
                    var wasPending = _activityRefreshPending;
                    _activityRefreshPending = true;
                    ScheduleActivityRefresh();
                    if (!wasPending) StateHasChanged();
                }
                if (previousStatus != _activityStatus) StateHasChanged();
            }), status => _ = InvokeAsync(() =>
            {
                if (!IsActivityCurrent(generation)) return;
                var statusChanged = _activityStatus != status;
                _activityStatus = status;
                if (status != "Live") _activityBackendConnected = false;
                if (statusChanged) StateHasChanged();
            }), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || generation != _activityGeneration)
        {
            // A later tenant or tab can replace a stream while startup is draining.
            return;
        }
    }

    private async Task StopActivityAsync()
    {
        ++_activityGeneration;
        var cancellation = _activityCancellation;
        _activityCancellation = null;
        _activityRead = null;
        cancellation?.Cancel();
        _activityRefreshTask = null;
        _activityRefreshPending = false;
        _activityBackendConnected = false;
        _activityStatus = "Paused";
        _activityPopups.Clear();
        _activityLastEdit = null;
        await ActivityStream.StopAsync();
        cancellation?.Dispose();
    }

    private bool IsActivityCurrent(long generation) =>
        generation == _activityGeneration && _activeTab == ActivityTab && !_automationDisposed;

    private async Task ActivityScopeChangedAsync(bool tenantChanged = false, bool editing = false)
    {
        if (editing) _activityLastEdit = ActivityClock.GetTimestamp();
        _activityPage = 0;
        ClearActivitySnapshot();
        if (tenantChanged || _activityCancellation is null)
        {
            // Tenant is the SSE scope. Other filters only change the history read.
            _activityExpandedGroups.Clear();
            _expandedActivityAttempts.Clear();
            var expectedGeneration = _activityGeneration + 1;
            await StopActivityAsync();
            if (!IsActivityCurrent(expectedGeneration)) return;
            await StartActivityAsync();
            if (!IsActivityCurrent(expectedGeneration + 1)) return;
        }
        await RefreshActivityAsync();
    }

    private void ClearActivitySnapshot()
    {
        _activityItems = [];
        _activityTotal = 0;
        _activityRecoveryPage = null;
        _activityHasSnapshot = false;
        _activityUpdatedAt = null;
        _activityError = null;
        _activityDetailLaunchers.Clear();
    }

    private async Task ChangeActivityPageAsync(int page)
    {
        _activityPage = page;
        ClearActivitySnapshot();
        await RefreshActivityAsync();
    }

    private Task ChangeActivityPageSizeAsync(int size)
    {
        _activityPageSize = size;
        return ChangeActivityPageAsync(0);
    }

    private void SetActivityPopup(string popup, bool open)
    {
        if (open) _activityPopups.Add(popup);
        else _activityPopups.Remove(popup);
        ScheduleActivityRefresh();
    }

    private TimeSpan ActivityQuietTimeRemaining =>
        _activityLastEdit is { } edit ? ActivityEditQuietPeriod - ActivityClock.GetElapsedTime(edit) : TimeSpan.Zero;

    private void ScheduleActivityRefresh()
    {
        if (_activityCancellation is not { } scope || !_activityRefreshPending || _activityRead is not null ||
            _activityRefreshTask is not null || _activityPopups.Count != 0) return;
        _activityRefreshTask = CoalesceActivityRefreshAsync(_activityGeneration, scope.Token);
    }

    // A trailing read consumes all dirty signals received before it starts. While
    // typing or a popup is open, keep the current page and leave one pending read.
    private async Task CoalesceActivityRefreshAsync(long generation, CancellationToken ct)
    {
        await Task.Yield();
        try
        {
            while (IsActivityCurrent(generation) && !ct.IsCancellationRequested && _activityRefreshPending &&
                   _activityPopups.Count == 0 && _activityRead is null)
            {
                var untilBudget = _activityLastRead is { } last ? ActivityRefreshInterval - ActivityClock.GetElapsedTime(last) : TimeSpan.Zero;
                var untilQuiet = ActivityQuietTimeRemaining;
                var delay = untilBudget > untilQuiet ? untilBudget : untilQuiet;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, ActivityClock, ct);
                    continue;
                }

                await ReadActivityAsync(automatic: true);
                if (IsActivityCurrent(generation)) StateHasChanged();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The outgoing tab or tenant owns this wait.
            return;
        }
        finally
        {
            if (generation == _activityGeneration)
            {
                _activityRefreshTask = null;
                ScheduleActivityRefresh();
            }
        }
    }

    private Task RefreshActivityAsync() => ReadActivityAsync(automatic: false);

    private async Task ReadActivityAsync(bool automatic)
    {
        var generation = _activityGeneration;
        if (_activityCancellation is not { } scope || !IsActivityCurrent(generation)) return;

        _activityRead?.Cancel();
        using var read = CancellationTokenSource.CreateLinkedTokenSource(scope.Token);
        _activityRead = read;
        var token = read.Token;
        var search = EmptyToNull(_attemptSearch);
        var status = EmptyToNull(_attemptStatus);
        var runtime = EmptyToNull(_attemptRid);
        var tenant = _attemptTenantId;
        var version = EmptyToNull(_attemptVersion);
        var requestedPage = _activityRecoveryPage ?? _activityPage;
        var pageSize = _activityPageSize;
        try
        {
            await _activityReads.WaitAsync(token);
            try
            {
                if (automatic && ActivityInteractionInProgress) return;
                _activityLastRead = ActivityClock.GetTimestamp();
                _activityRefreshPending = false;
                var page = await Artifacts.GetUpdateHistoryAsync(requestedPage, pageSize, search, status, null, runtime, tenant, version, token);
                token.ThrowIfCancellationRequested();

                var lastPage = Math.Max(0, (page.Total - 1) / pageSize);
                if (requestedPage > lastPage)
                {
                    if (automatic)
                    {
                        _activityRecoveryPage = lastPage;
                        _activityRefreshPending = true;
                        return;
                    }
                    page = await Artifacts.GetUpdateHistoryAsync(lastPage, pageSize, search, status, null, runtime, tenant, version, token);
                    token.ThrowIfCancellationRequested();
                }

                if (automatic && ActivityInteractionInProgress)
                {
                    _activityRefreshPending = true;
                    return;
                }
                ApplyActivityPage(page);
            }
            finally { _activityReads.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Only the latest page and filter request may apply.
            return;
        }
        catch (Exception exception) when (exception is HttpRequestException or NetRatel.Web.Services.Authentication.ReauthRequiredException or
                                          System.Text.Json.JsonException or OperationCanceledException)
        {
            if (!IsActivityCurrent(generation) || token.IsCancellationRequested) return;
            var accessLost = exception is NetRatel.Web.Services.Authentication.ReauthRequiredException or
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };
            if (accessLost) ClearActivitySnapshot();
            _activityError = _activityHasSnapshot
                ? "Activity refresh failed. Showing the last loaded page; use Refresh to retry."
                : "Activity refresh failed. No snapshot is available for these filters and page; use Refresh to retry.";
            _activityStatus = accessLost ? "Paused" : "Reconnecting";
            if (accessLost) _activityBackendConnected = false;
        }
        finally
        {
            if (ReferenceEquals(_activityRead, read))
            {
                _activityRead = null;
                ScheduleActivityRefresh();
            }
        }
    }

    private bool ActivityInteractionInProgress => _activityPopups.Count != 0 || ActivityQuietTimeRemaining > TimeSpan.Zero;

    private void ApplyActivityPage(ClientUpdateHistoryPageModel page)
    {
        _activityItems = page.Items.Select(item => new ActivityRow(item)).ToArray();
        var currentAttempts = page.Items.Select(item => item.AttemptId).ToHashSet();
        foreach (var attemptId in _activityDetailLaunchers.Keys.Where(id => !currentAttempts.Contains(id)).ToArray())
            _activityDetailLaunchers.Remove(attemptId);
        _activityTotal = page.Total;
        _activityPage = page.Page;
        _activityRecoveryPage = null;
        _activityHasSnapshot = true;
        _activityUpdatedAt = ActivityClock.GetUtcNow();
        _activityError = null;
        if (_activityBackendConnected) _activityStatus = "Live";
    }
}
