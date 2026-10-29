using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Web.Components.Pages.AIOps;

public partial class Chat
{
    [SupplyParameterFromQuery(Name = "tenantId")] public int? RequestedTenant { get; set; }
    [SupplyParameterFromQuery(Name = "chat")] public Guid? RequestedChat { get; set; }

    private IReadOnlyList<NetclawTenantDto> _tenants = [];
    private IReadOnlyList<InvestigationSummaryDto> _items = [];
    private readonly List<InvestigationEventDto> _events = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _selectionScope = new();
    private Task _polling = Task.CompletedTask;
    private IJSObjectReference? _scrollModule, _timeModule;
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;
    private NetclawConnectionDto? _connection;
    private InvestigationSnapshotDto? _snapshot;
    private InvestigationMessageRequest? _pendingMessage;
    private GeneralChatCreateRequest? _pendingCreate;
    private (int? Tenant, Guid? Chat)? _loadedRoute;
    private ElementReference _transcript, _composer;
    private string _message = "", _search = "";
    private string? _error, _liveError;
    private int _tenant, _generation;
    private bool _initialized, _loading = true, _busy, _disposed, _showArchived, _confirmArchive, _acknowledgeRemote;
    private bool _scrollRequested, _forceScroll, _timeLoaded;

    private NetclawTenantDto? Access => _tenants.FirstOrDefault(x => x.TenantId == _tenant);
    private bool ConnectionReady => _connection is { Enabled: true, HasDeviceToken: true } && _connection.TenantId == _tenant;
    private bool CanStart => Access?.CanInvestigate == true && ConnectionReady;
    private bool CanEditMessage => CanStart && !_busy && _pendingMessage is null && (_snapshot is null || _snapshot.Investigation.State == InvestigationState.Idle);
    private bool CanSend => CanStart && !_busy && !string.IsNullOrWhiteSpace(_message) && (_snapshot is null || _snapshot.Investigation.State == InvestigationState.Idle);
    private long Cursor => _events.LastOrDefault()?.Sequence ?? 0;
    private string ConnectionUrl => $"/account/integration-credentials?tab=netclaw&tenantId={_tenant}";
    private IEnumerable<InvestigationSummaryDto> FilteredItems => _items
        .Where(x => (_showArchived || x.State != InvestigationState.Archived) &&
            (string.IsNullOrWhiteSpace(_search) || x.Title.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase)))
        .OrderByDescending(x => x.LastActivityUtc);

    private static readonly ChatStarter[] Starters =
    [
        new("Create a script", "Turn a task into a reusable script.", Icons.Material.Outlined.Code,
            "Help me create a script. Ask me what it should do and which environment it will run in."),
        new("Review notifications", "Find out what needs attention.", Icons.Material.Outlined.NotificationsNone,
            "Review the notifications I can access in this tenant. Summarize what needs attention and suggest next steps."),
        new("Plan a flow", "Work through an automation idea.", Icons.Material.Outlined.AccountTree,
            "Help me plan an automation flow. Ask me about the trigger, the desired outcome and the steps involved."),
        new("Explore a question", "Get help with an operational task.", Icons.Material.Outlined.HelpOutline,
            "Help me work through an operational question. Ask me what I want to understand or achieve.")
    ];

    protected override async Task OnInitializedAsync()
    {
        try { _tenants = await Api.TenantsAsync(_lifetime.Token); }
        catch (HttpRequestException exception) { _error = exception.Message; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { _error = "The workspace request timed out. Reload the page to reconnect."; }
        _initialized = true;
        _polling = PollAsync(_lifetime.Token);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_initialized || _disposed || _loadedRoute == (RequestedTenant, RequestedChat)) return;
        _loadedRoute = (RequestedTenant, RequestedChat);
        _selectionScope.Cancel();
        _selectionScope.Dispose();
        _selectionScope = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = _selectionScope.Token;
        var generation = ++_generation;
        _tenant = RequestedTenant is { } requested
            ? _tenants.FirstOrDefault(x => x.TenantId == requested)?.TenantId ?? 0
            : _tenants.FirstOrDefault()?.TenantId ?? 0;
        var tenant = _tenant;
        _snapshot = null; _events.Clear(); _items = []; _connection = null;
        _message = _search = ""; _pendingMessage = null; _pendingCreate = null;
        _busy = _confirmArchive = _acknowledgeRemote = _showArchived = false;
        _liveError = null; _loading = true;
        if (_tenant == 0)
        {
            if (RequestedTenant.HasValue) _error = "The linked tenant is unavailable to this account.";
            _loading = false;
            return;
        }
        _error = null;
        try
        {
            var chats = Api.ListChatsAsync(tenant, ct);
            var connection = Api.ConnectionAsync(tenant, ct);
            await Task.WhenAll(chats, connection);
            if (!Current(tenant, generation)) return;
            _items = await chats; _connection = await connection;
            if (RequestedChat is { } id)
            {
                var snapshot = await Api.GetAsync(tenant, id, ct: ct);
                if (Current(tenant, generation)) Apply(snapshot, forceScroll: true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { if (Current(tenant, generation)) _error = "The chat request timed out. Refresh chats to reconnect."; }
        catch (HttpRequestException exception) { if (Current(tenant, generation)) _error = exception.Message; }
        finally { if (Current(tenant, generation)) _loading = false; }
    }

    private bool Current(int tenant, int generation) => !_disposed && tenant == _tenant && generation == _generation;
    private static string ChatUrl(int tenant, Guid? id = null) => id is { } chat
        ? $"/ai/chat?tenantId={tenant}&chat={chat:D}"
        : $"/ai/chat?tenantId={tenant}";

    private void ChooseTenant(ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), out var tenant) && tenant != _tenant && _tenants.Any(x => x.TenantId == tenant))
            Navigation.NavigateTo(ChatUrl(tenant));
    }

    private void NewChat()
    {
        if (_busy || !CanStart) return;
        if (_snapshot is null && RequestedChat is null)
        {
            _message = ""; _pendingMessage = null; _pendingCreate = null; _error = null;
            return;
        }
        Navigation.NavigateTo(ChatUrl(_tenant));
    }

    private async Task UseStarterAsync(string prompt)
    {
        if (!CanEditMessage) return;
        _message = prompt;
        await _composer.FocusAsync();
    }

    private Task ComposerKeyDownAsync(KeyboardEventArgs args) =>
        args.Key == "Enter" && (args.CtrlKey || args.MetaKey) && CanSend ? SendAsync() : Task.CompletedTask;

    private async Task ExecuteAsync(Func<int, int, CancellationToken, Task> action)
    {
        if (_busy || _disposed || _tenant == 0) return;
        var tenant = _tenant; var generation = _generation; var ct = _selectionScope.Token;
        _busy = true; _error = null;
        try { await action(tenant, generation, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { if (Current(tenant, generation)) _error = "The request timed out. Refresh the saved conversation or retry the same message."; }
        catch (HttpRequestException exception) { if (Current(tenant, generation)) _error = exception.Message; }
        finally { if (Current(tenant, generation)) _busy = false; }
    }

    private Task RefreshAsync() => ExecuteAsync(async (tenant, generation, ct) =>
    {
        var chats = Api.ListChatsAsync(tenant, ct);
        var connection = Api.ConnectionAsync(tenant, ct);
        await Task.WhenAll(chats, connection);
        if (!Current(tenant, generation)) return;
        _items = await chats; _connection = await connection; _liveError = null;
        var id = _snapshot?.Investigation.Id ?? RequestedChat;
        if (id is { } selected)
        {
            var snapshot = await Api.GetAsync(tenant, selected, Cursor, ct);
            if (Current(tenant, generation)) Apply(snapshot);
        }
    });

    private Task SendAsync() => ExecuteAsync(async (tenant, generation, ct) =>
    {
        if (!CanStart || string.IsNullOrWhiteSpace(_message) || _snapshot is not null && _snapshot.Investigation.State != InvestigationState.Idle) return;
        var message = _pendingMessage ??= new(Guid.NewGuid(), _message.Trim());
        try
        {
            if (_snapshot is null)
            {
                _pendingCreate ??= new(Guid.NewGuid(), CreateTitle(message.Text));
                var created = await Api.CreateChatAsync(tenant, _pendingCreate, ct);
                if (!Current(tenant, generation)) return;
                Apply(created);
                if (_snapshot is null) return;
                _pendingCreate = null;
                _loadedRoute = (tenant, created.Investigation.Id);
                Navigation.NavigateTo(ChatUrl(tenant, created.Investigation.Id), replace: true);
                if (_pendingMessage is null) return; // The idempotent create readback already confirmed this message.
            }
            var accepted = await Api.SendAsync(tenant, _snapshot.Investigation.Id, message, ct);
            if (!Current(tenant, generation)) return;
            Apply(accepted, forceScroll: true);
            _pendingMessage = null; _message = "";
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.BadRequest)
        {
            if (Current(tenant, generation)) { _pendingMessage = null; _pendingCreate = null; }
            throw;
        }
    });

    private Task ApproveAsync(string callId, string key) => ExecuteAsync(async (tenant, generation, ct) =>
    {
        if (_snapshot is null) return;
        var snapshot = await Api.ApproveAsync(tenant, _snapshot.Investigation.Id, callId, key, ct);
        if (Current(tenant, generation)) Apply(snapshot);
    });

    private Task RecoverAsync() => ExecuteAsync(async (tenant, generation, ct) =>
    {
        if (_snapshot is null) return;
        var snapshot = await Api.ReconcileAsync(tenant, _snapshot.Investigation.Id, ct);
        if (Current(tenant, generation)) Apply(snapshot);
    });

    private Task ArchiveAsync() => ExecuteAsync(async (tenant, generation, ct) =>
    {
        if (_snapshot is null) return;
        var snapshot = await Api.ArchiveAsync(tenant, _snapshot.Investigation.Id,
            new(_snapshot.ActiveMessageId ?? Guid.Empty, _acknowledgeRemote), ct);
        if (!Current(tenant, generation)) return;
        Apply(snapshot); _confirmArchive = _acknowledgeRemote = false;
    });

    private Task LoadMoreAsync() => ExecuteAsync(async (tenant, generation, ct) =>
    {
        if (_snapshot is null) return;
        var snapshot = await Api.GetAsync(tenant, _snapshot.Investigation.Id, Cursor, ct);
        if (Current(tenant, generation)) Apply(snapshot);
    });

    private void Apply(InvestigationSnapshotDto snapshot, bool forceScroll = false)
    {
        if (snapshot.Investigation.TenantId != _tenant || _snapshot is not null &&
            (_snapshot.Investigation.Id != snapshot.Investigation.Id || _snapshot.LastSequence > snapshot.LastSequence)) return;
        if (!snapshot.Investigation.IsGeneralChat)
        {
            _error = "This conversation is an investigation. Open it from AI Ops → Investigations.";
            return;
        }
        var previousCursor = Cursor;
        var previousDraft = _snapshot?.Draft;
        foreach (var item in snapshot.Events)
            if (_events.All(x => x.Sequence != item.Sequence)) _events.Add(item);
        _events.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
        _snapshot = snapshot;
        if (_pendingMessage is not null && _events.Any(x => x.Type == "operator" && x.MessageId == _pendingMessage.MessageId))
        {
            _pendingMessage = null; _message = "";
        }
        _items = _items.Where(x => x.Id != snapshot.Investigation.Id).Append(snapshot.Investigation).ToArray();
        _scrollRequested |= forceScroll || previousCursor != Cursor || previousDraft != snapshot.Draft;
        _forceScroll |= forceScroll;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_loading || _busy || _tenant == 0) continue;
                await InvokeAsync(async () =>
                {
                    var tenant = _tenant; var generation = _generation; var id = _snapshot?.Investigation.Id;
                    try
                    {
                        if (id is { } selected)
                        {
                            var snapshot = await Api.GetAsync(tenant, selected, Cursor, ct);
                            if (Current(tenant, generation) && _snapshot?.Investigation.Id == selected) { Apply(snapshot); _liveError = null; }
                        }
                        if (++ticks % 5 == 0)
                        {
                            var chats = await Api.ListChatsAsync(tenant, ct);
                            if (Current(tenant, generation)) _items = chats;
                        }
                    }
                    catch (HttpRequestException) { if (Current(tenant, generation)) _liveError = "Live updates are reconnecting. Your saved conversation remains visible."; }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { if (Current(tenant, generation)) _liveError = "Live updates timed out. Refresh chats to reconnect."; }
                    if (!_disposed) StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || _loading || _tenant == 0) return;
        try
        {
            if (!_timeLoaded)
            {
                _timeLoaded = true;
                try
                {
                    _timeModule = await JS.InvokeAsync<IJSObjectReference>("import", "./js/operator-time.js");
                    var zone = await _timeModule.InvokeAsync<string>("getTimeZone");
                    if (!string.IsNullOrWhiteSpace(zone)) _timeZone = TimeZoneInfo.FindSystemTimeZoneById(zone);
                }
                catch (Exception exception) when (exception is JSException or TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    _timeZone = TimeZoneInfo.Utc;
                }
                if (!_disposed) StateHasChanged();
            }
            _scrollModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/netclaw-chat.js");
            if (_snapshot is not null && _scrollRequested)
                await _scrollModule.InvokeVoidAsync("followOutput", _transcript, _forceScroll);
            if (ConnectionReady && _snapshot?.Investigation.State != InvestigationState.Archived)
                await _scrollModule.InvokeVoidAsync("prepareComposer", _composer);
            _scrollRequested = _forceScroll = false;
        }
        catch (JSDisconnectedException) { return; }
    }

    private static string CreateTitle(string question)
    {
        var line = question.Split('\n')
            .Select(value => new string(value.Select(character => char.IsControl(character) || char.IsWhiteSpace(character) ? ' ' : character).ToArray()))
            .Select(value => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .FirstOrDefault(value => value.Length > 0) ?? "New chat";
        return line.Length <= 100 ? line : line[..(char.IsHighSurrogate(line[96]) ? 96 : 97)] + "…";
    }

    private string MessageTime(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _timeZone).ToString(_timeZone == TimeZoneInfo.Utc ? "HH:mm 'UTC'" : "HH:mm");
    private string HistoryDate(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _timeZone).ToString(_timeZone == TimeZoneInfo.Utc ? "dd MMM 'UTC'" : "dd MMM");
    private string TimestampTitle(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _timeZone).ToString("yyyy-MM-dd HH:mm:ss zzz") + $" ({_timeZone.Id})";

    private static string StateLabel(InvestigationState state) => state switch
    {
        InvestigationState.Idle => "Ready",
        InvestigationState.Processing => "Working",
        InvestigationState.AwaitingApproval => "Decision needed",
        InvestigationState.DeliveryUnknown => "Reconnect needed",
        InvestigationState.Queued => "Queued",
        _ => "Archived"
    };

    private static string StateClass(InvestigationState state) => state switch
    {
        InvestigationState.Processing or InvestigationState.Queued => "active",
        InvestigationState.AwaitingApproval or InvestigationState.DeliveryUnknown => "attention",
        _ => ""
    };

    public async ValueTask DisposeAsync()
    {
        _disposed = true; ++_generation;
        await _lifetime.CancelAsync();
        await _selectionScope.CancelAsync();
        await _polling;
        _selectionScope.Dispose(); _lifetime.Dispose();
        if (_scrollModule is not null)
        {
            try { await _scrollModule.DisposeAsync(); }
            catch (JSDisconnectedException) { return; }
        }
        if (_timeModule is not null)
        {
            try { await _timeModule.DisposeAsync(); }
            catch (JSDisconnectedException) { return; }
        }
    }

    private sealed record ChatStarter(string Title, string Description, string Icon, string Prompt);
}
