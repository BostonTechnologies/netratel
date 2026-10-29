using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Infrastructure.Identity;

public sealed record IntegrationCredentialGrantRequest(int TenantId, string Permission);

public sealed record IntegrationCredentialCreateRequest(
    string Name,
    IntegrationCredentialPurpose Purpose,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<IntegrationCredentialGrantRequest> Grants,
    string? Resource = null,
    IReadOnlyList<string>? InstancePermissions = null);

public sealed record IntegrationCredentialSecret(
    string CredentialId,
    string PublicId,
    string TokenPrefix,
    string Secret,
    DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => $"Integration credential {CredentialId} (secret redacted)";
}

public sealed record IntegrationCredentialSummary(
    string Id,
    string PublicId,
    string TokenPrefix,
    string Name,
    IntegrationCredentialPurpose Purpose,
    string? Resource,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    IReadOnlyList<IntegrationCredentialGrantRequest> Grants)
{
    public IReadOnlyList<string> InstancePermissions { get; init; } = [];
}

public sealed record VerifiedIntegrationCredential(
    string CredentialId,
    string OwnerPrincipalId,
    IntegrationCredentialPurpose Purpose,
    IReadOnlyList<IntegrationCredentialGrantRequest> Grants)
{
    public string? Resource { get; init; }
    public IReadOnlyList<string> InstancePermissions { get; init; } = [];
}

/// <summary>
/// Revalidates a previously exchanged credential by non-secret identifier.
/// The API uses this on every local HTTP MCP execution, so revocation,
/// expiration, account disablement, and grant changes take effect immediately.
/// </summary>
public interface IIntegrationCredentialCurrentVerifier
{
    async Task<IntegrationCredentialCurrentResult> CheckCurrentAsync(string credentialId,
        IntegrationCredentialPurpose purpose, CancellationToken cancellationToken = default)
    {
        var credential = await VerifyCurrentAsync(credentialId, purpose, cancellationToken).ConfigureAwait(false);
        return new(credential, credential is null ? "delegated_identity_revoked" : null);
    }

    Task<VerifiedIntegrationCredential?> VerifyCurrentAsync(
        string credentialId,
        IntegrationCredentialPurpose purpose,
        CancellationToken cancellationToken = default);
}

public sealed record IntegrationCredentialCurrentResult(VerifiedIntegrationCredential? Credential, string? FailureCode);

public interface IIntegrationCredentialService
{
    async Task<IntegrationCredentialCurrentResult> VerifyWithStatusAsync(string secret, IntegrationCredentialPurpose purpose,
        string? expectedResource = null, CancellationToken cancellationToken = default)
    {
        var credential = await VerifyAsync(secret, purpose, cancellationToken).ConfigureAwait(false);
        return new(credential, credential is null ? "local_credential_invalid" : null);
    }

    Task<IntegrationCredentialSecret> CreateAsync(string ownerPrincipalId, IntegrationCredentialCreateRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntegrationCredentialSummary>> ListAsync(string ownerPrincipalId, CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(string ownerPrincipalId, string credentialId, string revokedByPrincipalId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string ownerPrincipalId, string credentialId, string deletedByPrincipalId, CancellationToken cancellationToken = default);
    Task<VerifiedIntegrationCredential?> VerifyAsync(string secret, IntegrationCredentialPurpose purpose, CancellationToken cancellationToken = default);
}

public interface IIntegrationCredentialPairingIssuer
{
    Task<IntegrationCredentialSecret> CreateForPairingAsync(string ownerPrincipalId, IntegrationCredentialCreateRequest request,
        string pairingRequestId, CancellationToken cancellationToken = default);
}

public interface IIntegrationCredentialReviewedLifecycle
{
    Task ReconcileReviewedAsync(string ownerPrincipalId, string credentialId, IntegrationCredentialCreateRequest approved,
        string approverPrincipalId, string expectedRevisionHash, CancellationToken cancellationToken = default);
}

/// <summary>Owns opaque credential generation, one-way verification, and revocation.</summary>
public sealed class IntegrationCredentialService(NetRatelIdentityDbContext db, TimeProvider? clock = null) : IIntegrationCredentialService, IIntegrationCredentialCurrentVerifier, IIntegrationCredentialReviewedLifecycle, IIntegrationCredentialPairingIssuer
{
    public const string ApiTokenPrefix = "nrt_ic_";
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(365);

    public Task<IntegrationCredentialSecret> CreateAsync(string ownerPrincipalId, IntegrationCredentialCreateRequest request, CancellationToken cancellationToken = default) =>
        CreateCoreAsync(ownerPrincipalId, request, null, cancellationToken);

    public Task<IntegrationCredentialSecret> CreateForPairingAsync(string ownerPrincipalId, IntegrationCredentialCreateRequest request, string pairingRequestId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(pairingRequestId, "N", out _)) throw new IntegrationPairingUnavailableException();
        return CreateCoreAsync(ownerPrincipalId, request, pairingRequestId, cancellationToken);
    }

    private async Task<IntegrationCredentialSecret> CreateCoreAsync(string ownerPrincipalId, IntegrationCredentialCreateRequest request, string? pairingRequestId, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        var resource = request.Resource?.Trim();
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var suppliedGrants = request.Grants ?? [];
        var suppliedInstancePermissions = request.InstancePermissions ?? [];
        var grants = suppliedGrants
            .Where(grant => grant.TenantId > 0 && !string.IsNullOrWhiteSpace(grant.Permission))
            .Select(grant => new IntegrationCredentialGrantRequest(grant.TenantId, grant.Permission.Trim()))
            .Distinct()
            .ToArray();
        var instancePermissions = suppliedInstancePermissions
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (string.IsNullOrWhiteSpace(ownerPrincipalId) || string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            !Enum.IsDefined(request.Purpose) ||
            (request.Purpose == IntegrationCredentialPurpose.HttpMcp && !McpResourceUri.TryNormalize(resource, out _)) ||
            (request.Purpose == IntegrationCredentialPurpose.Api && !string.IsNullOrWhiteSpace(resource)) ||
            request.ExpiresAtUtc <= now || request.ExpiresAtUtc > now.Add(MaximumLifetime) ||
            (grants.Length == 0 && instancePermissions.Length == 0) ||
            suppliedGrants.Count > 64 || suppliedInstancePermissions.Count > 32 ||
            suppliedGrants.Count != grants.Length || suppliedInstancePermissions.Count != instancePermissions.Length ||
            grants.Any(grant => !NetRatelPermissions.All.Contains(grant.Permission) ||
                grant.Permission == NetRatelPermissions.IntegrationManagement) ||
            instancePermissions.Any(permission => !NetRatelPermissions.All.Contains(permission) ||
                string.Equals(permission, NetRatelPermissions.IntegrationManagement, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Credential name, bounded expiry, and at least one explicit permission grant are required.");
        }

        if (request.Purpose == IntegrationCredentialPurpose.HttpMcp)
            McpResourceUri.TryNormalize(resource, out resource);

        var secret = ApiTokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var publicId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var credential = new IntegrationCredential
        {
            Id = pairingRequestId is null ? Guid.NewGuid().ToString("N") : Hash("NetRatel.Pairing.Credential.v1:" + pairingRequestId)[..32].ToLowerInvariant(),
            PairingRequestId = pairingRequestId,
            PublicId = publicId,
            TokenPrefix = secret[..Math.Min(secret.Length, ApiTokenPrefix.Length + 8)],
            SecretHash = Hash(secret),
            OwnerPrincipalId = ownerPrincipalId,
            Purpose = request.Purpose,
            Resource = string.IsNullOrWhiteSpace(resource) ? null : resource,
            Name = name,
            ExpiresAtUtc = request.ExpiresAtUtc
        };
        credential.Grants.AddRange(grants.Select(grant => new IntegrationCredentialGrant
        {
            CredentialId = credential.Id,
            TenantId = grant.TenantId,
            Permission = grant.Permission
        }));
        credential.InstanceGrants.AddRange(instancePermissions.Select(permission => new IntegrationCredentialInstanceGrant
        {
            CredentialId = credential.Id,
            Permission = permission
        }));

        db.IntegrationCredentials.Add(credential);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(credential.Id, credential.PublicId, credential.TokenPrefix, secret, credential.ExpiresAtUtc);
    }

    public async Task<IReadOnlyList<IntegrationCredentialSummary>> ListAsync(string ownerPrincipalId, CancellationToken cancellationToken = default)
    {
        var credentials = await db.IntegrationCredentials.AsNoTracking()
            .Include(credential => credential.Grants)
            .Include(credential => credential.InstanceGrants)
            .Where(credential => credential.OwnerPrincipalId == ownerPrincipalId)
            .OrderByDescending(credential => credential.CreatedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return credentials.Select(credential => new IntegrationCredentialSummary(
                credential.Id, credential.PublicId, credential.TokenPrefix, credential.Name, credential.Purpose,
                credential.Resource, credential.CreatedAtUtc, credential.ExpiresAtUtc, credential.RevokedAtUtc,
                credential.LastUsedAtUtc,
                credential.Grants.OrderBy(grant => grant.TenantId).ThenBy(grant => grant.Permission)
                    .Select(grant => new IntegrationCredentialGrantRequest(grant.TenantId, grant.Permission)).ToArray())
            {
                InstancePermissions = credential.InstanceGrants.Select(grant => grant.Permission).Order().ToArray()
            })
            .ToArray();
    }

    public static string RevisionHash(IntegrationCredentialSummary credential) => Hash(System.Text.Json.JsonSerializer.Serialize(new
    { credential.Id, credential.Name, credential.Purpose, credential.Resource, credential.ExpiresAtUtc, credential.RevokedAtUtc, credential.Grants, credential.InstancePermissions }));

    public async Task ReconcileReviewedAsync(string ownerPrincipalId, string credentialId, IntegrationCredentialCreateRequest approved,
        string approverPrincipalId, string expectedRevisionHash, CancellationToken cancellationToken = default)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (approved.ExpiresAtUtc <= now || approved.ExpiresAtUtc > now.Add(MaximumLifetime) ||
            string.IsNullOrWhiteSpace(approverPrincipalId))
            throw new IntegrationCredentialGrantValidationException("reviewedProposal");
        if (db.Database.IsRelational())
        {
            // Serialize reviewed changes and revocation on the existing credential row. Pairing already owns the transaction.
            if (db.Database.CurrentTransaction is null) throw new IntegrationPairingUnavailableException();
            var locked = await db.IntegrationCredentials.Where(x => x.Id == credentialId && x.OwnerPrincipalId == ownerPrincipalId && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Name, x => x.Name), cancellationToken).ConfigureAwait(false);
            if (locked != 1) throw new IntegrationPairingUnavailableException();
        }
        var current = (await ListAsync(ownerPrincipalId, cancellationToken).ConfigureAwait(false)).SingleOrDefault(x => x.Id == credentialId)
            ?? throw new IntegrationPairingUnavailableException();
        if (RevisionHash(current) != expectedRevisionHash) throw new IntegrationCredentialGrantValidationException("reviewedRevision");
        var credential = await db.IntegrationCredentials.Include(x => x.Grants).Include(x => x.InstanceGrants)
            .SingleOrDefaultAsync(x => x.Id == credentialId && x.OwnerPrincipalId == ownerPrincipalId, cancellationToken).ConfigureAwait(false)
            ?? throw new IntegrationPairingUnavailableException();
        if (credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= now || credential.Purpose != approved.Purpose ||
            !McpResourceUri.Equivalent(credential.Resource, approved.Resource)) throw new IntegrationPairingUnavailableException();
        credential.ExpiresAtUtc = approved.ExpiresAtUtc;
        var wantedGrants = approved.Grants.ToHashSet();
        var removedGrants = credential.Grants.Where(x => !wantedGrants.Contains(new(x.TenantId, x.Permission))).ToArray();
        db.RemoveRange(removedGrants);
        foreach (var removed in removedGrants) credential.Grants.Remove(removed);
        foreach (var added in wantedGrants.Where(x => !credential.Grants.Any(g => g.TenantId == x.TenantId && g.Permission == x.Permission)))
            credential.Grants.Add(new IntegrationCredentialGrant { CredentialId = credential.Id, TenantId = added.TenantId, Permission = added.Permission });
        var wantedInstance = (approved.InstancePermissions ?? []).ToHashSet(StringComparer.Ordinal);
        var removedInstance = credential.InstanceGrants.Where(x => !wantedInstance.Contains(x.Permission)).ToArray();
        db.RemoveRange(removedInstance);
        foreach (var removed in removedInstance) credential.InstanceGrants.Remove(removed);
        foreach (var added in wantedInstance.Where(x => !credential.InstanceGrants.Any(g => g.Permission == x)))
            credential.InstanceGrants.Add(new IntegrationCredentialInstanceGrant { CredentialId = credential.Id, Permission = added });
        db.IntegrationCredentialLifecycleEvents.Add(LifecycleEvent(credentialId, ownerPrincipalId, approverPrincipalId, now, "reconciled"));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RevokeAsync(string ownerPrincipalId, string credentialId, string revokedByPrincipalId, CancellationToken cancellationToken = default)
    {
        if (db.Database.IsRelational())
        {
            // Conditional writes serialize with deletion without loading a stale tracked credential.
            await using var transaction = db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
            await ClearSetupHandoffsAsync(ownerPrincipalId, credentialId, cancellationToken).ConfigureAwait(false);
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var owned = db.IntegrationCredentials.Where(candidate => candidate.Id == credentialId && candidate.OwnerPrincipalId == ownerPrincipalId);
            var changed = await owned.Where(candidate => candidate.RevokedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.RevokedAtUtc, now)
                    .SetProperty(candidate => candidate.RevokedByPrincipalId, revokedByPrincipalId), cancellationToken).ConfigureAwait(false);
            if (changed > 0)
            {
                db.IntegrationCredentialLifecycleEvents.Add(LifecycleEvent(credentialId, ownerPrincipalId, revokedByPrincipalId, now, "revoked"));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            var exists = changed > 0 || await owned.AnyAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return exists;
        }

        var credential = await db.IntegrationCredentials.SingleOrDefaultAsync(candidate =>
            candidate.Id == credentialId && candidate.OwnerPrincipalId == ownerPrincipalId, cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return false;
        }

        if (credential.RevokedAtUtc is null)
        {
            await ClearSetupHandoffsAsync(ownerPrincipalId, credentialId, cancellationToken).ConfigureAwait(false);
            credential.RevokedAtUtc = (clock ?? TimeProvider.System).GetUtcNow();
            credential.RevokedByPrincipalId = revokedByPrincipalId;
            db.IntegrationCredentialLifecycleEvents.Add(LifecycleEvent(credential.Id, ownerPrincipalId, revokedByPrincipalId, credential.RevokedAtUtc.Value, "revoked"));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<bool> DeleteAsync(string ownerPrincipalId, string credentialId, string deletedByPrincipalId, CancellationToken cancellationToken = default)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (db.Database.IsRelational())
        {
            await using var transaction = db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
            // Pairing mutations take the request lock before touching its credential. Keep that
            // order here too, including concurrent cancellation, expiry and acknowledgement.
            await db.IntegrationPairingRequests.Where(request => request.CredentialId == credentialId && request.OwnerPrincipalId == ownerPrincipalId &&
                    (request.State != IntegrationPairingState.Cancelled || request.ProtectedDelivery != null))
                .ExecuteUpdateAsync(update => update.SetProperty(request => request.State, IntegrationPairingState.Cancelled)
                    .SetProperty(request => request.ProtectedDelivery, (string?)null)
                    .SetProperty(request => request.DeliveryExpiresAtUtc, (DateTimeOffset?)null)
                    .SetProperty(request => request.ApprovedGrantsJson, (string?)null)
                    .SetProperty(request => request.ApprovedInstancePermissionsJson, (string?)null)
                    .SetProperty(request => request.CredentialExpiresAtUtc, (DateTimeOffset?)null)
                    .SetProperty(request => request.Revision, request => request.Revision + 1), cancellationToken).ConfigureAwait(false);
            // Keep request -> setup handoff -> credential ordering, as reviewed revocation does.
            await ClearSetupHandoffsAsync(ownerPrincipalId, credentialId, cancellationToken).ConfigureAwait(false);
            // Removing the verifier invalidates active access and cascades only this credential's
            // grants, at the same commit as protected delivery cancellation and lifecycle history.
            var removed = await db.IntegrationCredentials.Where(candidate => candidate.Id == credentialId && candidate.OwnerPrincipalId == ownerPrincipalId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                db.IntegrationCredentialLifecycleEvents.Add(LifecycleEvent(credentialId, ownerPrincipalId, deletedByPrincipalId, now, "deleted"));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return removed > 0;
        }

        var credential = await db.IntegrationCredentials.Include(candidate => candidate.Grants).Include(candidate => candidate.InstanceGrants)
            .SingleOrDefaultAsync(candidate => candidate.Id == credentialId && candidate.OwnerPrincipalId == ownerPrincipalId, cancellationToken).ConfigureAwait(false);
        if (credential is null) return false;
        foreach (var request in await db.IntegrationPairingRequests.Where(request => request.CredentialId == credentialId && request.OwnerPrincipalId == ownerPrincipalId)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            request.State = IntegrationPairingState.Cancelled;
            request.ProtectedDelivery = null;
            request.DeliveryExpiresAtUtc = null;
            request.ApprovedGrantsJson = null;
            request.ApprovedInstancePermissionsJson = null;
            request.CredentialExpiresAtUtc = null;
            request.Revision++;
        }
        db.IntegrationCredentials.Remove(credential);
        db.IntegrationCredentialLifecycleEvents.Add(LifecycleEvent(credentialId, ownerPrincipalId, deletedByPrincipalId, now, "deleted"));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task ClearSetupHandoffsAsync(string owner, string credential, CancellationToken ct)
    {
        var attempts = db.IntegrationSetupAttempts.Where(a => a.OwnerPrincipalId == owner && a.CredentialId == credential);
        if (db.Database.IsRelational())
            await attempts.ExecuteUpdateAsync(update => update.SetProperty(a => a.ProtectedDelivery, (string?)null).SetProperty(a => a.Acknowledged, true), ct).ConfigureAwait(false);
        else
            foreach (var attempt in await attempts.ToListAsync(ct).ConfigureAwait(false)) { attempt.ProtectedDelivery = null; attempt.Acknowledged = true; }
    }

    private static IntegrationCredentialLifecycleEvent LifecycleEvent(string credentialId, string owner, string actor, DateTimeOffset now, string action) => new()
    {
        CredentialId = credentialId, OwnerPrincipalId = owner, ActorPrincipalId = actor, OccurredAtUtc = now, Action = action
    };

    public async Task<VerifiedIntegrationCredential?> VerifyAsync(string secret, IntegrationCredentialPurpose purpose, CancellationToken cancellationToken = default)
        => (await VerifyWithStatusAsync(secret, purpose, null, cancellationToken).ConfigureAwait(false)).Credential;

    public async Task<IntegrationCredentialCurrentResult> VerifyWithStatusAsync(string secret, IntegrationCredentialPurpose purpose,
        string? expectedResource = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secret) || !secret.StartsWith(ApiTokenPrefix, StringComparison.Ordinal))
        {
            return new(null, "local_credential_invalid");
        }

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var suppliedHash = Hash(secret);
        var credential = await db.IntegrationCredentials.AsNoTracking()
            .Include(candidate => candidate.Grants)
            .Include(candidate => candidate.InstanceGrants)
            .SingleOrDefaultAsync(candidate => candidate.SecretHash == suppliedHash && candidate.Purpose == purpose, cancellationToken).ConfigureAwait(false);
        // A database lookup is not proof for lifecycle disclosure. Verify the full
        // hash in constant time and bind the expected resource before classifying.
        if (credential is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(credential.SecretHash), Encoding.ASCII.GetBytes(suppliedHash)) ||
            (expectedResource is not null && !McpResourceUri.Equivalent(credential.Resource, expectedResource)))
            return new(null, "local_credential_invalid");
        var discloseState = purpose == IntegrationCredentialPurpose.HttpMcp && expectedResource is not null;
        IntegrationCredentialCurrentResult Denied(string code) => new(null, discloseState ? code : "local_credential_invalid");
        if (credential.RevokedAtUtc is not null) return Denied("local_credential_revoked");
        if (credential.ExpiresAtUtc <= now) return Denied("local_credential_expired");
        if (!await PairingDeliveryIsCurrentAsync(credential, now, cancellationToken).ConfigureAwait(false))
            return Denied("local_credential_revoked");

        var localOwner = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.PrincipalId == credential.OwnerPrincipalId, cancellationToken).ConfigureAwait(false);
        if (localOwner is not null && !localOwner.IsEnabled)
        {
            return Denied("local_credential_revoked");
        }

        if (db.Database.IsRelational())
        {
            // Never attach and save the authentication snapshot: a concurrent lifecycle write
            // may already have revoked/deleted it, and authorization must then fail closed.
            var updated = await db.IntegrationCredentials.Where(candidate => candidate.Id == credential.Id && candidate.RevokedAtUtc == null && candidate.ExpiresAtUtc > now)
                .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.LastUsedAtUtc, now), cancellationToken).ConfigureAwait(false);
            if (updated == 0)
            {
                var current = await CheckCurrentAsync(credential.Id, purpose, cancellationToken).ConfigureAwait(false);
                return Denied(current.FailureCode == "delegated_identity_revoked" ? "local_credential_revoked" : current.FailureCode ?? "local_credential_invalid");
            }
        }
        else
        {
            var tracked = await db.IntegrationCredentials.SingleOrDefaultAsync(candidate => candidate.Id == credential.Id, cancellationToken).ConfigureAwait(false);
            if (tracked is null) return new(null, "local_credential_invalid");
            if (tracked.RevokedAtUtc is not null) return Denied("local_credential_revoked");
            if (tracked.ExpiresAtUtc <= now) return Denied("local_credential_expired");
            tracked.LastUsedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return new(new(credential.Id, credential.OwnerPrincipalId, credential.Purpose,
            credential.Grants.Select(grant => new IntegrationCredentialGrantRequest(grant.TenantId, grant.Permission)).ToArray())
        {
            Resource = credential.Resource,
            InstancePermissions = credential.InstanceGrants.Select(grant => grant.Permission).ToArray()
        }, null);
    }

    public async Task<VerifiedIntegrationCredential?> VerifyCurrentAsync(
        string credentialId,
        IntegrationCredentialPurpose purpose,
        CancellationToken cancellationToken = default)
        => (await CheckCurrentAsync(credentialId, purpose, cancellationToken).ConfigureAwait(false)).Credential;

    public async Task<IntegrationCredentialCurrentResult> CheckCurrentAsync(
        string credentialId, IntegrationCredentialPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentialId))
            return new(null, "delegated_identity_invalid");

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var credential = await db.IntegrationCredentials.AsNoTracking()
            .Include(candidate => candidate.Grants)
            .Include(candidate => candidate.InstanceGrants)
            .SingleOrDefaultAsync(candidate => candidate.Id == credentialId && candidate.Purpose == purpose, cancellationToken).ConfigureAwait(false);
        if (credential is null) return new(null, "delegated_identity_invalid");
        if (credential.RevokedAtUtc is not null) return new(null, "delegated_identity_revoked");
        if (credential.ExpiresAtUtc <= now) return new(null, "local_credential_expired");
        if (!await PairingDeliveryIsCurrentAsync(credential, now, cancellationToken).ConfigureAwait(false))
            return new(null, "delegated_identity_revoked");

        var localOwner = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.PrincipalId == credential.OwnerPrincipalId, cancellationToken).ConfigureAwait(false);
        if (localOwner is not null && !localOwner.IsEnabled)
            return new(null, "delegated_identity_revoked");

        return new(new(credential.Id, credential.OwnerPrincipalId, credential.Purpose,
            credential.Grants.Select(grant => new IntegrationCredentialGrantRequest(grant.TenantId, grant.Permission)).ToArray())
        {
            Resource = credential.Resource,
            InstancePermissions = credential.InstanceGrants.Select(grant => grant.Permission).ToArray()
        }, null);
    }

    private Task<bool> PairingDeliveryIsCurrentAsync(IntegrationCredential credential, DateTimeOffset now, CancellationToken ct)
    {
        if (credential.PairingRequestId is null || credential.PairingAcknowledgedAtUtc is not null) return Task.FromResult(true);
        // Enforce the orphan deadline on every authentication, even if periodic cleanup cannot run.
        return db.IntegrationPairingRequests.AsNoTracking().AnyAsync(request => request.Id == credential.PairingRequestId &&
            request.CredentialId == credential.Id && request.State == IntegrationPairingState.Issued && request.DeliveryExpiresAtUtc > now, ct);
    }

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
