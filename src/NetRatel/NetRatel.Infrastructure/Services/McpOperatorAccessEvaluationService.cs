using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Read-only projection of the persisted MCP operator policy state. It first
/// establishes tenant visibility, then resolves target facts server-side, and
/// finally delegates exact-operation decisions to the authoritative evaluator.
/// No target capability is invoked and no accepted-operation audit is written.
/// </summary>
public sealed class McpOperatorAccessEvaluationService(
    OrchestratorDbContext db,
    IMcpOperatorAuthorization authorization) : IMcpOperatorAccessEvaluation
{
    private const int MaximumVisibleTenants = 100;
    private readonly OrchestratorDbContext _db = db;
    private readonly IMcpOperatorAuthorization _authorization = authorization;

    public async Task<McpOperatorAccessCaller> WhoAmIAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var policies = (await PrincipalPoliciesAsync(environment, principal, cancellationToken).ConfigureAwait(false))
            .Where(policy => IsActive(policy, now)).ToArray();
        var developmentAccess = policies.Any(policy => McpOperatorAuthorization.IsDevelopmentPolicy(policy) &&
            policy.Effect == McpOperatorPolicyEffect.Allow);
        var tenantIds = policies.Select(policy => policy.TenantId)
            .Distinct()
            .Order()
            .ToArray();

        if (developmentAccess)
            tenantIds = (await _db.Tenants.AsNoTracking().Select(tenant => tenant.Id)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false)).Append(0).Distinct().Order().ToArray();

        return new McpOperatorAccessCaller(
            Redact(principal.Subject),
            string.IsNullOrWhiteSpace(principal.ClientId) ? null : Redact(principal.ClientId),
            environment,
            Ordered(principal.Groups),
            Ordered(principal.Roles),
            Ordered(principal.Scopes),
            developmentAccess ? tenantIds : tenantIds.Take(MaximumVisibleTenants).ToArray(),
            !developmentAccess && tenantIds.Length > MaximumVisibleTenants,
            developmentAccess);
    }

    public async Task<McpOperatorAccessTargetResolution> ResolveTargetAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        int tenantId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        return await ResolveTargetAsync(environment, principal, tenantId, agentId, requireTenantVisibility: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpOperatorAccessTargetResolution> ResolveTargetAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        int tenantId,
        Guid agentId,
        bool requireTenantVisibility,
        CancellationToken cancellationToken)
    {
        if (requireTenantVisibility &&
            !await _authorization.HasTenantVisibilityAsync(environment, principal, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return new McpOperatorAccessTargetResolution(false, null, []);
        }

        var agent = await _db.Agents.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.Id == agentId,
            cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            return new McpOperatorAccessTargetResolution(true, null, []);
        }

        var profile = await _db.McpOperatorTargetProfiles.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.AgentId == agentId,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string>? tags = null;
        var profileConfigured = profile is not null && TryReadTags(profile.TagsJson, out tags);
        var target = new McpOperatorAccessTarget(
            tenantId,
            agentId,
            profileConfigured ? profile!.Classification : null,
            profileConfigured ? tags!.Order(StringComparer.Ordinal).ToArray() : [],
            agent.IsEnabled && agent.Status != AgentStatus.Disabled,
            profileConfigured,
            profileConfigured ? profile!.Version : null);
        var relevant = await RelevantPoliciesAsync(environment, principal, target, cancellationToken).ConfigureAwait(false);
        var policies = Summaries(profileConfigured ? relevant : relevant.Where(McpOperatorAuthorization.IsDevelopmentPolicy));
        return new McpOperatorAccessTargetResolution(true, target, policies);
    }

    public async Task<McpOperatorEffectiveAccess?> EffectiveAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        int tenantId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var caller = await WhoAmIAsync(environment, principal, cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveTargetAsync(environment, principal, tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (!resolved.TenantVisible || resolved.Target is null)
        {
            return null;
        }

        var missing = MissingPrerequisites(resolved.Target, !caller.DevelopmentEnvironmentAccess);
        return new McpOperatorEffectiveAccess(
            caller,
            resolved.Target,
            resolved.RelevantPolicies,
            AllowedFamilies(resolved.RelevantPolicies),
            missing,
            (resolved.Target.ProfileConfigured || caller.DevelopmentEnvironmentAccess)
                ? "Use netratel_access evaluate with one exact catalogued tool and operation before attempting a target action."
                : "Ask a policy administrator to create a reviewed target profile before evaluating an operation.");
    }

    public async Task<McpOperatorExactAccessEvaluation?> EvaluateAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        int tenantId,
        Guid agentId,
        string tool,
        string operation,
        string correlationId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var descriptor = McpOperatorOperationCatalog.Find(tool, operation);
        if (descriptor is null)
            return null;

        var caller = await WhoAmIAsync(environment, principal, cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveTargetAsync(environment, principal, tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (!resolved.TenantVisible || resolved.Target is null)
            return null;

        return await EvaluateResolvedAsync(
            environment,
            principal,
            descriptor,
            caller,
            resolved.Target,
            resolved.RelevantPolicies,
            policyAdministratorInspection: false,
            correlationId,
            requestId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<McpOperatorExactAccessEvaluation?> EvaluateForPolicyAdministratorAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        int tenantId,
        Guid agentId,
        string tool,
        string operation,
        string correlationId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var descriptor = McpOperatorOperationCatalog.Find(tool, operation);
        if (descriptor is null)
            return null;

        var caller = await WhoAmIAsync(environment, principal, cancellationToken).ConfigureAwait(false);
        var resolved = await ResolveTargetAsync(
            environment,
            principal,
            tenantId,
            agentId,
            requireTenantVisibility: false,
            cancellationToken).ConfigureAwait(false);
        if (resolved.Target is null)
            return null;

        return await EvaluateResolvedAsync(
            environment,
            principal,
            descriptor,
            caller,
            resolved.Target,
            resolved.RelevantPolicies,
            policyAdministratorInspection: true,
            correlationId,
            requestId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<McpOperatorExactAccessEvaluation> EvaluateResolvedAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        McpOperatorOperationDescriptor descriptor,
        McpOperatorAccessCaller caller,
        McpOperatorAccessTarget target,
        IReadOnlyList<McpOperatorAccessPolicySummary> matchingPolicies,
        bool policyAdministratorInspection,
        string correlationId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var request = new McpOperatorAccessRequest(
            environment,
            principal,
            target.TenantId,
            target.AgentId,
            target.Classification,
            descriptor.OperationFamily,
            $"{descriptor.ToolName}/{descriptor.OperationName}",
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(descriptor.RequiredScope)], StringComparer.Ordinal),
            descriptor.ConfirmationClass,
            correlationId,
            requestId,
            TargetTags: target.Tags.ToHashSet(StringComparer.Ordinal),
            TargetEnabled: target.Enabled,
            TargetOnline: true,
            CapabilityAvailable: true);
        var decision = await _authorization.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        var missing = MissingPrerequisites(target, !caller.DevelopmentEnvironmentAccess).ToList();
        missing.Add("Target online state and the operation-specific capability are rechecked by the dispatch route.");
        return new McpOperatorExactAccessEvaluation(
            caller,
            target,
            descriptor.ToolName,
            descriptor.OperationName,
            McpOperationAccessScopeNames.Canonical(descriptor.RequiredScope),
            decision.IsAllowed,
            decision.FailureCode,
            decision.FailureLayer,
            matchingPolicies,
            decision.EffectiveConstraints,
            "not_checked",
            missing,
            decision.IsAllowed
                ? $"The policy decision is currently allowed. Invoke {descriptor.ToolName} operation={descriptor.OperationName}; the route will recheck readiness before dispatch."
                : policyAdministratorInspection && (decision.FailureCode is "oauth_scope_missing" or "oauth_role_missing")
                ? $"The current inspection assertion does not meet {descriptor.ToolName}/{descriptor.OperationName}'s scope or role requirements. " +
                  "Inspect the caller-owned netratel_access/receipt or credential admission, or invoke the exact supported operation using its own delegation for current prerequisites and outcome."
                : $"Resolve the '{decision.FailureCode ?? "target_policy_missing"}' prerequisite, then evaluate the same exact operation again.")
        {
            Interpretation = new McpOperatorEvaluationInterpretation(
                policyAdministratorInspection ? "netratel_policy" : "netratel_access",
                "evaluate",
                "current_assertion_only",
                Ordered(principal.Scopes),
                Ordered(principal.Roles),
                descriptor.ToolName,
                descriptor.OperationName,
                [McpOperationAccessScopeNames.Canonical(descriptor.RequiredScope)],
                McpOperationRoleRequirements.AnyOfRoles(descriptor.RequiredScope),
                "not_checked",
                policyAdministratorInspection ? "not_checked" : "checked",
                decision.FailureLayer is McpOperatorAuthorizationLayer.Delegation or McpOperatorAuthorizationLayer.OAuthScope or
                    McpOperatorAuthorizationLayer.Tenant or McpOperatorAuthorizationLayer.Target ? "not_checked" : "evaluated",
                "not_checked",
                "not_checked",
                false,
                "This decision assesses the present signed assertion, not the credential's complete entitlements or another operation's delegation. " +
                "A local policy/evaluate assertion has only its own operation's scope and roles; an inspection-context scope denial does not establish the outcome of a separate business call. " +
                "Inspect the caller-owned reviewed netratel_access/receipt or credential admission, or invoke the exact supported operation for its current prerequisites and outcome. " +
                "Configured grants and matching policy metadata do not establish dispatch success.")
        };
    }

    private async Task<IReadOnlyList<McpOperatorPolicyRecord>> RelevantPoliciesAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        McpOperatorAccessTarget target,
        CancellationToken cancellationToken)
    {
        var policies = await PrincipalPoliciesAsync(environment, principal, cancellationToken).ConfigureAwait(false);
        return policies
            .Where(policy => policy.TenantId == target.TenantId || McpOperatorAuthorization.IsDevelopmentPolicy(policy))
            .Where(policy => TargetMatches(policy, target))
            .Where(policy => policy.TargetClassification is null || policy.TargetClassification == target.Classification)
            .OrderByDescending(policy => policy.Priority)
            .ThenBy(policy => policy.Id)
            .ToArray();
    }

    private async Task<IReadOnlyList<McpOperatorPolicyRecord>> PrincipalPoliciesAsync(
        McpOperatorEnvironment environment,
        McpOperatorPrincipal principal,
        CancellationToken cancellationToken)
    {
        var policies = await _db.McpOperatorPolicies.AsNoTracking()
            .Where(policy => policy.Environment == environment &&
                policy.LifecycleState == McpOperatorPolicyLifecycleState.Active &&
                policy.DisabledAtUtc == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return policies.Where(policy => PrincipalMatches(policy, principal) &&
            (policy.TargetSelectorKind != McpOperatorTargetSelectorKind.DevelopmentEnvironment || McpOperatorAuthorization.IsDevelopmentPolicy(policy))).ToArray();
    }

    private static IReadOnlyList<McpOperatorOperationFamily> AllowedFamilies(IReadOnlyList<McpOperatorAccessPolicySummary> policies)
        => Enum.GetValues<McpOperatorOperationFamily>()
            .Where(family => family != McpOperatorOperationFamily.None)
            .Where(family =>
                policies.Any(policy => policy.Active && policy.Effect == McpOperatorPolicyEffect.Allow &&
                                       policy.Operation is null && policy.OperationFamily.HasFlag(family)) &&
                !policies.Any(policy => policy.Active && policy.Effect == McpOperatorPolicyEffect.Deny &&
                                        policy.Operation is null && policy.OperationFamily.HasFlag(family)))
            .Order()
            .ToArray();

    private static IReadOnlyList<McpOperatorAccessPolicySummary> Summaries(IEnumerable<McpOperatorPolicyRecord> policies)
    {
        var now = DateTimeOffset.UtcNow;
        return policies.Select(policy => new McpOperatorAccessPolicySummary(
                policy.Id,
                policy.Name,
                policy.Effect,
                policy.OperationFamily,
                policy.Operation,
                policy.ExpiresAtUtc,
                policy.ReviewByUtc,
                IsActive(policy, now)))
            .ToArray();
    }

    private static IReadOnlyList<string> MissingPrerequisites(McpOperatorAccessTarget target, bool requireProfile = true)
    {
        var missing = new List<string>();
        if (requireProfile && !target.ProfileConfigured)
            missing.Add("The target has no valid persisted operator profile.");
        if (!target.Enabled)
            missing.Add("The target is disabled.");
        return missing;
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

    private static bool TargetMatches(McpOperatorPolicyRecord policy, McpOperatorAccessTarget target) => policy.TargetSelectorKind switch
    {
        McpOperatorTargetSelectorKind.ExactAgent => policy.AgentId == target.AgentId,
        McpOperatorTargetSelectorKind.Tenant => true,
        McpOperatorTargetSelectorKind.DevelopmentEnvironment => McpOperatorAuthorization.IsDevelopmentPolicy(policy),
        McpOperatorTargetSelectorKind.ClientTag => policy.ClientTag is { Length: > 0 } tag && target.Tags.Contains(tag, StringComparer.Ordinal),
        _ => false
    };

    private static bool TryReadTags(string json, out IReadOnlyList<string>? tags)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize(json, McpOperatorJsonContext.Default.StringArray);
            if (parsed is null || parsed.Any(string.IsNullOrWhiteSpace))
            {
                tags = null;
                return false;
            }

            tags = parsed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return true;
        }
        catch (JsonException)
        {
            tags = null;
            return false;
        }
    }

    private static bool IsActive(McpOperatorPolicyRecord policy, DateTimeOffset now) =>
        policy.LifecycleState == McpOperatorPolicyLifecycleState.Active &&
        policy.DisabledAtUtc is null &&
        (policy.ExpiresAtUtc is null || policy.ExpiresAtUtc > now);

    private static string Redact(string value)
    {
        var prefixLength = Math.Min(3, value.Length);
        var suffixLength = value.Length > 5 ? Math.Min(2, value.Length - prefixLength) : 0;
        return suffixLength == 0
            ? $"{value[..prefixLength]}…"
            : $"{value[..prefixLength]}…{value[^suffixLength..]}";
    }

    private static string[] Ordered(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
