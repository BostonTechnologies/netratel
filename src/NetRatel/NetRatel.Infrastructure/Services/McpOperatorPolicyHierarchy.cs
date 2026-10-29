using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Infrastructure.Services;

public sealed partial class McpOperatorPolicyAdministration
{
    private async Task LockParentAsync(Guid? id, CancellationToken ct)
    {
        if (id is not { } policyId || !_db.Database.IsRelational()) return;
        // A parent is the serialization boundary for child membership and all lifecycle changes.
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"McpOperatorPolicies\" WHERE \"Id\" = {policyId} FOR UPDATE", ct).ConfigureAwait(false);
    }

    private async Task TouchParentAsync(McpOperatorPolicyRecord child, string actor, CancellationToken ct)
    {
        if (child.ParentPolicyId is not { } id) return;
        var parent = await _db.McpOperatorPolicies.SingleAsync(p => p.Id == id, ct).ConfigureAwait(false);
        parent.Version++;
        AddChangeAudit("policy_exception_changed", parent.TenantId, parent.Id, null, parent.Version, actor, PersistableUtcNow());
    }

    private async Task ValidateHierarchyAsync(McpOperatorPolicyDraft draft, CancellationToken ct)
    {
        if (draft.Operations is { } operations && (operations.Count is 0 or > 512 ||
            operations.Distinct(StringComparer.Ordinal).Count() != operations.Count || operations.Any(o => !IsSafeValue(o, 256))))
            throw new ArgumentException("Select a bounded, nonempty set of catalog operations.");
        if (draft.TargetSelector.AgentIds is { } agents)
        {
            if (draft.TargetSelector.Kind != McpOperatorTargetSelectorKind.Tenant || agents.Count is 0 or > 100 ||
                agents.Any(id => id == Guid.Empty) || agents.Distinct().Count() != agents.Count ||
                await _db.Agents.CountAsync(a => agents.Contains(a.Id) && a.TenantId == draft.TargetSelector.TenantId && a.DeletedAtUtc == null, ct).ConfigureAwait(false) != agents.Count)
                throw new ArgumentException("Select existing clients from the chosen tenant.");
        }
        if (draft.TargetSelector.ClientTags is { } tags && (draft.TargetSelector.Kind != McpOperatorTargetSelectorKind.Tenant ||
            tags.Count is 0 or > 32 || tags.Any(tag => !IsSafeValue(tag, 128)) || tags.Distinct().Count() != tags.Count))
            throw new ArgumentException("Select bounded client tags within the tenant.");
        if (draft.IntegrationCredentialId is { } credential && (!Guid.TryParseExact(credential, "N", out _) ||
            draft.PrincipalSelector.Kind != McpOperatorPrincipalSelectorKind.OAuthClientId || draft.PrincipalSelector.Value != credential ||
            !McpResourceUri.TryNormalize(draft.Resource, out var normalized) || normalized != draft.Resource))
            throw new ArgumentException("Integration policies require their exact credential and canonical resource.");
        if (draft.ParentPolicyId is not { } parentId) return;
        var parent = await _db.McpOperatorPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == parentId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The base policy no longer exists.");
        if (parent.ParentPolicyId is not null || parent.LifecycleState != McpOperatorPolicyLifecycleState.Active || parent.DisabledAtUtc is not null ||
            parent.ExpiresAtUtc <= DateTimeOffset.UtcNow || parent.Environment != draft.Environment || parent.TenantId != draft.TargetSelector.TenantId ||
            parent.PrincipalSelectorKind != draft.PrincipalSelector.Kind || parent.PrincipalSelectorValue != draft.PrincipalSelector.Value ||
            parent.IntegrationCredentialId != draft.IntegrationCredentialId || parent.Resource != draft.Resource ||
            parent.ExpiresAtUtc is { } expiry && (draft.ExpiresAtUtc is null || draft.ExpiresAtUtc > expiry) ||
            (draft.OperationFamily & ~parent.OperationFamily) != 0 || draft.IsContainer ||
            parent.Operations is { } ceiling && (draft.Operation is { } exact ? !ceiling.Contains(exact) :
                draft.Operations is null || draft.Operations.Any(o => !ceiling.Contains(o))))
            throw new InvalidOperationException("An exception must remain within its active base policy's identity, scope, operations and expiry.");
        if (draft.Effect == McpOperatorPolicyEffect.Allow && !parent.IsContainer &&
            JsonSerializer.Serialize(draft.Constraints, McpOperatorJsonContext.Default.McpOperatorConstraints) != parent.ConstraintsJson)
            throw new InvalidOperationException("Allow exceptions inherit the base policy limits.");
    }

    private async Task<McpOperatorPolicy?> ChangeLifecycleAsync(Guid id, long expectedVersion, string actor,
        McpOperatorPolicyLifecycleState state, CancellationToken ct)
    {
        if (!IsSafeValue(actor, MaxIdentityLength) || expectedVersion <= 0) throw new ArgumentException("Administrator and version are required.");
        await using var transaction = await BeginProfileTransactionAsync(ct).ConfigureAwait(false);
        var parentId = await _db.McpOperatorPolicies.AsNoTracking().Where(p => p.Id == id).Select(p => p.ParentPolicyId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        await LockParentAsync(parentId ?? id, ct).ConfigureAwait(false);
        var record = await _db.McpOperatorPolicies.SingleOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
        if (record is null) return null;
        // Refresh a previously tracked parent after acquiring the lock; never overwrite a concurrent child change.
        await _db.Entry(record).ReloadAsync(ct).ConfigureAwait(false);
        if (record.Version != expectedVersion) throw new DbUpdateConcurrencyException("The policy or its exceptions changed. Review again.");
        if (record.LifecycleState == McpOperatorPolicyLifecycleState.Deleted && state != McpOperatorPolicyLifecycleState.Deleted ||
            record.LifecycleState == McpOperatorPolicyLifecycleState.Revoked && state == McpOperatorPolicyLifecycleState.Disabled)
            throw new InvalidOperationException("A terminal policy cannot be reactivated or disabled.");
        if (record.LifecycleState != state)
        {
            var children = await _db.McpOperatorPolicies.Where(p => p.ParentPolicyId == id &&
                p.LifecycleState != McpOperatorPolicyLifecycleState.Deleted).ToListAsync(ct).ConfigureAwait(false);
            var now = PersistableUtcNow();
            foreach (var policy in children.Append(record))
            {
                // Disabling a parent must not downgrade terminal child history.
                if (state == McpOperatorPolicyLifecycleState.Disabled && policy.LifecycleState == McpOperatorPolicyLifecycleState.Revoked) continue;
                policy.LifecycleState = state; policy.DisabledAtUtc ??= now; policy.DisabledBy ??= actor;
                policy.Version++;
                AddChangeAudit("policy_" + state.ToString().ToLowerInvariant(), policy.TenantId, policy.Id, null, policy.Version, actor, now);
            }
            await TouchParentAsync(record, actor, ct).ConfigureAwait(false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return ToPolicy(record);
    }
}
