using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Microsoft.JSInterop;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Web.Services.Monitoring;

namespace NetRatel.Web.Components.Pages.Monitoring;

public partial class MonitoringPage
{
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    private ElementReference _newRuleLauncher;
    private readonly Dictionary<Guid, ElementReference> _ruleLaunchers = [];
    private ElementReference? _editorLauncher, _restoreEditorFocus;
    private int _editorLauncherTenant;
    private long _focusCloseGeneration;
    private string? _editorLauncherUri;
    private MonitoringPageState? _state;
    private MonitoringPageState State => _state ??= new(Api);
    private IReadOnlyList<MonitoringTenantDto> _tenants = [];
    private readonly CancellationTokenSource _lifetime = new();
    private string? _startupError, _editor, _editorError;
    private string _tab = "Active", _search = "", _reason = "", _groupName = "", _deleteName = "", _deleteCollection = "";
    private bool _interactive, _busy, _formDirty, _previewBusy;
    private MonitoringRuleDraft? _draft;
    private MonitoringGroupDto? _originalGroup;
    private HashSet<Guid> _groupMembers = [];
    private MonitoringSeriesState? _operatorSeries;
    private Guid _deleteId, _bypassId;
    private Guid? _bypassRule, _bypassGroup, _bypassAgent, _inventoryAgent;
    private string _bypassResource = "";
    private int? _bypassMinutes = 60;
    private MonitoringTargetPreviewDto? _preview;
    private long _editorGeneration, _previewGeneration, _inventoryGeneration;
    private ulong? _pendingWatchAtSaveRevision;
    private int _pendingWatchAtSaveTenant;
    private CancellationTokenSource? _previewRead, _inventoryRead;
    private string _inventoryNote = "Suggestions read cached inventory only. No collection is requested.";
    private IReadOnlyList<ClientServiceObservation> _serviceSuggestions = [];
    private static readonly ClientServiceState[] ExpectedStateOptions = [ClientServiceState.Running, ClientServiceState.Stopped, ClientServiceState.Failed, ClientServiceState.Starting, ClientServiceState.Stopping, ClientServiceState.Paused];
    private string EditorTitle => _editor switch { "rule" => "Rule", "group" => "Group", "ack" => "Acknowledge occurrence", "clear" => "Clear occurrence", "delete" => "Remove configuration", _ => "Bypass" };
    private IEnumerable<MonitoringSeriesState> ActiveRows => State.Snapshot?.Series.Items.Where(row =>
        (row.Occurrence is { EndedAtUtc: null } || row.Phase == MonitoringPhase.Pending || Health(row) == "Unknown") &&
        (Contains(RuleName(row.Series.RuleId)) || Contains(ClientName(row.Series.AgentId)) || Contains(row.Series.ResourceKey))) ?? [];

    protected override async Task OnInitializedAsync()
    {
        try { _tenants = await Api.GetTenantsAsync(_lifetime.Token); if (_tenants.Count > 0) await State.SelectTenantAsync(_tenants[0].TenantId); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException) { _startupError = "Could not load authorized monitoring tenants. Refresh to try again."; }
    }
    protected override void OnAfterRender(bool firstRender) { if (firstRender) { _interactive = true; StateHasChanged(); } }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_restoreEditorFocus is not { } launcher) return;
        _restoreEditorFocus = null;
        if (_lifetime.IsCancellationRequested || _editor is not null || _tab != "Manage" ||
            _focusCloseGeneration != _editorGeneration || State.TenantId != _editorLauncherTenant ||
            Navigation.Uri != _editorLauncherUri || State.Snapshot?.Permissions.CanManage != true) return;
        // The close render has removed the editor and its trap; return to its actual launcher.
        try { await launcher.FocusAsync(); }
        catch (JSDisconnectedException) { }
    }
    private async Task ChangeTenant(ChangeEventArgs args)
    {
        if (_editor is not null || !int.TryParse(args.Value?.ToString(), out var tenant) || !_tenants.Any(t => t.TenantId == tenant)) return;
        CloseEditor(); _search = ""; _pendingWatchAtSaveRevision = null; await State.SelectTenantAsync(tenant);
    }
    private Task Refresh() => State.RefreshAsync();
    private Task PageSeries(string? cursor) => State.NextSeriesPageAsync(cursor);
    private Task PageHistory(string? cursor) => State.NextHistoryPageAsync(cursor);
    private bool Contains(string? text) => string.IsNullOrWhiteSpace(_search) || text?.Contains(_search, StringComparison.OrdinalIgnoreCase) == true;
    private bool MatchesHistory(MonitoringEventIntent item) => Contains(item.PinnedRule.Name) || Contains(ClientName(item.Series.AgentId)) || Contains(item.Series.ResourceKey);
    private static string ExplainCode(string? code) => code switch
    {
        "platform-mismatch" => "The client platform does not match this rule",
        "capability-not-negotiated" => "The client has not enabled service telemetry",
        "collection-unsupported" => "Service collection is unsupported",
        "cached-service-evidence" or "cached-metric-evidence" or "cached_evidence_only" => "Cached evidence is available; current health still requires fresh observations",
        "evidence-unavailable" or "stream_unavailable" => "Current evidence is unavailable",
        "stale" => "The latest observation is stale",
        "partial" => "The latest collection is incomplete",
        "unsupported" => "This observation is unsupported",
        "numeric_uncertainty" => "Measurement precision overlaps a threshold",
        "before_evaluation_fence" or "before_receipt_fence" => "A new observation is required after the configuration or clear",
        "future_clock" or "invalid_clock" or "reordered_clock" => "Observation timing could not be validated",
        "stale_watch_policy" => "The current selected service policy is not yet reflected in this observation",
        "unknown_service" or "unproven_missing" => "Current service state is unknown",
        null or "" => "",
        _ => "Current support or evidence has not been confirmed"
    };
    private string RuleName(Guid id) => State.Snapshot?.Configuration.Rules.FirstOrDefault(r => r.RuleId == id)?.Name ?? id.ToString();
    private string ClientName(Guid id) => State.Snapshot?.Clients.Items.FirstOrDefault(c => c.AgentId == id)?.DisplayName ?? id.ToString();
    private static string Time(DateTimeOffset? at) => at?.ToLocalTime().ToString("g") ?? "—";
    private string Health(MonitoringSeriesState row)
    {
        var now = DateTimeOffset.UtcNow;
        var budget = State.Snapshot?.Configuration.Rules.FirstOrDefault(rule => rule.RuleId == row.Series.RuleId)?.FreshnessBudget ?? TimeSpan.Zero;
        var evidence = row.LatestEvidence;
        return row.EvidenceQuality == MonitoringEvidenceQuality.Unknown || evidence is null || evidence.ObservedAtUtc > now || evidence.ReceivedAtUtc > now ||
            now - evidence.ObservedAtUtc > budget || now - evidence.ReceivedAtUtc > budget ? "Unknown" : evidence.Classification.ToString();
    }
    private string EvidenceValue(MonitoringSeriesState row)
    {
        if (row.LatestEvidence is not { } evidence) return "Unknown";
        if (evidence.ServiceState is { } service) return service.ToString();
        if (evidence.NumericValue is not { } value) return "Unknown";
        var kind = State.Snapshot?.Configuration.Rules.FirstOrDefault(rule => rule.RuleId == row.Series.RuleId)?.Condition.Kind;
        var number = kind == MonitoringMetricKind.DiskFreeSpace ? $"{value / Math.Pow(1024, 3):G6} GiB free" : $"{value:G6}%";
        return number + (evidence.NumericResolution > 0 ? " · precision interval retained" : "");
    }
    private IEnumerable<MonitoringBypassDto> AppliedBypasses(MonitoringSeriesState row) => State.Snapshot?.Configuration.Bypasses.Where(b =>
        !row.ApplicableBypassIds.IsDefault && row.ApplicableBypassIds.Contains(b.BypassId)) ?? [];

    private void BeginEditor(string kind)
    {
        if (_busy) return;
        CloseEditor(); _restoreEditorFocus = null; _editor = kind; _editorGeneration++; _reason = ""; _formDirty = false; _editorError = null;
    }
    private void OpenRule(MonitoringRuleDto? rule) { if (_busy || rule is not null && rule.TenantId != State.TenantId) return; BeginEditor("rule"); _draft = MonitoringRuleDraft.Create(rule); }
    private void OpenRuleFromLauncher(MonitoringRuleDto? rule, ElementReference launcher)
    {
        var generation = _editorGeneration;
        OpenRule(rule);
        if (_editorGeneration == generation || _editor != "rule") return;
        _editorLauncher = launcher;
        _editorLauncherTenant = State.TenantId;
        _editorLauncherUri = Navigation.Uri;
    }
    private void OpenGroup(MonitoringGroupDto? group) { if (_busy || group is not null && group.TenantId != State.TenantId) return; BeginEditor("group"); _originalGroup = group; _groupName = group?.Name ?? ""; _groupMembers = group?.AgentIds.ToHashSet() ?? []; }
    private void OpenOperator(MonitoringSeriesState series, string operation)
    {
        if (_busy || series.Series.TenantId != State.TenantId) return;
        BeginEditor(operation); _operatorSeries = series;
        if (operation == "bypass") { _bypassId = Guid.NewGuid(); _bypassRule = series.Series.RuleId; _bypassAgent = series.Series.AgentId; _bypassGroup = null; _bypassResource = series.Series.ResourceKey; _bypassMinutes = 60; }
    }
    private void OpenBypass() { BeginEditor("bypass"); _bypassId = Guid.NewGuid(); _bypassRule = _bypassAgent = _bypassGroup = null; _bypassResource = ""; _bypassMinutes = 60; }
    private void OpenDelete(string collection, Guid id, string name) { BeginEditor("delete"); _deleteCollection = collection; _deleteId = id; _deleteName = name; }
    private void CloseEditor()
    {
        if (_busy) return;
        _restoreEditorFocus = _editor == "rule" ? _editorLauncher : null;
        _editorLauncher = null;
        _previewRead?.Cancel(); _inventoryRead?.Cancel(); _editorGeneration++; _previewGeneration++; _inventoryGeneration++;
        _focusCloseGeneration = _editorGeneration;
        _editor = null; _draft = null; _operatorSeries = null; _preview = null; _previewBusy = false;
        _serviceSuggestions = []; _inventoryAgent = null; _inventoryNote = "Suggestions read cached inventory only. No collection is requested.";
    }
    private void HandleKey(KeyboardEventArgs args) { if (args.Key == "Escape" && !_busy) CloseEditor(); }
    private void MarkDirty() => _formDirty = true;
    private void InvalidatePreview() { _preview = null; _previewGeneration++; _previewRead?.Cancel(); _previewBusy = false; }
    private void MetricChanged(ChangeEventArgs args) { if (_draft is not null && Enum.TryParse<MonitoringMetricKind>(args.Value?.ToString(), out var kind)) { _draft.SelectMetric(kind); InvalidatePreview(); } }
    private void Toggle<T>(HashSet<T> values, T value, ChangeEventArgs args) { if (args.Value is true) values.Add(value); else values.Remove(value); MarkDirty(); InvalidatePreview(); }

    private RenderFragment ClientPicker(HashSet<Guid> selected) => builder =>
    {
        var snapshot = State.Snapshot;
        if (snapshot is null) return;
        builder.OpenElement(0, "fieldset"); builder.OpenElement(1, "legend"); builder.AddContent(2, "Explicit tenant clients"); builder.CloseElement();
        foreach (var client in snapshot.Clients.Items)
        {
            builder.OpenElement(3, "label"); builder.AddAttribute(4, "class", "monitoring-check"); builder.OpenElement(5, "input"); builder.AddAttribute(6, "type", "checkbox");
            builder.AddAttribute(7, "checked", selected.Contains(client.AgentId)); builder.AddAttribute(8, "data-testid", "monitoring-client-choice");
            builder.AddAttribute(9, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, args => Toggle(selected, client.AgentId, args))); builder.CloseElement();
            builder.AddContent(10, $"{client.DisplayName ?? client.AgentId.ToString()} · {client.ServicesSupport}"); builder.CloseElement();
        }
        foreach (var agent in selected.Where(id => !snapshot.Clients.Items.Any(client => client.AgentId == id)).Order())
        {
            builder.OpenElement(30, "label"); builder.AddAttribute(31, "class", "monitoring-check"); builder.OpenElement(32, "input");
            builder.AddAttribute(33, "type", "checkbox"); builder.AddAttribute(34, "checked", true);
            builder.AddAttribute(35, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, args => Toggle(selected, agent, args))); builder.CloseElement();
            builder.AddContent(36, $"{agent} · selected outside this client page; uncheck to remove"); builder.CloseElement();
        }
        if (snapshot.Clients.Items.IsEmpty) { builder.OpenElement(11, "p"); builder.AddContent(12, "No eligible clients on this page."); builder.CloseElement(); }
        builder.OpenElement(13, "p"); builder.AddContent(14, $"{selected.Count} selected; selections persist across client pages. {snapshot.Clients.Total} eligible total."); builder.CloseElement();
        builder.OpenElement(15, "button"); builder.AddAttribute(16, "class", "monitoring-button"); builder.AddAttribute(17, "disabled", State.PagingClients); builder.AddAttribute(18, "onclick", EventCallback.Factory.Create(this, () => State.NextClientsPageAsync(null))); builder.AddContent(19, "First clients"); builder.CloseElement();
        builder.OpenElement(20, "button"); builder.AddAttribute(21, "class", "monitoring-button"); builder.AddAttribute(22, "disabled", State.PagingClients || snapshot.Clients.NextCursor is null); builder.AddAttribute(23, "onclick", EventCallback.Factory.Create(this, () => State.NextClientsPageAsync(snapshot.Clients.NextCursor))); builder.AddContent(24, "More clients"); builder.CloseElement();
        builder.CloseElement();
    };

    private async Task Preview()
    {
        if (_previewBusy || _draft is null || State.Snapshot is null) return;
        _previewRead?.Cancel(); _previewRead?.Dispose(); _previewRead = CancellationTokenSource.CreateLinkedTokenSource(State.Token);
        var token = _previewRead.Token; var generation = ++_previewGeneration; var editor = _editorGeneration; var tenant = State.TenantId; var scope = State.Generation;
        _previewBusy = true; _editorError = null;
        try
        {
            var rule = _draft.Build(tenant);
            var result = await Api.PreviewTargetsAsync(tenant, new(rule.Targets, rule.Condition), token);
            if (State.IsCurrent(tenant, scope) && editor == _editorGeneration && generation == _previewGeneration)
            {
                if (result.ConfigurationRevision != State.Snapshot.Configuration.Revision) throw new HttpRequestException("Configuration changed. Refresh after cancelling this editor, then preview again.");
                _preview = result;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or ArgumentException)
        { if (State.IsCurrent(tenant, scope) && editor == _editorGeneration && generation == _previewGeneration) _editorError = error is HttpRequestException ? error.Message : "The target preview is unavailable. Check the exact selection."; }
        finally { if (editor == _editorGeneration && generation == _previewGeneration) _previewBusy = false; }
    }
    private async Task ReadInventory(ChangeEventArgs args)
    {
        _inventoryRead?.Cancel(); _inventoryRead?.Dispose(); _inventoryRead = CancellationTokenSource.CreateLinkedTokenSource(State.Token);
        var token = _inventoryRead.Token; var generation = ++_inventoryGeneration; var editor = _editorGeneration; var tenant = State.TenantId; var scope = State.Generation;
        _serviceSuggestions = []; _inventoryAgent = Guid.TryParse(args.Value?.ToString(), out var agent) ? agent : null;
        if (_inventoryAgent is null) return;
        _inventoryNote = "Reading cached inventory…";
        try
        {
            var model = await ServicesApi.GetAsync(tenant, agent, token);
            if (!State.IsCurrent(tenant, scope) || editor != _editorGeneration || generation != _inventoryGeneration) return;
            if (model is null || model.TenantId != tenant || model.AgentId != agent) { _inventoryNote = "No cached inventory is available. Enter the exact stable name manually."; return; }
            _serviceSuggestions = model.LastCompleteInventory?.Services ?? [];
            _inventoryNote = model.LastCompleteInventory is { } inventory ? $"Cached inventory observed {Time(inventory.ObservedAtUtc)} · {(model.Connected ? "connected" : "offline / stale")}. Suggestions do not establish current service health." : "No complete inventory. Exact names can be entered manually.";
            if (!model.SupportsServices) _inventoryNote += " Service collection is unsupported or has not been negotiated.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (HttpRequestException)
        { if (State.IsCurrent(tenant, scope) && editor == _editorGeneration && generation == _inventoryGeneration) _inventoryNote = "Inventory access is unavailable or not permitted. Enter the exact stable name manually."; }
    }
    private async Task Save()
    {
        if (_busy || _editor is null || State.Snapshot is not { } snapshot) return;
        var tenant = State.TenantId; var scope = State.Generation; var editor = _editorGeneration; var token = State.Token;
        var reason = _draft?.Reason ?? _reason;
        _editorError = null;
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512) { _editorError = "Enter a reason (up to 512 characters)."; return; }
        if (_draft is not null)
        {
            _editorError = _draft.Validate(tenant, snapshot.Permissions, snapshot.PublishedFlows);
            if (_editorError is not null) return;
            if (_preview is null || _preview.ConfigurationRevision != snapshot.Configuration.Revision || _preview.AgentIds.IsEmpty)
            { _editorError = "Preview the current exact targets before saving. Unknown or unsupported targets are shown in the preview."; return; }
        }
        _busy = true;
        try
        {
            MonitoringConfigurationDto? saved = null;
            switch (_editor)
            {
                case "rule":
                    var rule = _draft!.Build(tenant);
                    saved = await Api.SaveRuleAsync(tenant, new(rule, snapshot.Configuration.Revision, reason,
                        _draft.ResetConfirmed ? MonitoringConditionResetPolicy.SuspendOccurrenceAndRequireNewWindow : null), token); break;
                case "group":
                    if (string.IsNullOrWhiteSpace(_groupName) || _groupName.Length > 128 || _groupMembers.Count == 0) throw new HttpRequestException("Enter a group name and select at least one tenant client.");
                    saved = await Api.SaveGroupAsync(tenant, new(new(tenant, _originalGroup?.GroupId ?? Guid.NewGuid(), (_originalGroup?.Revision ?? 0) + 1, _groupName.Trim(), _groupMembers.Order().ToImmutableArray()), snapshot.Configuration.Revision, reason), token); break;
                case "ack": await Api.AcknowledgeAsync(tenant, _operatorSeries!, reason, token); break;
                case "clear": await Api.ClearAsync(tenant, _operatorSeries!, reason, token); break;
                case "delete": saved = await Api.DeleteAsync(tenant, _deleteCollection, _deleteId, new(snapshot.Configuration.Revision, reason), token); break;
                default:
                    if (_bypassMinutes is <= 0 or > 525600) throw new HttpRequestException("Bypass expiry must be 1–525600 minutes or blank.");
                    saved = await Api.SaveBypassAsync(tenant, _bypassId, new(snapshot.Configuration.Revision, reason, _bypassRule, _bypassAgent,
                        string.IsNullOrWhiteSpace(_bypassResource) ? null : _bypassResource.Trim(), _bypassMinutes is { } minutes ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null, _bypassGroup), token); break;
            }
            if (saved is not null && (saved.TenantId != tenant || snapshot.Configuration.Revision == ulong.MaxValue || saved.Revision != snapshot.Configuration.Revision + 1))
                throw new HttpRequestException("The saved configuration could not be verified. Your edits are retained; refresh to check the result before retrying.");
            if (State.IsCurrent(tenant, scope) && editor == _editorGeneration)
            {
                _pendingWatchAtSaveTenant = tenant;
                _pendingWatchAtSaveRevision = saved?.WatchPolicyUpdatePending == true ? saved.Revision : null;
                _busy = false; CloseEditor(); await State.RefreshAsync();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException)
        { if (State.IsCurrent(tenant, scope) && editor == _editorGeneration) _editorError = error is HttpRequestException ? error.Message : "Could not confirm the save. Your edits are retained; refresh before retrying."; }
        finally { _busy = false; }
    }
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); _previewRead?.Cancel(); _previewRead?.Dispose(); _inventoryRead?.Cancel(); _inventoryRead?.Dispose(); _state?.Dispose(); }
}
