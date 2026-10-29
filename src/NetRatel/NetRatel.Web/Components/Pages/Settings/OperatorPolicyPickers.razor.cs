using Microsoft.AspNetCore.Components;
using MudBlazor;
using Microsoft.AspNetCore.WebUtilities;
using NetRatel.Application.Operations;
using NetRatel.Web.Services.OperatorPolicy;

namespace NetRatel.Web.Components.Pages.Settings;

public partial class OperatorPolicies
{
    private McpPolicyDirectory _directory = new([], [], [], [], false);
    private readonly HashSet<Guid> _expandedPolicies = [];
    private HashSet<McpOperatorPolicy> _bulkSelection = [];
    private readonly List<(McpOperatorPolicy Policy, PreparedPolicyChange Change, PolicyMutationPreview Preview)> _bulkPlans = [];
    private readonly List<string> _bulkResults = [];
    private bool _bulkOpen, _bulkConfirmed;
    private MudAutocomplete<string>? _operationPicker, _clientPicker;
    private string _consolidationCredential = "";
    private McpPolicyConsolidationPlan? _consolidationPlan;
    private bool _consolidationConfirmed;
    private Task<IEnumerable<string>> SearchConsolidationCredentials(string search, CancellationToken ct) => Task.FromResult(_credentials.Where(c => Match(c.Name, search)).Select(c => c.Id));
    private string ConsolidationCredentialName(string id) => _credentials.FirstOrDefault(c => c.Id == id)?.Name ?? id;
    private async Task PreviewConsolidationAsync()
    {
        if (_busy || string.IsNullOrEmpty(_consolidationCredential)) return;
        _consolidationConfirmed = false;
        await RunAsync(async () => _consolidationPlan = await PolicyApi.PreviewConsolidationAsync(_consolidationCredential, _lifetime.Token));
    }
    private async Task ConfirmConsolidationAsync()
    {
        if (_busy || !_consolidationConfirmed || _consolidationPlan is null) return;
        await RunAsync(async () => {
            await PolicyApi.ConfirmConsolidationAsync(_consolidationCredential, _consolidationPlan.PlanToken, _lifetime.Token);
            _consolidationPlan = null;
            _policies = (await PolicyApi.ListAsync(_environment, _tenantFilter, _lifetime.Token)).Items;
            _success = "Legacy policies consolidated. Existing restrictions and audit references were preserved.";
        });
    }
    private int BulkAffectedCount => _bulkPlans.Sum(item => 1 + DeleteChildCount(item.Policy));
    private int DeleteChildCount(McpOperatorPolicy policy) => Children(policy).Count(child => child.LifecycleState != McpOperatorPolicyLifecycleState.Deleted);
    private IEnumerable<McpOperatorPolicy> VisibleChildren(McpOperatorPolicy policy) => Children(policy).Where(child => _lifecycleFilter is null || child.LifecycleState == _lifecycleFilter && (_lifecycleFilter != McpOperatorPolicyLifecycleState.Active || child.ExpiresAtUtc is null || child.ExpiresAtUtc > DateTimeOffset.UtcNow));
    private IEnumerable<McpOperatorPolicy> Children(McpOperatorPolicy policy) => _policies.Where(p => p.ParentPolicyId == policy.PolicyId);
    private void ToggleChildren(Guid id) { if (!_expandedPolicies.Add(id)) _expandedPolicies.Remove(id); }
    private string TenantName(int id) => id == 0 ? "Server / instance" : _directory.Tenants.FirstOrDefault(t => t.Id == id)?.Name ?? $"Unavailable tenant (ID {id})";
    private string TenantOptionName(int? id) => id is { } value ? TenantName(value) : "All tenants and server";
    private string PrincipalName(McpOperatorPrincipalSelector selector) => _directory.Principals.FirstOrDefault(p => p.Kind == selector.Kind && p.Id == selector.Value) is { } p
        ? $"{p.Name} · {p.Detail}" : $"Unresolved {selector.Kind}: {selector.Value}";
    private string PrincipalOptionName(string? value) => string.IsNullOrEmpty(value) ? "" : PrincipalName(new(_editor.PrincipalKind, value));
    private string ClientOptionName(string? value) => Guid.TryParse(value, out var id) && _directory.Clients.FirstOrDefault(c => c.Id == id) is { } client
        ? $"{client.Name} · {id.ToString()[..8]}" : string.IsNullOrEmpty(value) ? "" : $"Unresolved client: {value}";
    private string ClientsLabel(McpOperatorTargetSelector selector) => selector.AgentId is { } id ? ClientOptionName(id.ToString()) :
        selector.AgentIds is { Count: > 0 } ids ? string.Join(", ", ids.Select(id => ClientOptionName(id.ToString()))) :
        selector.ClientTag ?? (selector.ClientTags is { } tags ? string.Join(", ", tags) : "All clients in scope");
    private IReadOnlyCollection<string> EditorShells => _editor.Shells.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private IEnumerable<string> KnownShells => McpIntegrationPolicyCatalog.AllAccessLimits.AllowedShells!.Concat(EditorShells).Distinct();

    private async Task LoadDirectoryAsync(string? search = null, int? tenant = null, CancellationToken ct = default)
    {
        var loaded = await PolicyApi.GetDirectoryAsync(search, tenant, ct);
        _directory = loaded with {
            Tenants = _directory.Tenants.Concat(loaded.Tenants).DistinctBy(t => t.Id).ToArray(),
            Clients = _directory.Clients.Concat(loaded.Clients).DistinctBy(c => c.Id).ToArray(),
            Principals = _directory.Principals.Concat(loaded.Principals).DistinctBy(p => (p.Kind, p.Id)).ToArray()
        };
    }
    private async Task<IEnumerable<int?>> SearchTenantsAsync(string search, CancellationToken ct)
    {
        await LoadDirectoryAsync(search, ct: ct);
        return new int?[] { 0 }.Concat(_directory.Tenants.Where(t => Match(t.Name, search)).Select(t => (int?)t.Id));
    }
    private async Task<IEnumerable<string>> SearchPrincipalsAsync(string search, CancellationToken ct)
    {
        try { await LoadDirectoryAsync(search, _editor.TenantId, ct); }
        catch (HttpRequestException) { _error = "Could not load the identity directory. Your existing selection is retained."; throw; }
        return _directory.Principals.Where(p => p.Kind == _editor.PrincipalKind && (Match(p.Name, search) || Match(p.Id, search))).Select(p => p.Id);
    }
    private async Task<IEnumerable<string>> SearchClientsAsync(string search, CancellationToken ct)
    {
        await LoadDirectoryAsync(search, _editor.TenantId, ct);
        return _directory.Clients.Where(c => c.TenantId == _editor.TenantId && Match(c.Name, search)).Select(c => c.Id.ToString());
    }
    private async Task<IEnumerable<string>> SearchTagsAsync(string search, CancellationToken ct)
    {
        await LoadDirectoryAsync(search, _editor.TenantId, ct);
        return _directory.Tags.Where(tag => Match(tag, search));
    }
    private static Task<IEnumerable<string>> SearchAdmissionOperations(string search, CancellationToken ct) => Task.FromResult(
        McpOperatorOperationCatalog.Operations.Select(op => op.ToolName + "/" + op.OperationName).Where(op => Match(op, search)));
    private void SelectAdmissionOperation(string? value) {
        if (string.IsNullOrEmpty(value)) return;
        var separator = value.IndexOf('/'); if (separator <= 0) return;
        var tool = value[..separator]; var operation = value[(separator + 1)..];
        if (McpOperatorOperationCatalog.Find(tool, operation) is null) return;
        _admissionTool = tool; _admissionOperation = operation;
    }
    private Task<IEnumerable<string>> SearchOperationsAsync(string search, CancellationToken ct) => Task.FromResult(
        McpOperatorOperationCatalog.Operations.Where(op => _editor.Families.Contains(op.OperationFamily))
            .Select(op => op.ToolName + "/" + op.OperationName).Where(op => Match(op, search) && !(_editor.Operations?.Contains(op) ?? false)));
    private static bool Match(string value, string? search) => string.IsNullOrWhiteSpace(search) || value.Contains(search, StringComparison.OrdinalIgnoreCase);
    private void PrincipalKindChanged(McpOperatorPrincipalSelectorKind kind) { _editor.PrincipalKind = kind; _editor.PrincipalValue = ""; }
    private async Task EditorTenantChangedAsync(int? id)
    {
        if (_editExisting || _editor.ParentPolicyId is not null) return;
        _editor.TenantId = id ?? 0; _editor.AgentId = ""; _editor.AgentIds = null; _editor.ClientTags = null; _editor.ClientTag = "";
        await LoadDirectoryAsync(tenant: id, ct: _lifetime.Token);
    }
    private async Task AddOperation(string? operation)
    {
        if (string.IsNullOrEmpty(operation)) return;
        var original = _editor.Operations ?? (string.IsNullOrEmpty(_editor.Operation) ? [] : new[] { _editor.Operation });
        _editor.Operations = original.Append(operation).Distinct().Order().ToArray(); _editor.Operation = "";
        if (_operationPicker is not null) await _operationPicker.ClearAsync();
    }
    private void RemoveOperation(string operation) => _editor.Operations = (_editor.Operations ?? []).Where(o => o != operation).ToArray();
    private async Task AddClient(string? value) { if (Guid.TryParse(value, out var id)) { _editor.AgentIds = (_editor.AgentIds ?? []).Append(id).Distinct().ToArray(); if (_clientPicker is not null) await _clientPicker.ClearAsync(); } }
    private void RemoveClient(Guid id) { var ids = (_editor.AgentIds ?? []).Where(c => c != id).ToArray(); _editor.AgentIds = ids.Length == 0 ? null : ids; }

    private async Task NewException()
    {
        if (!CanReplace || _selected is not { ParentPolicyId: null } parent) return;
        _editor = OperatorPolicyDraftEditor.From(parent);
        _editor.ParentPolicyId = parent.PolicyId; _editor.IsContainer = false;
        _editor.Name = parent.Name + " · Exception"; _editor.Effect = McpOperatorPolicyEffect.Deny;
        _editor.Operation = ""; _editor.Operations = []; _editor.AgentIds = null; _editor.ClientTags = null;
        _editExisting = false; _editing = true; ResetPreview();
        await LoadDirectoryAsync(tenant: parent.TargetSelector.TenantId, ct: _lifetime.Token);
    }

    private async Task PrepareBulkDeleteAsync()
    {
        if (_busy || _bulkSelection.Count == 0) return;
        _bulkPlans.Clear(); _bulkResults.Clear(); _bulkConfirmed = false;
        await RunAsync(async () => {
            foreach (var policy in _bulkSelection.Where(p => FilteredPolicies.Any(current => current.PolicyId == p.PolicyId) && p.LifecycleState != McpOperatorPolicyLifecycleState.Deleted)) {
                var change = new PreparedPolicyChange(PolicyChangeAction.Delete, null, policy.PolicyId, policy.Version, policy.TargetSelector);
                _bulkPlans.Add((policy, change, await PolicyApi.PreviewAsync(change, _lifetime.Token)));
            }
            _bulkOpen = _bulkPlans.Count > 0;
        });
    }
    private async Task ConfirmBulkDeleteAsync()
    {
        if (_busy || !_bulkConfirmed || _bulkResults.Count > 0) return;
        await RunAsync(async () => {
            foreach (var item in _bulkPlans) {
                try {
                    await PolicyApi.ConfirmAsync(item.Change, item.Preview, _lifetime.Token);
                    _bulkResults.Add($"{item.Policy.Name}: deleted with child exceptions.");
                    _bulkSelection.Remove(item.Policy);
                } catch (HttpRequestException error) { _bulkResults.Add($"{item.Policy.Name}: not confirmed ({error.StatusCode}); refresh and review before retrying."); }
            }
            _policies = (await PolicyApi.ListAsync(_environment, _tenantFilter, _lifetime.Token)).Items;
            _bulkSelection = _policies.Where(p => p.LifecycleState != McpOperatorPolicyLifecycleState.Deleted && _bulkSelection.Any(old => old.PolicyId == p.PolicyId)).ToHashSet();
        });
    }

    private void PersistFilters() => Navigation.NavigateTo(Navigation.GetUriWithQueryParameters(new Dictionary<string, object?> {
        ["policyState"] = _lifecycleFilter?.ToString() ?? "History", ["policySearch"] = _search.Length == 0 ? null : _search,
        ["policyTenant"] = _tenantFilter, ["policyEnvironment"] = _environment.ToString(), ["policyTarget"] = _targetFilter?.ToString()
    }), replace: true);
    private void RestoreFilters()
    {
        var query = QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query);
        if (query.TryGetValue("policyState", out var state)) _lifecycleFilter = Enum.TryParse<McpOperatorPolicyLifecycleState>(state, out var parsed) ? parsed : McpOperatorPolicyLifecycleState.Active;
        if (state == "History") _lifecycleFilter = null;
        if (query.TryGetValue("policySearch", out var search)) _search = search.ToString()[..Math.Min(200, search.ToString().Length)];
        if (query.TryGetValue("policyTenant", out var tenant) && int.TryParse(tenant, out var tenantId) && tenantId >= 0) _tenantFilter = tenantId;
        if (query.TryGetValue("policyEnvironment", out var environment) && Enum.TryParse<McpOperatorEnvironment>(environment, out var env) && Enum.IsDefined(env)) _environment = env;
        if (query.TryGetValue("policyTarget", out var target) && Enum.TryParse<McpOperatorTargetSelectorKind>(target, out var kind) && Enum.IsDefined(kind)) _targetFilter = kind;
    }
}
