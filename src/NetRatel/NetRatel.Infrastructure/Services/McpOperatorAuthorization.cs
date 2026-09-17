using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Database-backed, default-deny operator-policy evaluator. It deliberately
/// accepts only trusted caller/target facts supplied by a transport adapter;
/// it neither infers authority from capability presence nor consults a host
/// name. Database policy rows, not process-local state, are the concurrency
/// authority for accepted operations.
/// </summary>
public sealed class McpOperatorAuthorization(OrchestratorDbContext db) : IMcpOperatorAuthorization
{
    private const int MaxIdentityLength = 256;
    private readonly OrchestratorDbContext _db = db;

    public async Task<bool> HasTenantVisibilityAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        int tenantId,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(environment) || tenantId < 0 ||
            !IsSafeToken(principal.Subject, MaxIdentityLength) ||
            (principal.ClientId is not null && !IsSafeToken(principal.ClientId, MaxIdentityLength)))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var candidates = await _db.McpOperatorPolicies.AsNoTracking()
            .Where(policy =>
                policy.Environment == environment &&
                ((policy.TenantId == tenantId &&
                  (tenantId != 0 || policy.TargetSelectorKind == McpOperatorTargetSelectorKind.ControlPlane)) ||
                 (environment == McpOperatorEnvironment.Development && policy.TenantId == 0 &&
                  policy.TargetSelectorKind == McpOperatorTargetSelectorKind.DevelopmentEnvironment)) &&
                policy.LifecycleState == McpOperatorPolicyLifecycleState.Active &&
                policy.DisabledAtUtc == null &&
                (policy.ExpiresAtUtc == null || policy.ExpiresAtUtc > now))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return candidates.Any(policy => PrincipalMatches(policy, principal) &&
            (policy.TargetSelectorKind != McpOperatorTargetSelectorKind.DevelopmentEnvironment || IsDevelopmentPolicy(policy)));
    }

    public async Task<McpOperatorDecision> EvaluateAsync(
        McpOperatorAccessRequest request,
        CancellationToken cancellationToken)
    {
        var prerequisiteFailure = EvaluatePrerequisites(request);
        if (prerequisiteFailure is not null) return prerequisiteFailure;

        var candidates = await _db.McpOperatorPolicies.AsNoTracking()
            .Where(policy => policy.Environment == request.Environment &&
                (policy.TenantId == request.TenantId ||
                 (request.Environment == McpOperatorEnvironment.Development && policy.TenantId == 0 &&
                  policy.TargetSelectorKind == McpOperatorTargetSelectorKind.DevelopmentEnvironment)) &&
                policy.LifecycleState == McpOperatorPolicyLifecycleState.Active && policy.DisabledAtUtc == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return EvaluateSnapshot(request, candidates, DateTimeOffset.UtcNow);
    }

    private static McpOperatorDecision? EvaluatePrerequisites(McpOperatorAccessRequest request)
    {
        if (!IsValidRequest(request))
            return McpOperatorDecision.Denied(request, "delegated_identity_invalid", McpOperatorAuthorizationLayer.Delegation);

        if (!request.RequiredScopes.IsSubsetOf(request.Principal.Scopes))
            return McpOperatorDecision.Denied(request, "oauth_scope_missing", McpOperatorAuthorizationLayer.OAuthScope);

        if (!McpOperationRoleRequirements.IsSatisfied(request.Tool, request.Operation, request.Principal.Roles))
            return McpOperatorDecision.Denied(request, "oauth_role_missing", McpOperatorAuthorizationLayer.OAuthScope);

        if (!request.TenantVisible)
            return McpOperatorDecision.Denied(request, "tenant_not_authorized", McpOperatorAuthorizationLayer.Tenant);

        if (!request.TargetResolved)
            return McpOperatorDecision.Denied(request, "target_not_found", McpOperatorAuthorizationLayer.Target);

        if (!request.TargetEnabled && !AllowsDisabledClientAdministration(request))
            return McpOperatorDecision.Denied(request, "target_disabled", McpOperatorAuthorizationLayer.Target);

        return null;
    }

    // Disabling remote execution must leave the reviewed administration path
    // available to inspect, re-enable, or decommission the installation, and
    // to replay an already accepted disable confirmation.
    // The actual disabled fact remains in the request and all scope, role,
    // visibility, policy, confirmation, and audit checks still apply.
    private static bool AllowsDisabledClientAdministration(McpOperatorAccessRequest request) =>
        request.Tool == "netratel_clients" && request.Operation is
            "netratel_clients/get" or "netratel_clients/presence" or
            "netratel_clients/capabilities" or "netratel_clients/binding" or
            "netratel_clients/update_metadata" or "netratel_clients/update_attempts" or
            "netratel_clients/preview_disable" or "netratel_clients/disable" or
            "netratel_clients/preview_enable" or "netratel_clients/enable" or
            "netratel_clients/preview_delete" or "netratel_clients/delete";

    // Shared by exact dispatch evaluation and bounded read-only search batches.
    internal static McpOperatorDecision EvaluateSnapshot(
        McpOperatorAccessRequest request,
        IEnumerable<McpOperatorPolicyRecord> policies,
        DateTimeOffset now)
    {
        var prerequisiteFailure = EvaluatePrerequisites(request);
        if (prerequisiteFailure is not null) return prerequisiteFailure;
        var candidates = policies.Where(policy => policy.Environment == request.Environment &&
            (policy.TenantId == request.TenantId || IsDevelopmentPolicy(policy)) && policy.LifecycleState == McpOperatorPolicyLifecycleState.Active &&
            policy.DisabledAtUtc == null);

        var principalMatches = candidates
            .Where(policy => PrincipalMatches(policy, request.Principal))
            .ToArray();
        var targetSelectorMatches = principalMatches
            .Where(policy => TargetSelectorMatches(policy, request))
            .ToArray();
        var targetMatches = targetSelectorMatches
            .Where(policy => ClassificationMatches(policy, request))
            .ToArray();
        var matching = targetMatches
            .Where(policy => OperationMatches(policy, request))
            .ToArray();

        if (matching.Length == 0)
        {
            if (targetSelectorMatches.Length > 0 && targetMatches.Length == 0)
                return McpOperatorDecision.Denied(request, "target_classification_denied", McpOperatorAuthorizationLayer.Policy);
            if (targetMatches.Length > 0)
                return McpOperatorDecision.Denied(request, "target_operation_not_authorized", McpOperatorAuthorizationLayer.Policy);
        }

        var active = matching
            .Where(policy => policy.ExpiresAtUtc is null || policy.ExpiresAtUtc > now)
            .ToArray();

        if (matching.Length > 0 && active.Length == 0)
        {
            return McpOperatorDecision.Denied(
                request,
                "target_policy_expired",
                McpOperatorAuthorizationLayer.Policy,
                matching.Select(policy => policy.Id).ToArray());
        }

        if (active.Length == 0)
            return McpOperatorDecision.Denied(request, "target_policy_missing", McpOperatorAuthorizationLayer.Policy);

        var denies = active.Where(policy => policy.Effect == McpOperatorPolicyEffect.Deny).ToArray();
        if (denies.Length > 0)
        {
            var code = denies.Any(policy => policy.TargetClassification is not null)
                ? "target_classification_denied"
                : "target_policy_denied";
            return McpOperatorDecision.Denied(
                request,
                code,
                McpOperatorAuthorizationLayer.Policy,
                denies.Select(policy => policy.Id).ToArray());
        }

        var selected = active
            .Where(policy => policy.Effect == McpOperatorPolicyEffect.Allow)
            .OrderByDescending(policy => policy.Priority)
            .ThenBy(policy => policy.Id)
            .FirstOrDefault();
        if (selected is null)
            return McpOperatorDecision.Denied(request, "target_policy_missing", McpOperatorAuthorizationLayer.Policy);

        var constraints = ReadConstraints(selected.ConstraintsJson);
        if (constraints is null)
        {
            return McpOperatorDecision.Denied(
                request,
                "target_policy_denied",
                McpOperatorAuthorizationLayer.Policy,
                [selected.Id]);
        }

        if (constraints.AllowedTargetClassifications is { Count: > 0 } allowedClassifications &&
            (request.TargetClassification is null || !allowedClassifications.Contains(request.TargetClassification.Value)))
        {
            return McpOperatorDecision.Denied(
                request,
                "target_classification_denied",
                McpOperatorAuthorizationLayer.Constraint,
                [selected.Id]);
        }

        if (constraints.RequiredConfirmationClass is { } requiredConfirmation &&
            request.ConfirmationClass != requiredConfirmation)
        {
            return McpOperatorDecision.Denied(
                request,
                "confirmation_required",
                McpOperatorAuthorizationLayer.Confirmation,
                [selected.Id]);
        }

        if (constraints.DestructiveOperationsAllowed is false &&
            request.ConfirmationClass == McpOperatorConfirmationClass.Destructive)
        {
            return McpOperatorDecision.Denied(
                request,
                "target_operation_not_authorized",
                McpOperatorAuthorizationLayer.Constraint,
                [selected.Id]);
        }

        if (!request.CapabilityAvailable)
            return McpOperatorDecision.Denied(request, "capability_unavailable", McpOperatorAuthorizationLayer.Capability, [selected.Id]);

        if (!request.TargetOnline)
            return McpOperatorDecision.Denied(request, "target_offline", McpOperatorAuthorizationLayer.Target, [selected.Id]);

        return new McpOperatorDecision(true, null, null, [selected.Id], constraints, request.TargetSetDigest, request, selected.Version, IsDevelopmentPolicy(selected));
    }

    public async Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(
        McpOperatorDecision decision,
        string servicePrincipal,
        CancellationToken cancellationToken)
    {
        if (!decision.IsAllowed || decision.MatchingPolicyIds.Count != 1 || !IsSafeToken(servicePrincipal, MaxIdentityLength))
            throw new InvalidOperationException("Only an allowed operator decision with a trusted MCP service principal can be accepted.");

        var current = await EvaluateAsync(decision.Request, cancellationToken).ConfigureAwait(false);
        if (!current.IsAllowed || current.MatchingPolicyIds.Count != 1)
            throw new McpOperatorAdmissionRejectedException(current.FailureCode ?? "target_policy_missing");

        var audit = new McpOperatorAcceptedAuditRecord
        {
            Id = Guid.NewGuid(),
            PolicyId = current.MatchingPolicyIds[0],
            Environment = current.Request.Environment,
            ServicePrincipal = servicePrincipal,
            Subject = current.Request.Principal.Subject,
            ClientId = current.Request.Principal.ClientId,
            AuthorizedParty = current.Request.Principal.AuthorizedParty,
            GroupsJson = SerializeValues(current.Request.Principal.Groups),
            RolesJson = SerializeValues(current.Request.Principal.Roles),
            ScopesJson = SerializeValues(current.Request.Principal.Scopes),
            McpResource = current.Request.McpResource,
            McpInstance = current.Request.McpInstance,
            Tool = current.Request.Tool,
            TenantId = current.Request.TenantId,
            AgentId = current.Request.AgentId,
            OperationFamily = current.Request.OperationFamily,
            Operation = current.Request.Operation,
            CorrelationId = current.Request.CorrelationId,
            RequestId = current.Request.RequestId,
            OccurredAtUtc = DateTimeOffset.UtcNow
        };
        _db.McpOperatorAcceptedAudits.Add(audit);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new McpOperatorAcceptedAudit(
            audit.Id,
            audit.PolicyId,
            audit.Environment,
            audit.ServicePrincipal,
            audit.Subject,
            audit.ClientId,
            audit.AuthorizedParty,
            DeserializeValues(audit.GroupsJson),
            DeserializeValues(audit.RolesJson),
            DeserializeValues(audit.ScopesJson),
            audit.McpResource,
            audit.McpInstance,
            audit.Tool,
            audit.TenantId,
            audit.AgentId,
            audit.OperationFamily,
            audit.Operation,
            audit.CorrelationId,
            audit.RequestId,
            audit.OccurredAtUtc);
    }

    private static bool PrincipalMatches(McpOperatorPolicyRecord policy, McpOperatorPrincipal principal) => policy.PrincipalSelectorKind switch
    {
        McpOperatorPrincipalSelectorKind.OAuthSubject => string.Equals(policy.PrincipalSelectorValue, principal.Subject, StringComparison.Ordinal),
        McpOperatorPrincipalSelectorKind.OAuthClientId => string.Equals(policy.PrincipalSelectorValue, principal.ClientId, StringComparison.Ordinal),
        McpOperatorPrincipalSelectorKind.OidcGroup => principal.Groups.Contains(policy.PrincipalSelectorValue),
        McpOperatorPrincipalSelectorKind.MappedRole => principal.Roles.Contains(policy.PrincipalSelectorValue),
        McpOperatorPrincipalSelectorKind.ServicePrincipal => string.Equals(policy.PrincipalSelectorValue, principal.ServicePrincipal, StringComparison.Ordinal),
        _ => false
    };

    private static bool TargetSelectorMatches(McpOperatorPolicyRecord policy, McpOperatorAccessRequest request) => policy.TargetSelectorKind switch
    {
        McpOperatorTargetSelectorKind.ExactAgent => policy.AgentId is { } agentId && request.AgentId == agentId,
        McpOperatorTargetSelectorKind.Tenant => true,
        McpOperatorTargetSelectorKind.ClientTag => policy.ClientTag is { Length: > 0 } tag && request.TargetTags?.Contains(tag) == true,
        McpOperatorTargetSelectorKind.ControlPlane => request.TenantId == 0 && request.AgentId is null,
        McpOperatorTargetSelectorKind.DevelopmentEnvironment => IsDevelopmentPolicy(policy) && request.Environment == McpOperatorEnvironment.Development,
        _ => false
    };

    internal static bool IsDevelopmentPolicy(McpOperatorPolicyRecord policy) =>
        policy.Environment == McpOperatorEnvironment.Development &&
        policy.TargetSelectorKind == McpOperatorTargetSelectorKind.DevelopmentEnvironment &&
        policy.TenantId == 0 && policy.AgentId is null && policy.ClientTag is null &&
        policy.TargetClassification is null;

    private static bool ClassificationMatches(McpOperatorPolicyRecord policy, McpOperatorAccessRequest request) =>
        policy.TargetClassification is null || policy.TargetClassification == request.TargetClassification;

    private static bool OperationMatches(McpOperatorPolicyRecord policy, McpOperatorAccessRequest request) =>
        (policy.OperationFamily & request.OperationFamily) == request.OperationFamily &&
        (policy.Operation is null || string.Equals(policy.Operation, request.Operation, StringComparison.Ordinal));

    private static McpOperatorConstraints? ReadConstraints(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, McpOperatorJsonContext.Default.McpOperatorConstraints);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SerializeValues(IReadOnlySet<string> values)
        => JsonSerializer.Serialize(values.Order(StringComparer.Ordinal).ToArray(), McpOperatorJsonContext.Default.StringArray);

    private static IReadOnlyList<string> DeserializeValues(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, McpOperatorJsonContext.Default.StringArray) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsValidRequest(McpOperatorAccessRequest request) =>
        (request.TenantId > 0 ||
            (request.TenantId == 0 && request.AgentId is null && request.TargetClassification is null)) &&
        request.OperationFamily != McpOperatorOperationFamily.None &&
        request.RequiredScopes.Count > 0 &&
        IsSafeToken(request.Principal.Subject, MaxIdentityLength) &&
        (request.Principal.ClientId is null || IsSafeToken(request.Principal.ClientId, MaxIdentityLength)) &&
        IsSafeToken(request.Operation, MaxIdentityLength) &&
        IsSafeToken(request.CorrelationId, MaxIdentityLength) &&
        IsSafeToken(request.RequestId, MaxIdentityLength);

    private static bool IsSafeToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':' or '/' or '@' or '#');
}
