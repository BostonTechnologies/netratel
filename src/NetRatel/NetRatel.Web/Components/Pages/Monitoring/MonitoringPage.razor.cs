using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Microsoft.JSInterop;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Web.Services.Monitoring;

namespace NetRatel.Web.Components.Pages.Monitoring;

public partial class MonitoringPage
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "tenantId")] public int? RequestedTenantId { get; set; }
    [SupplyParameterFromQuery(Name = "clientId")] public Guid? RequestedClientId { get; set; }
    [SupplyParameterFromQuery(Name = "state")] public string? RequestedState { get; set; }
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;
    private int? _appliedQueryTenant;
    private Guid? _appliedQueryClient;
    private string? _appliedQueryState;
    private string _stateFilter = "";
    private bool _conflict, _reviewedConflict;
    private string? _reviewDescription;
    private ulong _editorConfigurationRevision;
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
    private string? _startupError, _editor, _editorError, _invalidField, _successMessage;
    private bool _focusInvalid;
    private ElementReference _editorRoot;
    private void IgnoreFormSubmit() { }
    private Dictionary<string, object> FieldAttributes(string field) => new()
    {
        ["data-monitoring-field"] = field,
        ["aria-invalid"] = _invalidField == field ? "true" : "false",
        ["aria-describedby"] = _invalidField == field ? $"monitoring-field-error-{field}" : ""
    };
    private RenderFragment FieldFeedback(string field) => builder =>
    {
        if (_invalidField != field || _editorError is null) return;
        builder.OpenElement(0, "small");
        builder.AddAttribute(1, "id", $"monitoring-field-error-{field}");
        builder.AddAttribute(2, "class", "monitoring-field-error");
        builder.AddContent(3, _editorError);
        builder.CloseElement();
    };
    private string _tab = "Active", _search = "", _reason = "", _groupName = "", _deleteName = "", _deleteCollection = "";
    private bool _interactive, _busy, _formDirty;
    private MonitoringRuleDraft? _draft;
    private MonitoringGroupDto? _originalGroup;
    private HashSet<Guid> _groupMembers = [];
    private MonitoringSeriesState? _operatorSeries;
    private Guid _deleteId, _bypassId;
    private Guid? _bypassRule, _bypassGroup, _bypassAgent, _inventoryAgent;
    private string _bypassResource = "";
    private int? _bypassMinutes = 60;
    private long _editorGeneration, _inventoryGeneration;
    private ulong? _pendingWatchAtSaveRevision;
    private int _pendingWatchAtSaveTenant;
    private CancellationTokenSource? _inventoryRead;
    private string _inventoryNote = "Suggestions read cached inventory only. No collection is requested.";
    private IReadOnlyList<ClientServiceObservation> _serviceSuggestions = [];
    private static readonly ClientServiceState[] ExpectedStateOptions = [ClientServiceState.Running, ClientServiceState.Stopped, ClientServiceState.Failed, ClientServiceState.Starting, ClientServiceState.Stopping, ClientServiceState.Paused];
    private string TenantName => _tenants.FirstOrDefault(tenant => tenant.TenantId == State.TenantId)?.Name ?? "Tenant";
    private string SubmitText => _editor switch { "ack" => "Acknowledge", "clear" => "Clear alert", "delete" => "Remove", "bypass" => "Apply bypass", _ => "Save changes" };
    private string EditorTitle => _editor switch { "rule" => _draft?.Original is null ? "New monitoring rule" : $"Edit {_draft!.Name}", "group" => _originalGroup is null ? "New client group" : $"Edit {_groupName}", "ack" => "Acknowledge occurrence", "clear" => "Clear occurrence", "delete" => "Remove configuration", _ => "Bypass" };
    private IEnumerable<MonitoringSeriesState> ActiveRows => State.Snapshot?.Series.Items.Where(row =>
        (row.Occurrence is { EndedAtUtc: null } || row.Phase == MonitoringPhase.Pending || Health(row) == "Unknown") &&
        (RequestedClientId is null || row.Series.AgentId == RequestedClientId) && MatchesState(row) &&
        (Contains(RuleName(row.Series.RuleId)) || Contains(ClientName(row.Series.AgentId)) || Contains(ClientContext(row.Series.AgentId)) || Contains(row.Series.ResourceKey))) ?? [];

    protected override async Task OnInitializedAsync()
    {
        try { _tenants = await Api.GetTenantsAsync(_lifetime.Token); if (_tenants.Count > 0) { State.ClientFilter = RequestedClientId; _stateFilter = RequestedState ?? ""; await State.SelectTenantAsync(_tenants.Any(tenant => tenant.TenantId == RequestedTenantId) ? RequestedTenantId!.Value : _tenants[0].TenantId); RememberQuery(); } }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException) { _startupError = "Could not load authorized monitoring tenants. Refresh to try again."; }
    }
    protected override async Task OnParametersSetAsync() { await ApplyRequestedScope(); }
    private void RememberQuery()
    {
        _appliedQueryTenant = RequestedTenantId;
        _appliedQueryClient = RequestedClientId;
        _appliedQueryState = RequestedState;
    }
    private async Task<bool> ApplyRequestedScope()
    {
        if (_busy || _tenants.Count == 0 ||
            (_appliedQueryTenant == RequestedTenantId && _appliedQueryClient == RequestedClientId && _appliedQueryState == RequestedState)) return false;
        var tenant = _tenants.Any(item => item.TenantId == RequestedTenantId) ? RequestedTenantId!.Value : State.TenantId;
        var stateChanged = _appliedQueryState != RequestedState;
        RememberQuery();
        CloseEditor(); _search = "";
        if (stateChanged) _stateFilter = RequestedState ?? "";
        State.ClientFilter = RequestedClientId;
        if (State.TenantId != tenant) _pendingWatchAtSaveRevision = null;
        await State.SelectTenantAsync(tenant);
        return true;
    }
    private void ClearClientFilter()
    {
        var query = $"/monitoring?tenantId={State.TenantId}";
        if (!string.IsNullOrWhiteSpace(_stateFilter)) query += "&state=" + Uri.EscapeDataString(_stateFilter);
        Navigation.NavigateTo(query, replace: true);
    }
    protected override void OnAfterRender(bool firstRender) { if (firstRender) { _interactive = true; StateHasChanged(); } }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/operator-time.js");
                var name = await module.InvokeAsync<string>("getTimeZone");
                if (!string.IsNullOrWhiteSpace(name)) _timeZone = TimeZoneInfo.FindSystemTimeZoneById(name);
                StateHasChanged();
            }
            catch (Exception error) when (error is JSException or JSDisconnectedException or TimeZoneNotFoundException or InvalidTimeZoneException) { _timeZone = TimeZoneInfo.Utc; }
        }
        if (await ApplyRequestedScope()) { StateHasChanged(); return; }
        if (_focusInvalid && _editor is not null)
        {
            _focusInvalid = false;
            try
            {
                await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/monitoring-editor.js");
                await module.InvokeVoidAsync("focusInvalidField", _editorRoot);
            }
            catch (Exception error) when (error is JSException or JSDisconnectedException) { }
        }
        if (_restoreEditorFocus is not { } launcher) return;
        _restoreEditorFocus = null;
        if (_lifetime.IsCancellationRequested || _editor is not null || _tab != "Manage" ||
            _focusCloseGeneration != _editorGeneration || State.TenantId != _editorLauncherTenant ||
            Navigation.Uri != _editorLauncherUri || State.Snapshot?.Permissions.CanManage != true) return;
        // The close render has removed the editor and its trap; return to its actual launcher.
        try { await launcher.FocusAsync(); }
        catch (JSDisconnectedException) { return; }
    }
    private async Task ChangeTenant(ChangeEventArgs args)
    {
        if (_editor is not null || !int.TryParse(args.Value?.ToString(), out var tenant) || !_tenants.Any(t => t.TenantId == tenant)) return;
        CloseEditor(); _search = ""; _stateFilter = ""; RequestedTenantId = tenant; RequestedClientId = null; RequestedState = null;
        RememberQuery(); State.ClientFilter = null; _pendingWatchAtSaveRevision = null; _successMessage = null; await State.SelectTenantAsync(tenant);
        Navigation.NavigateTo($"/monitoring?tenantId={tenant}", replace: true);
    }
    private Task Refresh() => State.RefreshAsync();
    private Task PageSeries(string? cursor) => State.NextSeriesPageAsync(cursor);
    private Task PageHistory(string? cursor) => State.NextHistoryPageAsync(cursor);
    private bool Contains(string? text) => string.IsNullOrWhiteSpace(_search) || text?.Contains(_search, StringComparison.OrdinalIgnoreCase) == true;
    private sealed record HistoryRow(DateTimeOffset AtUtc, MonitoringEventIntent? Event = null, MonitoringHistoryAuditDto? Audit = null);
    private IEnumerable<HistoryRow> HistoryRows => State.Snapshot is { } snapshot
        ? snapshot.History.Items.Where(MatchesHistory).Select(item => new HistoryRow(item.AtUtc, Event: item))
            .Concat((snapshot.History.Audits.IsDefault ? [] : snapshot.History.Audits)
                .Where(audit => Contains(audit.EntityName) || Contains(audit.Reason) || Contains(audit.OperatorDisplayName) || Contains(ActionName(audit.Action)))
                .Select(audit => new HistoryRow(audit.AtUtc, Audit: audit))).OrderByDescending(row => row.AtUtc)
        : [];
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
    private string RuleName(Guid id) => State.Snapshot?.Configuration.Rules.FirstOrDefault(r => r.RuleId == id)?.Name ?? "Removed rule";
    private string ClientName(Guid id) => ClientIdentityPresentation.Name(State.ClientIdentity(id));
    private string ClientContext(Guid id) => ClientIdentityPresentation.Context(State.ClientIdentity(id));
    private string Time(DateTimeOffset? at) => at is { } value ? TimeZoneInfo.ConvertTime(value, _timeZone).ToString("yyyy-MM-dd HH:mm:ss zzz") : "—";
    private bool MatchesState(MonitoringSeriesState row) => _stateFilter.ToLowerInvariant() switch
    {
        "firing" => row.Occurrence is { EndedAtUtc: null } && row.Phase == MonitoringPhase.Firing,
        "pending" => row.Phase == MonitoringPhase.Pending,
        "unknown" => Health(row) == "Unknown",
        "suppressed" => row.Suppressed,
        _ => true
    };
    private void FilterState(string filter) { _stateFilter = _stateFilter == filter ? "" : filter; _tab = "Active"; }
    private static string HistoryActor(MonitoringEventIntent item) => item.OperatorDisplayName ??
        (item.Kind is MonitoringEventKind.AlertCleared or MonitoringEventKind.AlertAcknowledged ? "Recorded operator unavailable" : "System");
    private static string ActionName(string action) => action.Replace('_', '-') switch { "rule-upsert" => "Rule changed", "group-upsert" => "Group changed", "bypass-upsert" => "Bypass applied", "rule-delete" => "Rule removed", "group-delete" => "Group removed", "bypass-delete" => "Bypass revoked", "save-rule" => "Rule changed", "save-group" => "Group changed", "save-bypass" => "Bypass applied", "delete-rule" => "Rule removed", "delete-group" => "Group removed", "delete-bypass" => "Bypass revoked", "ack" => "Acknowledged", "clear" => "Alert cleared", _ => action.Replace('_', ' ').Replace('-', ' ') };
    private static string Label(Enum? value) => value switch
    {
        MonitoringEventKind.AlertRaised => "Alert raised", MonitoringEventKind.AlertResolved => "Recovered", MonitoringEventKind.AlertCleared => "Alert cleared", MonitoringEventKind.AlertSuspended => "Alert suspended", MonitoringEventKind.AlertAcknowledged => "Acknowledged",
        MonitoringPhase.NotApplicable => "Not applicable",
        MonitoringFlowDispatchDisposition.NoFlowSelected => "Display only", MonitoringFlowDispatchDisposition.DeliveryUnknown => "Delivery unconfirmed",
        MonitoringClosureDisposition.ManuallyCleared => "Manually cleared", MonitoringClosureDisposition.RuleDisabled => "Rule disabled", MonitoringClosureDisposition.TargetRemoved => "Target removed", MonitoringClosureDisposition.ConfigurationChanged => "Configuration changed",
        MonitoringMetricKind.CpuUsagePercent => "CPU usage", MonitoringMetricKind.DiskFreePercent => "Disk free %", MonitoringMetricKind.DiskFreeSpace => "Disk free space", MonitoringMetricKind.ServiceExpectedState => "Selected service state",
        null => "—", _ => value.ToString()
    };
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
        CloseEditor(); _restoreEditorFocus = null; _conflict = _reviewedConflict = false; _reviewDescription = null; _editorConfigurationRevision = State.Snapshot?.Configuration.Revision ?? 0; _editor = kind; _editorGeneration++; _reason = ""; _formDirty = false; _editorError = null; _invalidField = null; _focusInvalid = false; _successMessage = null;
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
        _inventoryRead?.Cancel(); _editorGeneration++; _inventoryGeneration++;
        _focusCloseGeneration = _editorGeneration;
        _editor = null; _draft = null; _operatorSeries = null; _invalidField = null; _focusInvalid = false;
        _serviceSuggestions = []; _inventoryAgent = null; _inventoryNote = "Suggestions read cached inventory only. No collection is requested.";
    }
    private void HandleKey(KeyboardEventArgs args) { if (args.Key == "Escape" && !_busy) CloseEditor(); }
    private void MarkDirty() => _formDirty = true;
    private void MetricChanged(ChangeEventArgs args) { if (_draft is not null && Enum.TryParse<MonitoringMetricKind>(args.Value?.ToString(), out var kind)) _draft.SelectMetric(kind); }
    private void Toggle<T>(HashSet<T> values, T value, ChangeEventArgs args) { if (args.Value is true) values.Add(value); else values.Remove(value); MarkDirty(); }
    private void SelectionChanged() => MarkDirty();
    private async Task ReviewConflict()
    {
        if (_busy) return;
        var previousOccurrence = _operatorSeries?.Occurrence?.OccurrenceId;
        await State.RefreshAsync();
        if (State.Snapshot is not { } snapshot) return;
        if (_operatorSeries is { } original)
        {
            var current = snapshot.Series.Items.FirstOrDefault(row => row.Series == original.Series);
            if (current?.Occurrence is not { EndedAtUtc: null } occurrence || occurrence.OccurrenceId != previousOccurrence)
            { _editorError = "This occurrence is no longer active. Your reason is retained; close the drawer and review the current alert."; return; }
            _operatorSeries = current;
        }
        _reviewDescription = _draft?.Original is not null
            ? snapshot.Configuration.Rules.FirstOrDefault(rule => rule.RuleId == _draft.RuleId) is { } currentRule
                ? $"Latest saved rule: {currentRule.Name}; {Label(currentRule.Condition.Kind)}; breach {currentRule.Condition.BreachThreshold}, recovery {currentRule.Condition.RecoveryThreshold}; {currentRule.Severity}; {currentRule.Targets.AgentIds.Length} explicit clients and {currentRule.Targets.GroupIds.Length} groups. Your draft below is retained."
                : "This rule was removed. Close the drawer and create a new rule if needed."
            : _originalGroup is not null
                ? snapshot.Configuration.Groups.FirstOrDefault(group => group.GroupId == _originalGroup.GroupId) is { } currentGroup
                    ? $"Latest saved group: {currentGroup.Name}; {currentGroup.AgentIds.Length} members. Your draft below is retained."
                    : "This group was removed. Close the drawer and create a new group if needed."
                : _operatorSeries is { } currentSeries
                    ? $"Current alert: {RuleName(currentSeries.Series.RuleId)} on {ClientName(currentSeries.Series.AgentId)}; {Label(currentSeries.Phase)}; {(currentSeries.Occurrence?.AcknowledgedAtUtc is null ? "Unacknowledged" : "Acknowledged")}."
                    : "The latest saved configuration is loaded. Your draft below is retained.";
        _reviewedConflict = !(_draft?.Original is not null && !snapshot.Configuration.Rules.Any(rule => rule.RuleId == _draft.RuleId)) &&
            !(_originalGroup is not null && !snapshot.Configuration.Groups.Any(group => group.GroupId == _originalGroup.GroupId));
        _editorError = "Fresh state is loaded. Review the current rule or alert before choosing Use reviewed state. Your draft and reason are retained.";
    }
    private void AcceptReviewedState()
    {
        if (!_reviewedConflict || State.Snapshot is null) return;
        if (_draft?.Original is not null)
        {
            var current = State.Snapshot.Configuration.Rules.FirstOrDefault(rule => rule.RuleId == _draft.RuleId);
            if (current is null) return;
            _draft.AdoptReviewedRevision(current);
        }
        if (_originalGroup is not null)
        {
            var current = State.Snapshot.Configuration.Groups.FirstOrDefault(group => group.GroupId == _originalGroup.GroupId);
            if (current is null) return;
            _originalGroup = current;
        }
        _editorConfigurationRevision = State.Snapshot.Configuration.Revision;
        _reviewDescription = null;
        _conflict = _reviewedConflict = false; _editorError = null;
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
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (HttpRequestException)
        { if (State.IsCurrent(tenant, scope) && editor == _editorGeneration && generation == _inventoryGeneration) _inventoryNote = "Inventory access is unavailable or not permitted. Enter the exact stable name manually."; }
    }
    private async Task Save()
    {
        if (_busy || _editor is null || State.Snapshot is not { } snapshot) return;
        var tenant = State.TenantId; var scope = State.Generation; var editor = _editorGeneration; var token = State.Token;
        var reason = _draft?.Reason ?? _reason;
        _editorError = null; _invalidField = null;
        if (_draft is not null)
            _editorError = _draft.Validate(tenant, snapshot.Permissions, snapshot.PublishedFlows, out _invalidField);
        else if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
        { _editorError = "Enter a reason (up to 512 characters)."; _invalidField = nameof(MonitoringRuleDraft.Reason); }
        else if (_editor == "group" && (string.IsNullOrWhiteSpace(_groupName) || _groupName.Length > 128))
        { _editorError = "Enter a group name (up to 128 characters)."; _invalidField = "GroupName"; }
        if (_editorError is not null) { _focusInvalid = _invalidField is not null; return; }
        _busy = true;
        try
        {
            MonitoringConfigurationDto? saved = null;
            switch (_editor)
            {
                case "rule":
                    var rule = _draft!.Build(tenant);
                    saved = await Api.SaveRuleAsync(tenant, new(rule, _editorConfigurationRevision, reason,
                        _draft.ResetConfirmed ? MonitoringConditionResetPolicy.SuspendOccurrenceAndRequireNewWindow : null), token); break;
                case "group":
                    if (string.IsNullOrWhiteSpace(_groupName) || _groupName.Length > 128 || _groupMembers.Count == 0) throw new HttpRequestException("Enter a group name and select at least one tenant client.");
                    saved = await Api.SaveGroupAsync(tenant, new(new(tenant, _originalGroup?.GroupId ?? Guid.NewGuid(), (_originalGroup?.Revision ?? 0) + 1, _groupName.Trim(), _groupMembers.Order().ToImmutableArray()), _editorConfigurationRevision, reason), token); break;
                case "ack": await Api.AcknowledgeOccurrenceAsync(tenant, _operatorSeries!, _editorConfigurationRevision, reason, token); break;
                case "clear": await Api.ClearOccurrenceAsync(tenant, _operatorSeries!, _editorConfigurationRevision, reason, token); break;
                case "delete": saved = await Api.DeleteAsync(tenant, _deleteCollection, _deleteId, new(_editorConfigurationRevision, reason), token); break;
                default:
                    if (_bypassMinutes is <= 0 or > 525600) throw new HttpRequestException("Bypass expiry must be 1–525600 minutes or blank.");
                    saved = await Api.SaveBypassAsync(tenant, _bypassId, new(_editorConfigurationRevision, reason, _bypassRule, _bypassAgent,
                        string.IsNullOrWhiteSpace(_bypassResource) ? null : _bypassResource.Trim(), _bypassMinutes is { } minutes ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null, _bypassGroup), token); break;
            }
            if (saved is not null && (saved.TenantId != tenant || _editorConfigurationRevision == ulong.MaxValue || saved.Revision != _editorConfigurationRevision + 1))
                throw new HttpRequestException("The saved configuration could not be verified. Your edits are retained; refresh to check the result before retrying.");
            if (State.IsCurrent(tenant, scope) && editor == _editorGeneration)
            {
                _pendingWatchAtSaveTenant = tenant;
                _pendingWatchAtSaveRevision = saved?.WatchPolicyUpdatePending == true ? saved.Revision : null;
                _successMessage = $"{EditorTitle} saved successfully.";
                _busy = false; CloseEditor(); await State.RefreshAsync();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (OperationCanceledException)
        { if (State.IsCurrent(tenant, scope) && editor == _editorGeneration) _editorError = "The save timed out. Your edits are retained; refresh to check the result before retrying."; }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException)
        { if (State.IsCurrent(tenant, scope) && editor == _editorGeneration) { _conflict = error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Conflict }; _editorError = error is HttpRequestException ? error.Message : "Could not confirm the save. Your edits are retained; refresh before retrying."; } }
        finally { _busy = false; }
    }
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); _inventoryRead?.Cancel(); _inventoryRead?.Dispose(); _state?.Dispose(); }
}
