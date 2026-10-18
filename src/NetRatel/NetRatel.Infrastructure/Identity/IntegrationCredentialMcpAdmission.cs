using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Connectivity;
using NetRatel.Shared.Operations;

namespace NetRatel.Infrastructure.Identity;

public sealed partial class IntegrationPairingService
{
    /// <summary>Current owner-scoped facts using the same evaluator as reviewed receipts. Never provisions policies.</summary>
    public async Task<CredentialMcpAdmission?> InspectCredentialAdmissionAsync(string owner, string id,
        McpOperatorEnvironment environment, IReadOnlyList<ReviewedMcpAccessTarget>? selectedTargets = null, CancellationToken ct = default)
    {
        var credential = await InspectOwnedCredentialAsync(owner, id, ct).ConfigureAwait(false);
        if (credential is null) return null;
        if (applicationDb is null || effectiveAccess is null || policyAdministration is null || operatorAuthorization is null)
            throw new IntegrationPairingUnavailableException();
        var candidates = selectedTargets ?? DefaultAdmissionTargets(credential);
        var truncated = selectedTargets is null && candidates.Count > 32;
        var targets = truncated ? candidates.Take(32).ToArray() : candidates;
        if (targets.Count > 32 || targets.Distinct().Count() != targets.Count ||
            targets.Any(x => x is null || string.IsNullOrWhiteSpace(x.Tool) || x.Tool.Length > 128 ||
                string.IsNullOrWhiteSpace(x.Operation) || x.Operation.Length > 128))
            throw new IntegrationCredentialGrantValidationException("targets");
        // Reuse the reviewed contract's catalog, target-shape, configured-grant and profile-revision validation.
        await ValidateTargetsAsync(new(Guid.NewGuid().ToString("N"), credential.Name, "reconcile", credential.ExpiresAtUtc,
            credential.Grants.Select(x => new IntegrationPairingGrant(x.TenantId, x.Permission)).ToArray(), credential.InstancePermissions,
            targets, credential.Id), ct).ConfigureAwait(false);
        var state = credential.RevokedAtUtc is not null ? "revoked" : credential.ExpiresAtUtc <= clock.GetUtcNow() ? "expired" : "active";
        var authentication = "not_checked";
        if (credentials is IIntegrationCredentialCurrentVerifier verifier)
        {
            var current = await verifier.CheckCurrentAsync(credential.Id, IntegrationCredentialPurpose.HttpMcp, ct).ConfigureAwait(false);
            authentication = current.Credential is not null ? "current_credential_valid" : current.FailureCode ?? "unavailable";
            if (state == "active" && current.Credential is null) state = "unavailable";
        }
        var instance = environment == McpOperatorEnvironment.Production ? "prod" : "dev";
        var admissions = await EvaluateCredentialAdmissionAsync(owner, credential, state, credential.Resource!, instance, environment,
            targets, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        var missing = new List<string>();
        if (authentication != "current_credential_valid") missing.Add("Current credential authentication is " + authentication + ".");
        if (state != "active") missing.Add("The credential is " + state + "; configured grants do not establish usable access.");
        if (admissions.Any(x => !x.OwnerPermissionGranted)) missing.Add("Current durable owner permissions do not include every selected operation.");
        if (admissions.Any(x => !x.TenantVisible || x.PolicyAllowed == false))
            missing.Add(environment == McpOperatorEnvironment.Production
                ? "Matching Production operator policies are a separate prerequisite; configured application grants do not install them."
                : "Current tenant or operation policies do not admit every selected operation.");
        if (admissions.Any(x => x.PolicyAdmissionStatus == "conditional_runtime_unverified")) missing.Add("Agent runtime readiness and operation capability remain unverified.");
        if (targets.Count == 0) missing.Add("No catalogued read sample was selected; inspect an exact operation and target through the reviewed access flow.");
        if (truncated) missing.Add("Only the first 32 named read samples are shown. Select exact targets to inspect additional admission.");
        var matchingIds = admissions.SelectMany(x => x.MatchingPolicies).Distinct().ToArray();
        var policies = await applicationDb.McpOperatorPolicies.AsNoTracking().Where(x => matchingIds.Contains(x.Id))
            .OrderBy(x => x.Id).ToArrayAsync(ct).ConfigureAwait(false);
        missing.Add("This bounded view covers only its named operations. Dispatch rechecks current authority, tenant policy, readiness and capability.");
        return new CredentialMcpAdmission(credential.Id, clock.GetUtcNow(), state, authentication, credential.Resource!, instance, environment.ToString(),
            credential.ExpiresAtUtc, credential.Grants.Select(x => new IntegrationPairingGrant(x.TenantId, x.Permission)).ToArray(),
            credential.InstancePermissions, admissions, missing,
            "Use netratel_access request with an exact reconcile proposal, have an independent authorized operator inspect and approve it, then resume the same requestId. Preserve the configured grants and original exact UTC expiry unless the approved proposal changes them.")
        {
            TargetsTruncated = truncated,
            MatchingPolicies = policies.Select(x => new CredentialMcpPolicy(x.Id, x.Environment.ToString(), x.TenantId, x.AgentId,
                x.Operation, x.Effect.ToString(), x.ExpiresAtUtc, x.ReviewByUtc, x.Version)).ToArray()
        };
    }

    private static IReadOnlyList<ReviewedMcpAccessTarget> DefaultAdmissionTargets(IntegrationCredentialSummary credential)
    {
        // These are explicitly named samples, never a claim that a grant admits every operation.
        var targets = new List<ReviewedMcpAccessTarget>();
        foreach (var tenant in credential.Grants.Select(x => x.TenantId).Distinct().Order())
        {
            if (credential.Grants.Any(x => x.TenantId == tenant && x.Permission == NetRatelPermissions.FlowRead))
                targets.Add(new(tenant, "netratel_flows", "list"));
        }
        return targets;
    }

    private async Task<List<ReviewedMcpAccessAdmission>> EvaluateCredentialAdmissionAsync(string owner,
        IntegrationCredentialSummary? credential, string state, string resource, string instance, McpOperatorEnvironment environment,
        IReadOnlyList<ReviewedMcpAccessTarget> targets, string correlationId, CancellationToken ct)
    {
        var credentialId = credential?.Id;
        var admissions = new List<ReviewedMcpAccessAdmission>();
        foreach (var target in targets)
        {
            var access = McpOperationAccessCatalog.Find(target.Tool, target.Operation)!;
            var scope = McpOperationAccessScopeNames.Canonical(access.RequiredScope);
            var roles = McpLocalOperationAuthorizationCatalog.RolesFor(access.RequiredScope).ToArray();
            var permission = McpLocalOperationAuthorizationCatalog.RequiredPermission(target.Tool, target.Operation)!;
            var configured = credential is not null && new[] { permission }.Concat(McpLocalOperationAuthorizationCatalog.AdditionalPermissions(target.Tool, target.Operation))
                .All(p => target.TenantId == 0 ? credential!.InstancePermissions.Contains(p) : credential!.Grants.Any(x => x.TenantId == target.TenantId && x.Permission == p));
            var principal = new McpOperatorPrincipal(owner, credentialId, "netratel-local-http-mcp", new HashSet<string>(), roles.ToHashSet(StringComparer.Ordinal), new HashSet<string>([scope]));
            var visible = credentialId is not null && (target.TenantId == 0 || await operatorAuthorization!.HasTenantVisibilityAsync(environment, principal, target.TenantId, ct).ConfigureAwait(false));
            var profile = target.AgentId is { } agent ? await policyAdministration!.GetTargetProfileAsync(target.TenantId, agent, ct).ConfigureAwait(false) : null;
            var descriptor = McpOperatorOperationCatalog.Find(target.Tool, target.Operation)!;
            var enabled = target.AgentId is null || await applicationDb!.Agents.AsNoTracking().AnyAsync(x => x.Id == target.AgentId && x.TenantId == target.TenantId && x.IsEnabled && x.Status != NetRatel.Infrastructure.Persistence.AgentStatus.Disabled, ct).ConfigureAwait(false);
            var decision = await operatorAuthorization!.EvaluateAsync(new(environment, principal, target.TenantId, target.AgentId,
                profile?.Classification, descriptor.OperationFamily, target.Tool + "/" + target.Operation, new HashSet<string>([scope]), descriptor.ConfirmationClass,
                correlationId, correlationId, TargetTags: profile?.Tags.ToHashSet(StringComparer.Ordinal), McpResource: resource, McpInstance: instance, Tool: target.Tool, TenantVisible: visible, TargetResolved: target.AgentId is null || profile is not null, TargetEnabled: enabled, TargetOnline: target.AgentId is null, CapabilityAvailable: target.AgentId is null), ct).ConfigureAwait(false);
            var ownerGranted = true;
            foreach (var required in new[] { permission }.Concat(McpLocalOperationAuthorizationCatalog.AdditionalPermissions(target.Tool, target.Operation)))
                ownerGranted &= await effectiveAccess!.AuthorizeAsync(DurablePrincipal(owner), required, target.TenantId == 0 ? null : target.TenantId, ct).ConfigureAwait(false);
            var runtimeUnverified = target.AgentId is not null && decision.FailureCode is "capability_unavailable" or "target_offline";
            admissions.Add(new(target, scope, roles, configured, visible, runtimeUnverified ? null : decision.IsAllowed,
                state != "active" ? "credential_" + state : !configured ? "application_grant_missing" : !ownerGranted ? "owner_permission_missing" : !visible ? "tenant_not_authorized" : runtimeUnverified ? "target_runtime_not_checked" : decision.FailureCode,
                decision.MatchingPolicyIds)
            { PolicyAdmissionStatus = runtimeUnverified ? "conditional_runtime_unverified" : "evaluated", OwnerPermissionGranted = ownerGranted, AuthorizationAdmitted = state == "active" && configured && ownerGranted && visible && decision.IsAllowed && !runtimeUnverified });
        }
        return admissions;
    }
}
