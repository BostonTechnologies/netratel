using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Infrastructure.Identity;

public sealed partial class IntegrationPairingService
{
    private async Task ApplyCompactTargetPoliciesAsync(IntegrationPairingRequest request, ReviewedAccessMetadata metadata,
        string credentialId, CancellationToken ct)
    {
        var tenantNames = await applicationDb!.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct).ConfigureAwait(false);
        foreach (var scope in metadata.Proposal.Targets.GroupBy(t => t.TenantId))
        {
            var current = (await policyAdministration!.ListAsync(metadata.Environment, scope.Key, ct).ConfigureAwait(false)).Items;
            var root = current.SingleOrDefault(p => p.IntegrationCredentialId == credentialId && p.ParentPolicyId is null && p.LifecycleState == McpOperatorPolicyLifecycleState.Active);
            var operations = McpIntegrationPolicyCatalog.WithCanonicalPolicyOperations(scope.Select(t => t.Tool + "/" + t.Operation));
            var families = scope.Aggregate(McpOperatorOperationFamily.None, (f, t) => f | McpOperatorOperationCatalog.Find(t.Tool, t.Operation)!.OperationFamily);
            if (root is { IsContainer: false })
            {
                // A regular integration base already owns admission. Exact requests must
                // fit that reviewed ceiling; they cannot silently replace its bounds.
                if (root.Effect != McpOperatorPolicyEffect.Allow || (families & ~root.OperationFamily) != 0 ||
                    operations.Any(op => root.Operation is { } exact ? exact != op : root.Operations is { } selected && !selected.Contains(op)))
                    throw ReviewValidation("review_policy_changed", "policies", "basePolicy");
                continue;
            }
            if (root is null && scope.All(t => t.AgentId is null))
            {
                await policyAdministration.CreateAsync(new($"{metadata.Proposal.Name} · {(scope.Key == 0 ? "Server access" : tenantNames.GetValueOrDefault(scope.Key, "Unavailable tenant"))} · Reviewed access",
                    metadata.Environment, McpOperatorPolicyEffect.Allow, 0, new(McpOperatorPrincipalSelectorKind.OAuthClientId, credentialId),
                    new(scope.Key == 0 ? McpOperatorTargetSelectorKind.ControlPlane : McpOperatorTargetSelectorKind.Tenant, scope.Key),
                    null, families, null, metadata.TargetPolicyLimits ?? new(), metadata.Proposal.ExpiresAtUtc, null, $"pairing:{request.Id}:base:{scope.Key}")
                { IntegrationCredentialId = credentialId, Resource = request.Resource, Operations = operations }, metadata.ApproverPrincipalId!, ct).ConfigureAwait(false);
                continue;
            }
            if (root is null)
            {
                root = await policyAdministration.CreateAsync(new($"{metadata.Proposal.Name} · {(scope.Key == 0 ? "Server access" : tenantNames.GetValueOrDefault(scope.Key, "Unavailable tenant"))} · Reviewed access",
                    metadata.Environment, McpOperatorPolicyEffect.Allow, 0, new(McpOperatorPrincipalSelectorKind.OAuthClientId, credentialId),
                    new(scope.Key == 0 ? McpOperatorTargetSelectorKind.ControlPlane : McpOperatorTargetSelectorKind.Tenant, scope.Key),
                    null, families, null, new(), metadata.Proposal.ExpiresAtUtc, null, $"pairing:{request.Id}:base:{scope.Key}")
                { IntegrationCredentialId = credentialId, Resource = request.Resource, Operations = operations, IsContainer = true }, metadata.ApproverPrincipalId!, ct).ConfigureAwait(false);
            }
            else
            {
                root = await policyAdministration.ReplaceAsync(root.PolicyId, root.Version, root.ToDraft() with {
                    Operations = (root.Operations ?? []).Concat(operations).Distinct().Order().ToArray(), OperationFamily = root.OperationFamily | families,
                    ExpiresAtUtc = root.ExpiresAtUtc is null || root.ExpiresAtUtc > metadata.Proposal.ExpiresAtUtc ? root.ExpiresAtUtc : metadata.Proposal.ExpiresAtUtc
                }, metadata.ApproverPrincipalId!, ct).ConfigureAwait(false);
            }
            foreach (var group in scope.GroupBy(t => t.AgentId))
            {
                var first = group.First();
                var profile = first.AgentId is { } agent ? await policyAdministration.GetTargetProfileAsync(first.TenantId, agent, ct).ConfigureAwait(false) : null;
                var reference = $"pairing:{request.Id}:target:{scope.Key}:{first.AgentId?.ToString("N") ?? "scope"}";
                if (current.Any(p => p.AuditReference == reference)) continue;
                await policyAdministration.CreateAsync(new($"Reviewed access · {(first.AgentId is null ? "Scope operations" : "Client " + first.AgentId.ToString()![..8])}",
                    metadata.Environment, McpOperatorPolicyEffect.Allow, 0, root.PrincipalSelector,
                    new(first.AgentId is not null ? McpOperatorTargetSelectorKind.ExactAgent : scope.Key == 0 ? McpOperatorTargetSelectorKind.ControlPlane : McpOperatorTargetSelectorKind.Tenant, scope.Key, first.AgentId),
                    profile?.Classification, group.Aggregate(McpOperatorOperationFamily.None, (f, t) => f | McpOperatorOperationCatalog.Find(t.Tool, t.Operation)!.OperationFamily),
                    null, metadata.TargetPolicyLimits ?? new(), metadata.Proposal.ExpiresAtUtc, null, reference)
                { ParentPolicyId = root.PolicyId, IntegrationCredentialId = credentialId, Resource = request.Resource,
                    Operations = McpIntegrationPolicyCatalog.WithCanonicalPolicyOperations(group.Select(t => t.Tool + "/" + t.Operation)) }, metadata.ApproverPrincipalId!, ct).ConfigureAwait(false);
            }
        }
    }
    public async Task<IReadOnlyList<McpIntegrationPolicy>> GetAccountBasePoliciesAsync(string owner, string credentialId,
        McpOperatorEnvironment environment, string? requestId = null, CancellationToken ct = default)
    {
        var credential = await InspectOwnedCredentialAsync(owner, credentialId, ct).ConfigureAwait(false) ?? throw new IntegrationPairingUnavailableException();
        if (requestId is not null)
        {
            var request = await FindAccessAsync(requestId, owner, credential.Id, ct).ConfigureAwait(false) ?? throw new IntegrationPairingUnavailableException();
            return AccessMetadata(request)!.BasePolicies ?? [];
        }
        return await ProposeBasesAsync(credential, credential.Grants.Select(g => new IntegrationPairingGrant(g.TenantId, g.Permission)).ToArray(), credential.InstancePermissions, environment, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<McpIntegrationPolicy>> ProposeBasesAsync(IntegrationCredentialSummary credential,
        IReadOnlyList<IntegrationPairingGrant> grants, IReadOnlyList<string> instancePermissions, McpOperatorEnvironment environment, CancellationToken ct)
    {
        var current = (await policyAdministration!.ListAsync(environment, null, ct).ConfigureAwait(false)).Items
            .Where(p => p.IntegrationCredentialId == credential.Id && p.ParentPolicyId is null && p.LifecycleState == McpOperatorPolicyLifecycleState.Active).ToArray();
        var names = await applicationDb!.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct).ConfigureAwait(false);
        return grants.Select(g => g.TenantId).Concat(instancePermissions.Count > 0 ? [0] : []).Distinct().Order().Select(tenant =>
        {
            var supported = McpIntegrationPolicyCatalog.Supported(tenant, tenant == 0 ? instancePermissions : grants.Where(g => g.TenantId == tenant).Select(g => g.Permission));
            var existing = current.SingleOrDefault(p => p.TargetSelector.TenantId == tenant);
            if (existing is not null && (existing.IsContainer || existing.TargetSelector.Kind is not (McpOperatorTargetSelectorKind.Tenant or McpOperatorTargetSelectorKind.ControlPlane))) throw new IntegrationCredentialGrantValidationException("policies");
            var families = supported.Aggregate(McpOperatorOperationFamily.None, (f, op) => f | op.OperationFamily);
            return existing is null ? new McpIntegrationPolicy(tenant, $"{credential.Name} · {(tenant == 0 ? "Server access" : names.GetValueOrDefault(tenant, "Unavailable tenant"))} · All access",
                    McpOperatorPolicyEffect.Allow, families, McpIntegrationPolicyCatalog.AllAccessLimits, credential.ExpiresAtUtc)
                : new McpIntegrationPolicy(tenant, existing.Name, existing.Effect, existing.OperationFamily & families, existing.Constraints,
                    existing.ExpiresAtUtc ?? credential.ExpiresAtUtc) { Operations = existing.Operation is { } exact ? [exact] : existing.Operations,
                        ExistingPolicyId = existing.PolicyId, ExpectedVersion = existing.Version, LifecycleState = existing.LifecycleState };
        }).ToArray();
    }

    private async Task ValidateBasePlanAsync(ReviewedMcpAccessProposal proposal, IReadOnlyList<McpIntegrationPolicy> plan, CancellationToken ct)
    {
        var scopes = proposal.Grants.Select(g => g.TenantId).Concat(proposal.InstancePermissions.Count > 0 ? [0] : []).Distinct().Order();
        if (!plan.Select(p => p.TenantId).Order().SequenceEqual(scopes)) throw new IntegrationCredentialGrantValidationException("policies");
        foreach (var policy in plan)
        {
            var available = McpIntegrationPolicyCatalog.Supported(policy.TenantId, policy.TenantId == 0 ? proposal.InstancePermissions : proposal.Grants.Where(g => g.TenantId == policy.TenantId).Select(g => g.Permission));
            var families = available.Aggregate(McpOperatorOperationFamily.None, (f, op) => f | op.OperationFamily);
            var operations = McpIntegrationPolicyCatalog.WithCanonicalPolicyOperations(available.Where(op => policy.Families.HasFlag(op.OperationFamily)).Select(op => op.ToolName + "/" + op.OperationName));
            if (policy.Families == McpOperatorOperationFamily.None || (policy.Families & ~families) != 0 || policy.ExpiresAtUtc > proposal.ExpiresAtUtc ||
                policy.ExpiresAtUtc <= clock.GetUtcNow() || !ReviewedMcpAccessExpiry.IsSupported(policy.ExpiresAtUtc) ||
                policy.Operations is { } selected && (selected.Count == 0 || selected.Any(op => !operations.Contains(op))))
                throw new IntegrationCredentialGrantValidationException("policies");
            IntegrationSetupService.ValidateUsableLimits(policy);
            await policyAdministration!.ValidateDraftAsync(new(policy.Name, McpOperatorEnvironment.Production, policy.Effect, 0,
                new(McpOperatorPrincipalSelectorKind.OAuthClientId, proposal.CredentialId!),
                new(policy.TenantId == 0 ? McpOperatorTargetSelectorKind.ControlPlane : McpOperatorTargetSelectorKind.Tenant, policy.TenantId),
                null, policy.Families, null, policy.Constraints, policy.ExpiresAtUtc, null), "integration-policy-review", ct).ConfigureAwait(false);
        }
    }

    private async Task ReconcileBasesAsync(IntegrationPairingRequest request, ReviewedAccessMetadata metadata, string credentialId, CancellationToken ct)
    {
        if (metadata.BasePolicies is not { } plan) return;
        await ValidateBasePlanAsync(metadata.Proposal, plan, ct).ConfigureAwait(false);
        var current = (await policyAdministration!.ListAsync(metadata.Environment, null, ct).ConfigureAwait(false)).Items
            .Where(p => p.IntegrationCredentialId == credentialId && p.ParentPolicyId is null && p.LifecycleState == McpOperatorPolicyLifecycleState.Active).ToArray();
        foreach (var policy in plan)
        {
            var available = McpIntegrationPolicyCatalog.Supported(policy.TenantId, policy.TenantId == 0 ? metadata.Proposal.InstancePermissions : metadata.Proposal.Grants.Where(g => g.TenantId == policy.TenantId).Select(g => g.Permission));
            var operations = policy.Operations ?? McpIntegrationPolicyCatalog.WithCanonicalPolicyOperations(available.Where(op => policy.Families.HasFlag(op.OperationFamily)).Select(op => op.ToolName + "/" + op.OperationName));
            var draft = new McpOperatorPolicyDraft(policy.Name, metadata.Environment, policy.Effect, 0,
                new(McpOperatorPrincipalSelectorKind.OAuthClientId, credentialId),
                new(policy.TenantId == 0 ? McpOperatorTargetSelectorKind.ControlPlane : McpOperatorTargetSelectorKind.Tenant, policy.TenantId),
                null, policy.Families, null, policy.Constraints, policy.ExpiresAtUtc, null, "pairing:" + request.Id + ":base:" + policy.TenantId)
            { IntegrationCredentialId = credentialId, Resource = request.Resource, Operations = operations };
            var existing = current.SingleOrDefault(p => p.TargetSelector.TenantId == policy.TenantId);
            if (policy.ExistingPolicyId is { } expectedId && (existing?.PolicyId != expectedId || existing.Version != policy.ExpectedVersion))
                throw new DbUpdateConcurrencyException("The base policy changed after review.");
            if (existing is null) await policyAdministration.CreateAsync(draft, metadata.ApproverPrincipalId!, ct).ConfigureAwait(false);
            else await policyAdministration.ReplaceAsync(existing.PolicyId, existing.Version, existing.ToDraft() with {
                Name = draft.Name, Effect = draft.Effect, OperationFamily = draft.OperationFamily, Operation = null,
                Operations = draft.Operations, Constraints = draft.Constraints, ExpiresAtUtc = draft.ExpiresAtUtc, AuditReference = draft.AuditReference
            }, metadata.ApproverPrincipalId!, ct).ConfigureAwait(false);
        }
        foreach (var removed in current.Where(p => !plan.Any(b => b.TenantId == p.TargetSelector.TenantId)))
            await policyAdministration!.DisableAsync(removed.PolicyId, removed.Version, metadata.ApproverPrincipalId!, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<ReviewedMcpBasePolicy>? BaseSummaries(ReviewedAccessMetadata metadata) => (metadata.BasePolicies ?? (metadata.TargetPolicyLimits is null ? null : metadata.Proposal.Targets.GroupBy(t => t.TenantId).Select(group => new McpIntegrationPolicy(group.Key, metadata.Proposal.Name + " · Reviewed scope " + group.Key, McpOperatorPolicyEffect.Allow, group.Aggregate(McpOperatorOperationFamily.None, (f, t) => f | McpOperatorOperationCatalog.Find(t.Tool, t.Operation)!.OperationFamily), metadata.TargetPolicyLimits, metadata.Proposal.ExpiresAtUtc) { Operations = McpIntegrationPolicyCatalog.WithCanonicalPolicyOperations(group.Select(t => t.Tool + "/" + t.Operation)) }).ToArray()))?.Select(policy =>
    {
        var available = McpIntegrationPolicyCatalog.Supported(policy.TenantId, policy.TenantId == 0 ? metadata.Proposal.InstancePermissions : metadata.Proposal.Grants.Where(g => g.TenantId == policy.TenantId).Select(g => g.Permission));
        var limits = JsonSerializer.SerializeToElement(policy.Constraints, McpOperatorJsonContext.Default.McpOperatorConstraints);
        return new ReviewedMcpBasePolicy(policy.TenantId, policy.Name, policy.Effect.ToString(), policy.Families.ToString(), policy.ExpiresAtUtc,
            policy.Operations ?? McpIntegrationPolicyCatalog.WithCanonicalPolicyOperations(available.Where(op => policy.Families.HasFlag(op.OperationFamily)).Select(op => op.ToolName + "/" + op.OperationName)),
            limits.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).ToDictionary(p => p.Name, p => p.Value.ToString()));
    }).ToArray();
}
