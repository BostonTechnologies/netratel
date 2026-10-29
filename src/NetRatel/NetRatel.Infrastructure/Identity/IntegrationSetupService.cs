using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Infrastructure.Identity;

/// <summary>Credential, exact approved base policies and retry handoff commit in the same database transaction.</summary>
public sealed class IntegrationSetupService(NetRatelIdentityDbContext identity, OrchestratorDbContext application,
    IIntegrationCredentialService credentials, IIntegrationCredentialGrantValidator validator, IEffectiveAccessService access,
    IMcpOperatorPolicyAdministration policies, IDataProtectionProvider protection)
{
    public async Task<IntegrationCredentialSecret> CreateAsync(ClaimsPrincipal principal, string requestId,
        IntegrationCredentialCreateRequest request, IReadOnlyList<McpIntegrationPolicy> approved,
        McpOperatorEnvironment environment, CancellationToken ct)
    {
        var owner = principal.FindFirstValue("netratel_principal_id") ?? throw new UnauthorizedAccessException();
        if (!Guid.TryParse(requestId, out var id) || id == Guid.Empty || !ReviewedMcpAccessExpiry.IsSupported(request.ExpiresAtUtc) || request.Purpose != IntegrationCredentialPurpose.HttpMcp ||
            !McpResourceUri.TryNormalize(request.Resource, out var resource) || request.Resource != resource || !Enum.IsDefined(environment))
            throw new ArgumentException("Review the connection and setup request.");
        if (!await access.AuthorizeAsync(principal, NetRatelPermissions.McpPolicyAdministration, null, ct).ConfigureAwait(false))
            throw new UnauthorizedAccessException();
        await validator.ValidateAsync(principal, request, ct).ConfigureAwait(false);
        var scopes = request.Grants.Select(g => g.TenantId).Concat((request.InstancePermissions?.Count ?? 0) > 0 ? [0] : []).Distinct().Order().ToArray();
        if (!approved.Select(p => p.TenantId).Order().SequenceEqual(scopes))
            throw new ArgumentException("Approve exactly one base policy for each selected tenant and the separate server scope.");
        var hash = IntegrationCredentialService.Hash(JsonSerializer.Serialize(new { request, approved, environment }));
        await using var transaction = await identity.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await identity.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"integration-setup:" + id.ToString("N")}, 0))", ct).ConfigureAwait(false);
        var existing = await identity.IntegrationSetupAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id.ToString("N"), ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.OwnerPrincipalId != owner || existing.RequestHash != hash) throw new InvalidOperationException("The setup request changed. Keep the approved draft or start a new setup.");
            if (existing.Acknowledged || existing.ProtectedDelivery is null || existing.DeliveryExpiresAtUtc <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("This setup already completed or its secret handoff expired. Inspect the credential list; do not create a duplicate.");
            if (!await identity.IntegrationCredentials.AsNoTracking().AnyAsync(c => c.Id == existing.CredentialId && c.OwnerPrincipalId == owner &&
                c.RevokedAtUtc == null && c.ExpiresAtUtc > DateTimeOffset.UtcNow, ct).ConfigureAwait(false))
                throw new InvalidOperationException("The issued credential is no longer active.");
            var replay = JsonSerializer.Deserialize<IntegrationCredentialSecret>(Protector(existing.Id).Unprotect(existing.ProtectedDelivery))!;
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return replay;
        }
        // Identity and application models share the application database. Enlist both
        // contexts in the identity transaction rather than attempting compensating writes.
        application.Database.SetDbConnection(identity.Database.GetDbConnection(), contextOwnsConnection: false);
        await using var enlistment = await application.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct).ConfigureAwait(false);
        var created = await credentials.CreateAsync(owner, request, ct).ConfigureAwait(false);
        foreach (var policy in approved)
        {
            var permissions = policy.TenantId == 0 ? request.InstancePermissions ?? [] :
                request.Grants.Where(g => g.TenantId == policy.TenantId).Select(g => g.Permission).ToArray();
            var supported = McpIntegrationPolicyCatalog.Supported(policy.TenantId, permissions);
            var families = supported.Aggregate(McpOperatorOperationFamily.None, (f, op) => f | op.OperationFamily);
            if (string.IsNullOrWhiteSpace(policy.Name) || !Enum.IsDefined(policy.Effect) || policy.Families == McpOperatorOperationFamily.None ||
                (policy.Families & ~families) != 0 || policy.ExpiresAtUtc > request.ExpiresAtUtc ||
                !ReviewedMcpAccessExpiry.IsSupported(policy.ExpiresAtUtc) || policy.ExistingPolicyId is not null || policy.ExpectedVersion is not null)
                throw new ArgumentException("Review policy scope, operations, limits and exact expiry.");
            var availableOperations = McpIntegrationPolicyCatalog.WithCanonicalPolicyOperations(supported.Where(op =>
                policy.Families.HasFlag(op.OperationFamily)).Select(op => op.ToolName + "/" + op.OperationName));
            if (policy.Operations is { } selected && (selected.Count is 0 or > 512 || selected.Distinct().Count() != selected.Count || selected.Any(op => !availableOperations.Contains(op))))
                throw new ArgumentException("Selected operations must remain within the approved grants and families.");
            ValidateUsableLimits(policy);
            await policies.CreateAsync(new(policy.Name, environment, policy.Effect, 0,
                new(McpOperatorPrincipalSelectorKind.OAuthClientId, created.CredentialId),
                new(policy.TenantId == 0 ? McpOperatorTargetSelectorKind.ControlPlane : McpOperatorTargetSelectorKind.Tenant, policy.TenantId),
                null, policy.Families, null, policy.Constraints, policy.ExpiresAtUtc, null, "integration-setup:" + id.ToString("N"))
            {
                IntegrationCredentialId = created.CredentialId, Resource = resource,
                Operations = policy.Operations ?? availableOperations
            }, owner, ct).ConfigureAwait(false);
        }
        identity.IntegrationSetupAttempts.Add(new()
        {
            Id = id.ToString("N"), OwnerPrincipalId = owner, CredentialId = created.CredentialId, RequestHash = hash,
            ProtectedDelivery = Protector(id.ToString("N")).Protect(JsonSerializer.Serialize(created)),
            DeliveryExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        await identity.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return created;
    }

    public async Task AcknowledgeAsync(string owner, string requestId, CancellationToken ct)
    {
        if (!Guid.TryParse(requestId, out var id)) throw new ArgumentException("Unknown setup request.");
        await using var transaction = await identity.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await identity.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"integration-setup:" + id.ToString("N")}, 0))", ct).ConfigureAwait(false);
        var attempt = await identity.IntegrationSetupAttempts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id.ToString("N") && x.OwnerPrincipalId == owner, ct).ConfigureAwait(false);
        if (attempt is { Acknowledged: false } && attempt.DeliveryExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The handoff expired. The unacknowledged credential will be revoked; create a new setup.");
        await identity.IntegrationSetupAttempts.Where(x => x.Id == id.ToString("N") && x.OwnerPrincipalId == owner)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ProtectedDelivery, (string?)null).SetProperty(x => x.Acknowledged, true), ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task CleanupAsync(CancellationToken ct)
    {
        var expired = await identity.IntegrationSetupAttempts.AsNoTracking().Where(x => !x.Acknowledged && x.DeliveryExpiresAtUtc <= DateTimeOffset.UtcNow)
            .Take(100).Select(x => x.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var id in expired)
        {
            await using var transaction = await identity.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await identity.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"integration-setup:" + id}, 0))", ct).ConfigureAwait(false);
            var attempt = await identity.IntegrationSetupAttempts.SingleAsync(x => x.Id == id, ct).ConfigureAwait(false);
            if (attempt.Acknowledged) { await transaction.CommitAsync(ct).ConfigureAwait(false); continue; }
            await credentials.RevokeAsync(attempt.OwnerPrincipalId, attempt.CredentialId, attempt.OwnerPrincipalId, ct).ConfigureAwait(false);
            attempt.ProtectedDelivery = null; attempt.Acknowledged = true;
            await identity.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    private IDataProtector Protector(string id) => protection.CreateProtector("NetRatel.IntegrationSetup.Handoff.v1", id);

    public static void ValidateUsableLimits(McpIntegrationPolicy policy)
    {
        if (policy.Effect != McpOperatorPolicyEffect.Allow) return;
        var f = policy.Families; var c = policy.Constraints;
        if (f.HasFlag(McpOperatorOperationFamily.FileRead) && c.ReadRoots is not { Count: > 0 } ||
            f.HasFlag(McpOperatorOperationFamily.FileWrite) && c.WriteRoots is not { Count: > 0 } ||
            (f.HasFlag(McpOperatorOperationFamily.TerminalExecute) || f.HasFlag(McpOperatorOperationFamily.TaskExecution)) &&
                (c.AllowedShells is not { Count: > 0 } || c.WorkingDirectories is not { Count: > 0 } || c.MaxCommandDurationSeconds is not > 0 || c.MaxOutputBytes is not > 0) ||
            f.HasFlag(McpOperatorOperationFamily.Onboarding) && (c.MaxOnboardingCodeLifetimeSeconds is not > 0 || c.MaxOnboardingCodeUses is not > 0))
            throw new ArgumentException("Selected operation families require explicit path, execution or onboarding limits.");
    }
}
