using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Identity;

/// <summary>Reconciles catalog intent without replacing a credential, its scope or its secret.</summary>
public sealed class IntegrationAllAccessReconciliation(NetRatelIdentityDbContext identity, OrchestratorDbContext application)
{
    private const string Actor = "system:all-access-catalog";

    public static McpOperatorConstraints Stamp(McpOperatorConstraints constraints, IEnumerable<string> permissions)
    {
        var selected = permissions.ToHashSet(StringComparer.Ordinal);
        var full = constraints.AllOperations && constraints.Unrestricted && selected.SetEquals(IntegrationPermissionCatalog.Delegable.Select(p => p.Id));
        return constraints with { FullPermissionCatalog = full, PermissionCatalogSnapshot = full ? Fingerprint(selected) : null };
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        await using var transaction = await identity.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        await identity.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({Actor}, 0))", ct).ConfigureAwait(false);
        application.Database.SetDbConnection(identity.Database.GetDbConnection(), contextOwnsConnection: false);
        await using var enlistment = await application.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var credentials = await identity.IntegrationCredentials.AsTracking().Include(c => c.Grants).Include(c => c.InstanceGrants)
            .Where(c => c.Purpose == IntegrationCredentialPurpose.HttpMcp && c.RevokedAtUtc == null && c.ExpiresAtUtc > now)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var credential in credentials)
        {
            var policies = await application.McpOperatorPolicies.AsTracking().Where(p => p.IntegrationCredentialId == credential.Id &&
                p.ParentPolicyId == null && !p.IsContainer && p.Effect == McpOperatorPolicyEffect.Allow &&
                p.LifecycleState == McpOperatorPolicyLifecycleState.Active && p.DisabledAtUtc == null && p.ExpiresAtUtc > now)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var policy in policies)
            {
                if (policy.Resource != credential.Resource || policy.PrincipalSelectorKind != McpOperatorPrincipalSelectorKind.OAuthClientId ||
                    policy.PrincipalSelectorValue != credential.Id || policy.AgentId is not null || policy.ClientTag is not null ||
                    policy.Operation is not null ||
                    policy.TargetSelectorKind != (policy.TenantId == 0 ? McpOperatorTargetSelectorKind.ControlPlane : McpOperatorTargetSelectorKind.Tenant)) continue;
                var permissions = policy.TenantId == 0 ? credential.InstanceGrants.Select(g => g.Permission).ToHashSet(StringComparer.Ordinal)
                    : credential.Grants.Where(g => g.TenantId == policy.TenantId).Select(g => g.Permission).ToHashSet(StringComparer.Ordinal);
                McpOperatorConstraints? constraints;
                try { constraints = JsonSerializer.Deserialize(policy.ConstraintsJson, McpOperatorJsonContext.Default.McpOperatorConstraints); }
                catch (JsonException) { continue; }
                if (constraints is null) continue;
                var legacy = !constraints.AllOperations && IsLegacyPreset(policy, constraints, permissions) &&
                    await HasCreationEvidenceAsync(policy, credential, ct).ConfigureAwait(false);
                if (!constraints.AllOperations && !legacy) continue;
                if (legacy) constraints = McpIntegrationPolicyCatalog.AllAccessLimits with
                { FullPermissionCatalog = true, PermissionCatalogSnapshot = Fingerprint(permissions) };
                if (constraints.FullPermissionCatalog && constraints.PermissionCatalogSnapshot != Fingerprint(permissions))
                    constraints = constraints with { FullPermissionCatalog = false, PermissionCatalogSnapshot = null };
                if (!constraints.FullPermissionCatalog && constraints.Unrestricted &&
                    permissions.SetEquals(IntegrationPermissionCatalog.Delegable.Select(p => p.Id)))
                    constraints = Stamp(constraints, permissions);
                var grantsChanged = false;
                if (constraints.FullPermissionCatalog)
                {
                    foreach (var permission in IntegrationPermissionCatalog.Delegable.Select(p => p.Id).Except(permissions).ToArray())
                    {
                        // Refresh the approved credential ceiling. Authentication and each
                        // operation still intersect these grants with the owner's current authority.
                        if (policy.TenantId == 0) credential.InstanceGrants.Add(new() { CredentialId = credential.Id, Permission = permission });
                        else credential.Grants.Add(new() { CredentialId = credential.Id, TenantId = policy.TenantId, Permission = permission });
                        permissions.Add(permission); grantsChanged = true;
                    }
                }
                if (constraints.FullPermissionCatalog) constraints = constraints with { PermissionCatalogSnapshot = Fingerprint(permissions) };
                var supported = McpIntegrationPolicyCatalog.Supported(policy.TenantId, permissions);
                var operations = McpIntegrationPolicyCatalog.WithCanonicalPolicyOperations(supported.Select(op => op.ToolName + "/" + op.OperationName));
                var families = supported.Aggregate(McpOperatorOperationFamily.None, (f, op) => f | op.OperationFamily);
                var json = JsonSerializer.Serialize(constraints, McpOperatorJsonContext.Default.McpOperatorConstraints);
                if (!grantsChanged && json == policy.ConstraintsJson && families == policy.OperationFamily &&
                    policy.Operations is { } current && current.ToHashSet(StringComparer.Ordinal).SetEquals(operations)) continue;
                policy.ConstraintsJson = json; policy.Operations = operations.ToArray(); policy.OperationFamily = families; policy.Version++;
                application.McpOperatorPolicyChangeAudits.Add(new() { Id = Guid.NewGuid(), Action = legacy ? "policy_all_access_repaired" : "policy_catalog_reconciled",
                    PolicyId = policy.Id, TenantId = policy.TenantId, ActorId = Actor, Version = policy.Version, OccurredAtUtc = now });
                if (grantsChanged) identity.IntegrationCredentialLifecycleEvents.Add(new() { CredentialId = credential.Id,
                    OwnerPrincipalId = credential.OwnerPrincipalId, ActorPrincipalId = Actor, Action = "catalog_sync", OccurredAtUtc = now });
            }
        }
        await identity.SaveChangesAsync(ct).ConfigureAwait(false);
        await application.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> HasCreationEvidenceAsync(McpOperatorPolicyRecord policy, IntegrationCredential credential, CancellationToken ct)
    {
        if (!await application.McpOperatorPolicyChangeAudits.AnyAsync(a => a.PolicyId == policy.Id && a.Version == 1 &&
            a.Action == "policy_created" && a.ActorId == policy.CreatedBy, ct).ConfigureAwait(false)) return false;
        if (policy.AuditReference?.StartsWith("integration-setup:", StringComparison.Ordinal) == true)
        {
            var id = policy.AuditReference["integration-setup:".Length..];
            return await identity.IntegrationSetupAttempts.AnyAsync(a => a.Id == id && a.CredentialId == credential.Id &&
                a.OwnerPrincipalId == credential.OwnerPrincipalId && a.Acknowledged, ct).ConfigureAwait(false);
        }
        return credential.PairingRequestId is { } request && policy.AuditReference == $"pairing:{request}:base:{policy.TenantId}" &&
            credential.PairingAcknowledgedAtUtc is not null;
    }

    /// <summary>Frozen original preset fingerprints, combined with creation evidence; names alone never authorize repair.</summary>
    public static bool IsLegacyPreset(McpOperatorPolicyRecord policy, McpOperatorConstraints constraints, IReadOnlySet<string> permissions)
    {
        if (policy.Version != 1 || policy.Priority != 0 || policy.TargetClassification is not null ||
            policy.AgentIds is { Length: > 0 } || policy.ClientTags is { Length: > 0 } ||
            !policy.Name.EndsWith(" · All access", StringComparison.Ordinal) || policy.Operations is null ||
            JsonSerializer.Serialize(constraints, McpOperatorJsonContext.Default.McpOperatorConstraints) !=
            JsonSerializer.Serialize(McpIntegrationPolicyCatalog.BoundedDefaults, McpOperatorJsonContext.Default.McpOperatorConstraints)) return false;
        var expected = policy.TenantId == 0 ? "29A1AF757A4FD74F36CA7AE05BD7FBCB75FE8CC8FAC7FE2E50528D7D8F312817"
            : "4D4295BFB6047BCC8B0622505A32CA3DCB93CAD036EBB3380ECA473104007BF3";
        return policy.OperationFamily == (McpOperatorOperationFamily)(policy.TenantId == 0 ? 516097 : 1982463) &&
            Fingerprint(policy.Operations) == expected && Fingerprint(permissions) == LegacyPermissionFingerprint;
    }

    private const string LegacyPermissionFingerprint = "9941E88810FBAFFE9DB63636A75D9534ACC1D673CB761844620041478D694E25";
    private static string Fingerprint(IEnumerable<string> values) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', values.Order(StringComparer.Ordinal)))));
}
