using System.Net;
using System.Text.Json;
using NetRatel.Application.Operations;
using NetRatel.Shared.Connectivity;
using NetRatel.Web.Services.OperatorPolicy;

namespace NetRatel.Web.Components.Pages.Settings;

public partial class OperatorPolicies
{
    private readonly CancellationTokenSource _lifetime = new();
    private PolicyAccountAuthority? _authority;
    private IReadOnlyList<McpOperatorPolicy> _policies = [];
    private IReadOnlyList<PolicyCredentialOption> _credentials = [];
    private McpOperatorEnvironment _environment;
    private int? _tenantFilter;
    private McpOperatorPolicy? _selected;
    private McpOperatorTargetProfile? _targetProfile;
    private IReadOnlyList<McpOperatorPolicyChangeAudit> _audits = [];
    private OperatorPolicyDraftEditor _editor = new();
    private PreparedPolicyChange? _prepared;
    private PolicyMutationPreview? _preview;
    private CredentialMcpAdmission? _credentialAdmission;
    private string _credentialId = "";
    private string _admissionTool = "netratel_job_runs";
    private string _admissionOperation = "start";
    private bool _loading = true;
    private bool _busy;
    private bool _editing;
    private bool _editExisting;
    private bool _confirmed;
    private bool _disposed;
    private string? _error;
    private string? _success;
    private string? _readbackWarning;
    private AcceptedPolicyChange? _accepted;

    private bool CanReplace => _selected is { LifecycleState: McpOperatorPolicyLifecycleState.Active, DisabledAtUtc: null } &&
        _selected.Environment == _authority?.Environment;
    private bool CanDisable => CanReplace;
    private bool CanRevoke => _selected is { LifecycleState: not McpOperatorPolicyLifecycleState.Revoked } &&
        _selected.Environment == _authority?.Environment;

    protected override async Task OnInitializedAsync()
    {
        await RunAsync(async () =>
        {
            _authority = await PolicyApi.GetAuthorityAsync(_lifetime.Token);
            _environment = _authority.Environment;
            _policies = (await PolicyApi.ListAsync(_environment, null, _lifetime.Token)).Items;
            _credentials = await PolicyApi.GetOwnedCredentialsAsync(_lifetime.Token);
        });
        _loading = false;
    }

    private async Task EnvironmentChangedAsync(McpOperatorEnvironment value)
    {
        if (_busy) return;
        _environment = value;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_busy || _authority is null) return;
        CancelEdit();
        ClearAcceptedReceipt();
        _selected = null;
        _targetProfile = null;
        _audits = [];
        await RunAsync(async () => _policies = (await PolicyApi.ListAsync(_environment, _tenantFilter, _lifetime.Token)).Items);
    }

    private async Task SelectAsync(Guid policyId)
    {
        if (_busy || _authority is null) return;
        CancelEdit();
        ClearAcceptedReceipt();
        _selected = null;
        _targetProfile = null;
        _audits = [];
        await RunAsync(() => ReadSelectionAsync(policyId));
    }

    private async Task ReadSelectionAsync(Guid policyId)
    {
        var policy = await PolicyApi.GetAsync(policyId, _lifetime.Token);
        var audits = await PolicyApi.GetAuditsAsync(policyId, _lifetime.Token);
        var profile = policy.TargetSelector.AgentId is { } agentId
            ? await PolicyApi.GetTargetAsync(policy.TargetSelector.TenantId, agentId, _lifetime.Token) : null;
        if (_disposed) return;
        _selected = policy;
        _credentialAdmission = null;
        _credentialId = policy.PrincipalSelector.Kind == McpOperatorPrincipalSelectorKind.OAuthClientId &&
            _credentials.Any(credential => credential.Id == policy.PrincipalSelector.Value) ? policy.PrincipalSelector.Value : "";
        _audits = audits;
        _targetProfile = profile;
    }

    private void NewPolicy()
    {
        if (_busy || _authority is null || _environment != _authority.Environment) return;
        ResetPreview();
        ClearAcceptedReceipt();
        _error = null;
        _editExisting = false;
        _editing = true;
        _editor = new OperatorPolicyDraftEditor
        {
            Environment = _environment, TenantId = _tenantFilter ?? 0,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            Constraints = new(MaxCommandDurationSeconds: 60, MaxConcurrentCommands: 1, MaxOutputBytes: 131072,
                MaxFanOut: 1, DestructiveOperationsAllowed: false, AllowActiveTenantDeletion: false)
        };
    }

    private void EditPolicy()
    {
        if (_busy || !CanReplace || _selected is null) return;
        ResetPreview();
        ClearAcceptedReceipt();
        _error = null;
        _editExisting = _editing = true;
        _editor = OperatorPolicyDraftEditor.From(_selected);
    }

    private async Task PreviewDraftAsync()
    {
        if (_busy || !_editing || _authority is null) return;
        await RunAsync(async () =>
        {
            var draft = _editor.Build();
            var change = new PreparedPolicyChange(_editExisting ? PolicyChangeAction.Replace : PolicyChangeAction.Create,
                draft, _editExisting ? _selected?.PolicyId : null, _editExisting ? _selected?.Version : null, draft.TargetSelector);
            await PrepareAsync(change);
        });
    }

    private async Task PreviewStateAsync(PolicyChangeAction action)
    {
        if (_busy || _authority is null || _selected is null ||
            (action == PolicyChangeAction.Disable ? !CanDisable : action != PolicyChangeAction.Revoke || !CanRevoke)) return;
        _editing = false;
        ResetPreview();
        var selected = _selected;
        await RunAsync(() => PrepareAsync(new(action, null, selected.PolicyId, selected.Version, selected.TargetSelector)));
    }

    private async Task PrepareAsync(PreparedPolicyChange change)
    {
        var preview = await PolicyApi.PreviewAsync(change, _lifetime.Token);
        if (_disposed) return;
        _prepared = change;
        _preview = preview;
        _confirmed = false;
        ClearAcceptedReceipt();
    }

    private async Task CheckAdmissionAsync()
    {
        if (_busy || _authority is null || _selected is not { } selected || !_credentials.Any(credential => credential.Id == _credentialId) ||
            selected.Environment != _authority.Environment ||
            selected.TargetSelector.Kind is not (McpOperatorTargetSelectorKind.ExactAgent or McpOperatorTargetSelectorKind.Tenant or McpOperatorTargetSelectorKind.ControlPlane)) return;
        _credentialAdmission = null;
        await RunAsync(async () =>
        {
            var target = new ReviewedMcpAccessTarget(selected.TargetSelector.TenantId, _admissionTool.Trim(), _admissionOperation.Trim(),
                selected.TargetSelector.AgentId, _targetProfile?.Version);
            var admission = await PolicyApi.CheckAdmissionAsync(_credentialId.Trim(), target, _lifetime.Token);
            if (!_disposed) _credentialAdmission = admission;
        });
    }

    private async Task ConfirmAsync()
    {
        if (_busy || _authority is null || !_confirmed || _prepared is not { } change || _preview is not { } preview) return;
        AcceptedPolicyChange? accepted = null;
        await RunAsync(async () =>
        {
            var result = await PolicyApi.ConfirmAsync(change, preview, _lifetime.Token);
            if (_disposed) return;
            ResetPreview();
            _editing = false;
            accepted = _accepted = new(change.Action, result);
            _success = ReceiptDescription(accepted);
        }, confirming: true);
        if (accepted is not null && !_disposed) await RefreshAcceptedAsync();
    }

    private async Task RefreshAcceptedAsync()
    {
        if (_busy || _authority is null || _accepted is not { } accepted) return;
        _readbackWarning = null;
        _success = ReceiptDescription(accepted);
        await RunAsync(async () =>
        {
            await ReadSelectionAsync(accepted.Result.Policy.PolicyId);
            if (_disposed) return;
            _policies = (await PolicyApi.ListAsync(_environment, _tenantFilter, _lifetime.Token)).Items;
            var matchesReceipt = _selected!.Version == accepted.Result.Policy.Version;
            var hasAcceptedAudit = _audits.Any(audit => audit.PolicyId == accepted.Result.Policy.PolicyId &&
                audit.Version == accepted.Result.Policy.Version && audit.Action == ExpectedAuditAction(accepted.Action));
            _success = ReceiptDescription(accepted);
            if (!matchesReceipt)
                _readbackWarning = $"Current authoritative readback is {_selected.LifecycleState}, version {_selected.Version}; it differs from accepted version {accepted.Result.Policy.Version}. Review this state before making another change.";
            else if (hasAcceptedAudit)
                _success += " Authoritative policy readback and retained change audit match this accepted version.";
            if (!hasAcceptedAudit)
                _readbackWarning = (_readbackWarning is null ? "" : _readbackWarning + " ") +
                    $"The retained change audit for accepted version {accepted.Result.Policy.Version} is not present in this readback. Refresh the accepted receipt to check again.";
        }, acceptedReadback: true);
    }

    private async Task RunAsync(Func<Task> action, bool confirming = false, bool acceptedReadback = false)
    {
        if (_disposed || _busy) return;
        _busy = true;
        _error = null;
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { _error = null; }
        catch (OperationCanceledException)
        {
            _error = confirming ? "Confirmation timed out. Retry this same reviewed confirmation to recover its durable result."
                : "The policy request timed out. Retry this request.";
        }
        catch (JsonException) { _error = "The policy service returned an unexpected response. Refresh the policy to inspect its current state."; }
        catch (PolicyApiException exception)
        {
            _error = Explain(exception);
            if (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // A target-policy denial need not remove valid administration access.
                if (exception.Code == "operator_policy_request_failed") _authority = null;
                ResetPreview();
            }
            else if (confirming && exception.Code == "idempotency_pending")
            {
                _error = "The reviewed confirmation is still pending. Retry the same confirmation to recover its durable result.";
            }
            else if (confirming && exception.StatusCode == HttpStatusCode.Conflict)
            {
                ResetPreview();
                _error += " Refresh the policy and obtain a new preview.";
            }
        }
        catch (FormatException) { _error = "Enter a valid exact client ID and date with an explicit UTC offset or Z."; }
        catch (HttpRequestException)
        {
            _error = confirming
                ? "Confirmation response was interrupted. Retry the same reviewed confirmation to recover its idempotent receipt, or inspect the policy before making another change."
                : "The policy service could not be reached. Retry this request.";
        }
        finally
        {
            if (acceptedReadback && _error is not null && _accepted is { } accepted)
                _error = $"The change was accepted at version {accepted.Result.Policy.Version}, but policy or audit readback is incomplete. Refresh the accepted receipt to retry readback. {_error}";
            _busy = false;
        }
    }

    private static string Explain(PolicyApiException exception) => exception.Code switch
    {
        "operator_policy_self_escalation" => "A policy administrator cannot use this change to alter their own authority. Select a separate principal.",
        "target_policy_missing" or "tenant_not_authorized" or "target_operation_not_authorized" => "The signed-in administrator needs an existing bounded parent policy for this target and lifecycle operation.",
        "operator_policy_version_conflict" or "confirmation_plan_stale" => "The policy or its authorizing target facts changed after review.",
        "confirmation_plan_expired" => "This confirmation preview expired.",
        "target_profile_missing" => "Create the target's trusted profile before changing its operator policy.",
        "validation_error" => "Review the selectors, bounds and expiry. The server rejected this policy draft.",
        _ => $"The server rejected the request ({exception.Code}, HTTP {(int?)exception.StatusCode})."
    };

    private void DiscardPreview() { if (!_busy) ResetPreview(); }
    private void CancelEdit()
    {
        if (_busy) return;
        ResetPreview();
        _editing = false;
        _editor = new();
    }
    private void ResetPreview() { _preview = null; _prepared = null; _confirmed = false; }
    private void ClearAcceptedReceipt() { _accepted = null; _success = null; _readbackWarning = null; }
    private static string ReceiptDescription(AcceptedPolicyChange accepted) =>
        $"{accepted.Action} accepted{(accepted.Result.Replay ? " (original idempotent receipt)" : "")} at version {accepted.Result.Policy.Version} ({accepted.Result.Policy.LifecycleState}). Correlation: {accepted.Result.CorrelationId}.";
    private static string ExpectedAuditAction(PolicyChangeAction action) => action switch
    {
        PolicyChangeAction.Create => "policy_created", PolicyChangeAction.Replace => "policy_replaced",
        PolicyChangeAction.Disable => "policy_disabled", PolicyChangeAction.Revoke => "policy_revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    private sealed record AcceptedPolicyChange(PolicyChangeAction Action, PolicyMutationResult Result);
    private static McpOperatorPolicyDraft ToDraft(McpOperatorPolicy policy) => new(policy.Name, policy.Environment, policy.Effect,
        policy.Priority, policy.PrincipalSelector, policy.TargetSelector, policy.TargetClassification, policy.OperationFamily,
        policy.Operation, policy.Constraints, policy.ExpiresAtUtc, policy.ReviewByUtc, policy.AuditReference);
    private static string StateChangeDescription(PolicyChangeAction action) => action switch
    {
        PolicyChangeAction.Disable => "This makes the selected policy inactive and retains its history. A later grant requires a separately reviewed successor.",
        PolicyChangeAction.Revoke => "Revocation is terminal. This policy cannot be reactivated; history remains available.",
        _ => "The confirmed change must match this preview exactly. Current credential grants and other matching policies still constrain admission."
    };

    public void Dispose()
    {
        _disposed = true;
        ResetPreview();
        _editor = new();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
