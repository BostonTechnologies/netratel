using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Services;
using NetRatel.Infrastructure.Persistence;
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
        var candidates = selectedTargets ?? await DefaultAdmissionTargetsAsync(credential, ct).ConfigureAwait(false);
        var truncated = selectedTargets is null && candidates.Count > 32;
        var targets = truncated ? candidates.Take(32).ToArray() : candidates;
        if (truncated && !targets.Any(x => x.Tool == "netratel_search" && x.AgentId is not null))
            targets = targets.Where(x => x.Tool != "netratel_search" || x.Operation != "clients").ToArray();
        if (targets.Count > 32 || targets.Any(x => x is null || string.IsNullOrWhiteSpace(x.Tool) || x.Tool.Length > 128 ||
                string.IsNullOrWhiteSpace(x.Operation) || x.Operation.Length > 128))
            throw new IntegrationCredentialGrantValidationException("targets");
        if (selectedTargets is not null)
        {
            var currentTargets = new List<ReviewedMcpAccessTarget>();
            foreach (var target in targets)
            {
                if (target?.AgentId is { } agent && target.ProfileRevision is null)
                {
                    var revision = await applicationDb.McpOperatorTargetProfiles.AsNoTracking()
                        .Where(x => x.TenantId == target.TenantId && x.AgentId == agent)
                        .Select(x => (long?)x.Version).SingleOrDefaultAsync(ct).ConfigureAwait(false);
                    currentTargets.Add(target with { ProfileRevision = revision });
                }
                else currentTargets.Add(target!);
            }
            targets = currentTargets;
        }
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
        var missingProductionPolicy = environment == McpOperatorEnvironment.Production && state == "active" &&
            admissions.Any(x => x.ApplicationGrantConfigured && x.OwnerPermissionGranted &&
                x.FailureCode is "tenant_not_authorized" or "target_policy_missing");
        if (missingProductionPolicy)
            missing.Add("Matching Production operator policies are a separate prerequisite; configured application grants do not install them.");
        else if (admissions.Any(x => !x.TenantVisible || x.PolicyAllowed == false))
            missing.Add("One or more selected operations is not admitted; inspect its exact failure code and current prerequisites.");
        if (admissions.Any(x => x.Target.AgentId is not null)) missing.Add("Agent runtime readiness and operation capability remain unverified.");
        if (targets.Count == 0) missing.Add("No catalogued read sample was selected; inspect an exact operation and target through the reviewed access flow.");
        if (truncated) missing.Add("Only the first 32 named read samples are shown. Select exact targets to inspect additional admission.");
        var matchingIds = admissions.SelectMany(x => x.MatchingPolicies).Distinct().ToArray();
        var policies = await applicationDb.McpOperatorPolicies.AsNoTracking().Where(x => matchingIds.Contains(x.Id))
            .OrderBy(x => x.Id).ToArrayAsync(ct).ConfigureAwait(false);
        missing.Add("This bounded view covers only its named operations. Dispatch rechecks current authority, tenant policy, readiness and capability.");
        return new CredentialMcpAdmission(credential.Id, clock.GetUtcNow(), state, authentication, credential.Resource!, instance, environment.ToString(),
            credential.ExpiresAtUtc, credential.Grants.Select(x => new IntegrationPairingGrant(x.TenantId, x.Permission)).ToArray(),
            credential.InstancePermissions, admissions, missing,
            "Use Details → Configured access → Edit access to review grants and base policies. Use Admin → MCP Policies for exceptions or required profile facts. Unrestricted tenant policies cover current and newly enrolled clients without profiles. Retain existing grants and exact expiry unless intentionally changing them.")
        {
            TargetsTruncated = truncated,
            MatchingPolicies = policies.Select(x => new CredentialMcpPolicy(x.Id, x.Environment.ToString(), x.TenantId, x.AgentId,
                x.Operation, x.Effect.ToString(), x.ExpiresAtUtc, x.ReviewByUtc, x.Version)).ToArray()
        };
    }

    private async Task<IReadOnlyList<ReviewedMcpAccessTarget>> DefaultAdmissionTargetsAsync(IntegrationCredentialSummary credential, CancellationToken ct)
    {
        // Representative read coverage only; the browser can select any exact operation.
        var targets = new List<ReviewedMcpAccessTarget>();
        bool Granted(int tenant, string tool, string operation) =>
            new[] { McpLocalOperationAuthorizationCatalog.RequiredPermission(tool, operation)! }
                .Concat(McpLocalOperationAuthorizationCatalog.AdditionalPermissions(tool, operation))
                .All(permission => tenant == 0 ? credential.InstancePermissions.Contains(permission) :
                    credential.Grants.Any(grant => grant.TenantId == tenant && grant.Permission == permission));
        if (Granted(0, "netratel_search", "clients")) targets.Add(new(0, "netratel_search", "clients"));
        foreach (var tenant in credential.Grants.Select(x => x.TenantId).Distinct().Order())
        {
            if (Granted(tenant, "netratel_flows", "list")) targets.Add(new(tenant, "netratel_flows", "list"));
            var agent = await applicationDb!.Agents.AsNoTracking().Where(x => x.TenantId == tenant && x.IsEnabled && x.Status != AgentStatus.Disabled)
                .OrderBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (agent is { } id)
            {
                var revision = await applicationDb.McpOperatorTargetProfiles.AsNoTracking().Where(x => x.TenantId == tenant && x.AgentId == id)
                    .Select(x => (long?)x.Version).SingleOrDefaultAsync(ct).ConfigureAwait(false);
                if (Granted(0, "netratel_search", "clients") && Granted(tenant, "netratel_search", "clients")) targets.Add(new(tenant, "netratel_search", "clients", id, revision));
                if (Granted(tenant, "netratel_monitoring", "client_series")) targets.Add(new(tenant, "netratel_monitoring", "client_series", id, revision));
            }
        }
        // Search needs both its control-plane boundary and at least one exact candidate.
        if (!targets.Any(x => x.Tool == "netratel_search" && x.AgentId is not null)) targets.RemoveAll(x => x.Tool == "netratel_search");
        foreach (var (tool, operation) in new[] { ("netratel_health", "get"), ("netratel_policy", "policies"), ("netratel_connectivity", "settings") })
            if (Granted(0, tool, operation)) targets.Add(new(0, tool, operation));
        return targets;
    }

    // These diagnostic routes authorize the operation's credential/owner/delegation boundary
    // directly. Claiming a missing target policy for them would disagree with actual dispatch.
    private static bool RequiresOperatorPolicy(ReviewedMcpAccessTarget target) => target.Tool switch
    {
        "netratel_auth" or "netratel_capabilities" or "netratel_health" or "netratel_system" or "netratel_access" => false,
        "netratel_policy" when target.Operation is "policies" or "policy" or "change_audits" or "accepted_audits" or "target" or "matches" or "evaluate" => false,
        "netratel_search" when target.Operation == "clients" && target.AgentId is null => false,
        _ => true
    };

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
            var requiresPolicy = RequiresOperatorPolicy(target);
            var visible = credentialId is not null && (!requiresPolicy || target.TenantId == 0 || await operatorAuthorization!.HasTenantVisibilityAsync(environment, principal, target.TenantId, ct).ConfigureAwait(false));
            var agentRecord = target.AgentId is { } agent ? await applicationDb!.Agents.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == agent && x.TenantId == target.TenantId, ct).ConfigureAwait(false) : null;
            var profile = target.AgentId is { } profileAgent ? await applicationDb!.McpOperatorTargetProfiles.AsNoTracking()
                .SingleOrDefaultAsync(x => x.AgentId == profileAgent && x.TenantId == target.TenantId, ct).ConfigureAwait(false) : null;
            var facts = McpOperatorTargetFacts.Read(target.TenantId, target.AgentId ?? Guid.Empty, profile);
            var descriptor = McpOperatorOperationCatalog.Find(target.Tool, target.Operation)!;
            var enabled = target.AgentId is null || agentRecord is { IsEnabled: true, Status: not AgentStatus.Disabled };
            // This inspection evaluates authorization only. Live readiness and capabilities remain unchecked.
            var decision = requiresPolicy ? await operatorAuthorization!.EvaluateAsync(new(environment, principal, target.TenantId, target.AgentId,
                facts.Classification, descriptor.OperationFamily, target.Tool + "/" + target.Operation, new HashSet<string>([scope]), descriptor.ConfirmationClass,
                correlationId, correlationId, facts.Digest, facts.Tags, resource, instance, target.Tool,
                TenantVisible: visible, TargetResolved: target.AgentId is null || agentRecord is not null, TargetEnabled: enabled)
                { TargetProfileInvalid = facts.Invalid }, ct).ConfigureAwait(false) : null;
            var ownerGranted = true;
            foreach (var required in new[] { permission }.Concat(McpLocalOperationAuthorizationCatalog.AdditionalPermissions(target.Tool, target.Operation)))
                ownerGranted &= await effectiveAccess!.AuthorizeAsync(DurablePrincipal(owner), required, target.TenantId == 0 ? null : target.TenantId, ct).ConfigureAwait(false);
            admissions.Add(new(target, scope, roles, configured, visible, decision?.IsAllowed,
                state != "active" ? "credential_" + state : !configured ? "application_grant_missing" : !ownerGranted ? "owner_permission_missing" : !visible ? "tenant_not_authorized" : decision?.FailureCode,
                decision?.MatchingPolicyIds ?? [])
            { PolicyAdmissionStatus = requiresPolicy ? "evaluated" : "not_required_by_route", OwnerPermissionGranted = ownerGranted,
                AuthorizationAdmitted = state == "active" && configured && ownerGranted && visible && (decision is null || decision.IsAllowed),
                FailureLayer = state != "active" ? "Credential" : !configured ? "ApplicationGrant" : !ownerGranted ? "OwnerPermission" : decision?.FailureLayer?.ToString(), TargetReadiness = state == "active" && configured && ownerGranted && visible && !enabled ? "disabled" : "not_checked" });
        }
        return admissions;
    }
}
