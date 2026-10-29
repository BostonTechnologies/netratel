using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Identity;

/// <summary>Explicit, version-bound consolidation. Containers preserve the exact union of legacy rules.</summary>
public sealed class IntegrationPolicyConsolidation(OrchestratorDbContext db, NetRatelIdentityDbContext identity, IDataProtectionProvider protection)
{
    private sealed record Binding(string Owner, string Credential, McpOperatorEnvironment Environment, string Revision, DateTimeOffset Expiry);
    private IDataProtector Protector => protection.CreateProtector("NetRatel.IntegrationPolicyConsolidation.v1");

    public async Task<McpPolicyConsolidationPlan> PreviewAsync(string owner, string credential, McpOperatorEnvironment environment, CancellationToken ct)
    {
        var name = await RequireCredentialAsync(owner, credential, ct).ConfigureAwait(false);
        var rows = await CandidatesAsync(credential, environment, ct).ConfigureAwait(false);
        var names = await db.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct).ConfigureAwait(false);
        var linkedIds = rows.Where(p => !p.IsContainer).Select(p => p.Id).ToArray();
        var hierarchicalScopes = await db.McpOperatorPolicies.AsNoTracking().Where(p => p.ParentPolicyId != null && linkedIds.Contains(p.ParentPolicyId.Value))
            .Select(p => p.TenantId).Distinct().ToArrayAsync(ct).ConfigureAwait(false);
        var conflicts = rows.Where(p => p.TargetSelectorKind == McpOperatorTargetSelectorKind.DevelopmentEnvironment)
            .Select(p => $"{p.Name}: environment-wide policy requires separate review and is preserved.")
            .Concat(hierarchicalScopes.Select(id => $"Scope {id}: an existing base already has exceptions. Its hierarchy is preserved; review overlapping legacy rules separately.")).ToArray();
        var scopes = rows.Where(p => p.TargetSelectorKind != McpOperatorTargetSelectorKind.DevelopmentEnvironment && !hierarchicalScopes.Contains(p.TenantId))
            .GroupBy(p => p.TenantId).Select(group => new McpPolicyConsolidationScope(group.Key,
                name + " · " + (group.Key == 0 ? "Server access" : names.GetValueOrDefault(group.Key, "Unavailable tenant")), group.Count())).ToArray();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);
        return new(Protector.Protect(JsonSerializer.Serialize(new Binding(owner, credential, environment, Revision(rows), expiry))), expiry, scopes, conflicts);
    }

    public async Task<McpPolicyConsolidationPlan> ApplyAsync(string owner, string credential, McpOperatorEnvironment environment, string token, CancellationToken ct)
    {
        var binding = JsonSerializer.Deserialize<Binding>(Protector.Unprotect(token)) ?? throw new ArgumentException("Review consolidation again.");
        if (binding.Owner != owner || binding.Credential != credential || binding.Environment != environment || binding.Expiry <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The consolidation review changed or expired.");
        var name = await RequireCredentialAsync(owner, credential, ct).ConfigureAwait(false);
        var resource = await identity.IntegrationCredentials.Where(c => c.Id == credential).Select(c => c.Resource).SingleAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"McpOperatorPolicies\" WHERE \"PrincipalSelectorValue\" = {credential} AND \"Environment\" = {(short)environment} ORDER BY \"Id\" FOR UPDATE", ct).ConfigureAwait(false);
        var rows = await CandidatesAsync(credential, environment, ct).ConfigureAwait(false);
        if (binding.Revision != Revision(rows)) throw new DbUpdateConcurrencyException("The policy inventory changed. Review consolidation again.");
        var preview = await PreviewAsync(owner, credential, environment, ct).ConfigureAwait(false);
        foreach (var scope in preview.Scopes)
        {
            var originals = rows.Where(p => p.TenantId == scope.TenantId && p.TargetSelectorKind != McpOperatorTargetSelectorKind.DevelopmentEnvironment).ToArray();
            var linked = originals.SingleOrDefault(p => p.IntegrationCredentialId == credential && p.ParentPolicyId is null);
            if (originals.Length == 1 && linked is not null) continue;
            var now = DateTimeOffset.UtcNow;
            if (originals.Length == 1 && linked is null && originals[0].TargetSelectorKind is McpOperatorTargetSelectorKind.Tenant or McpOperatorTargetSelectorKind.ControlPlane) {
                var only = originals[0]; db.Attach(only); only.IntegrationCredentialId = credential; only.Resource = resource; only.Version++;
                db.McpOperatorPolicyChangeAudits.Add(new() { Id = Guid.NewGuid(), Action = "policy_consolidated", PolicyId = only.Id, TenantId = only.TenantId, ActorId = owner, Version = only.Version, OccurredAtUtc = now });
                await db.SaveChangesAsync(ct).ConfigureAwait(false); continue;
            }
            // Retire a previous active base inside this transaction before inserting its successor.
            if (linked is not null && !linked.IsContainer)
            {
                db.Attach(linked); linked.LifecycleState = McpOperatorPolicyLifecycleState.Disabled;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            var container = linked is { IsContainer: true } ? linked : new McpOperatorPolicyRecord {
                Id = Guid.NewGuid(), Name = scope.Name + " · Preserved legacy access", Environment = environment,
                Effect = McpOperatorPolicyEffect.Allow, PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthClientId,
                PrincipalSelectorValue = credential, TargetSelectorKind = scope.TenantId == 0 ? McpOperatorTargetSelectorKind.ControlPlane : McpOperatorTargetSelectorKind.Tenant,
                TenantId = scope.TenantId, IntegrationCredentialId = credential, Resource = resource, IsContainer = true,
                CreatedAtUtc = now, CreatedBy = owner, Version = 0, AuditReference = "legacy-consolidation" };
            if (container.Version == 0) db.McpOperatorPolicies.Add(container); else db.Attach(container);
            container.OperationFamily = originals.Aggregate(McpOperatorOperationFamily.None, (f, p) => f | p.OperationFamily);
            // Null preserves a pre-existing family-wide rule. A container cannot admit an operation by itself.
            container.Operations = originals.Any(p => p.Operation is null && p.Operations is null) ? null : McpIntegrationPolicyCatalog.WithCanonicalPolicyOperations(originals.SelectMany(p => p.Operation is { } op ? [op] : p.Operations ?? [])).ToArray();
            container.ExpiresAtUtc = originals.Any(p => p.ExpiresAtUtc is null) ? null : originals.Max(p => p.ExpiresAtUtc);
            container.Version++;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var original in originals.Where(p => p.Id != container.Id))
            {
                if (db.Entry(original).State == EntityState.Detached) db.Attach(original);
                original.ParentPolicyId = container.Id; original.IntegrationCredentialId = credential; original.Resource = resource;
                original.LifecycleState = McpOperatorPolicyLifecycleState.Active; original.Version++;
                db.McpOperatorPolicyChangeAudits.Add(new() { Id = Guid.NewGuid(), Action = "policy_consolidated", PolicyId = original.Id,
                    TenantId = original.TenantId, ActorId = owner, Version = original.Version, OccurredAtUtc = now });
            }
            db.McpOperatorPolicyChangeAudits.Add(new() { Id = Guid.NewGuid(), Action = "policy_consolidation_base", PolicyId = container.Id,
                TenantId = container.TenantId, ActorId = owner, Version = container.Version, OccurredAtUtc = now });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return preview with { PlanToken = "", Applied = true };
    }

    private async Task<string> RequireCredentialAsync(string owner, string credential, CancellationToken ct) =>
        await identity.IntegrationCredentials.AsNoTracking().Where(c => c.Id == credential && c.OwnerPrincipalId == owner && c.Purpose == IntegrationCredentialPurpose.HttpMcp)
            .Select(c => c.Name).SingleOrDefaultAsync(ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("Credential unavailable.");
    private Task<List<McpOperatorPolicyRecord>> CandidatesAsync(string credential, McpOperatorEnvironment environment, CancellationToken ct) =>
        db.McpOperatorPolicies.AsNoTracking().Where(p => p.Environment == environment && p.PrincipalSelectorKind == McpOperatorPrincipalSelectorKind.OAuthClientId &&
            p.PrincipalSelectorValue == credential && p.ParentPolicyId == null && p.LifecycleState == McpOperatorPolicyLifecycleState.Active && p.DisabledAtUtc == null &&
            (p.ExpiresAtUtc == null || p.ExpiresAtUtc > DateTimeOffset.UtcNow)).OrderBy(p => p.Id).ToListAsync(ct);
    private static string Revision(IEnumerable<McpOperatorPolicyRecord> rows) => IntegrationCredentialService.Hash(JsonSerializer.Serialize(rows.Select(p => new { p.Id, p.Version })));
}
