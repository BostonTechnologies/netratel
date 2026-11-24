using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Secrets;

namespace NetRatel.Infrastructure.Services;

public sealed partial class SecretService(OrchestratorDbContext db, IDataProtectionProvider protection) : ISecretService
{
    public async Task<IReadOnlyList<SecretInfo>> ListAsync(int[]? authorizedTenantIds, CancellationToken ct = default)
    {
        var records = await InScope(authorizedTenantIds).AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(ct);
        return records.Select(Reveal).ToArray();
    }

    public async Task<SecretInfo?> GetAsync(int id, int[]? authorizedTenantIds, CancellationToken ct = default)
    {
        var secret = await InScope(authorizedTenantIds).AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        return secret is null ? null : Reveal(secret);
    }

    public async Task<SecretMetadata> CreateAsync(CreateSecretCommand command, CancellationToken ct = default)
    {
        ValidateValue(command.Value);
        var secret = await CreateRecordAsync(command, name: null, ct);
        return Metadata(secret);
    }

    private async Task<SecretRecord> CreateRecordAsync(CreateSecretCommand command, string? name, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var secret = new SecretRecord
        {
            TenantId = command.TenantId,
            Name = name,
            ClientIdentity = NormalizeNullable(command.ClientIdentity),
            Description = NormalizeNullable(command.Description),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        // Obtain the database-generated ID without ever persisting the clear value.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Secrets.Add(secret);
        await db.SaveChangesAsync(ct);
        ProtectVersion(secret, command.Value);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return secret;
    }

    public async Task<SecretMetadata?> UpdateAsync(UpdateSecretCommand command, int[]? authorizedTenantIds, CancellationToken ct = default)
    {
        var secret = await InScope(authorizedTenantIds).FirstOrDefaultAsync(x => x.Id == command.Id, ct);
        if (secret is null)
            return null;

        ValidateValue(command.Value);
        ReplaceValue(secret, command.Value);
        secret.Description = NormalizeNullable(command.Description);
        secret.UpdatedAtUtc = DateTimeOffset.UtcNow;
        secret.Revision++;

        await SaveChangesAsync(ct);
        return Metadata(secret);
    }

    public async Task<SecretMetadata?> DeleteAsync(int id, int[]? authorizedTenantIds, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var secret = await InScope(authorizedTenantIds).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (secret is null)
            return null;

        if (secret.TenantId is { } tenantId)
        {
            await SecretConsumerQuery.LockTenantAsync(db, tenantId, ct, requireTenant: false);
            if (await SecretConsumerQuery.HasConsumersAsync(db, tenantId, TenantSecretService.LocalConnectionId(tenantId),
                    secret.Id.ToString(CultureInfo.InvariantCulture), ct))
                throw SecretProviderValidation.InUse();
        }
        db.Secrets.Remove(secret);
        await SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Metadata(secret);
    }

    public async Task ProtectLegacyValuesAsync(CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var records = await db.Secrets.FromSqlRaw(
            """SELECT * FROM "Secrets" WHERE NOT "ValueProtected" ORDER BY "Id" FOR UPDATE""").ToListAsync(ct);
        foreach (var secret in records)
            Protect(secret, secret.Value);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private IQueryable<SecretRecord> InScope(int[]? authorizedTenantIds) =>
        authorizedTenantIds is null
            ? db.Secrets
            : db.Secrets.Where(secret => secret.TenantId.HasValue && authorizedTenantIds.Contains(secret.TenantId.Value));

    private static void ValidateValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Secret value is required.");
    }

    private void Protect(SecretRecord secret, string value)
    {
        secret.Value = Protector(secret).Protect(value);
        secret.ValueProtected = true;
    }

    private SecretInfo Reveal(SecretRecord secret)
    {
        return new SecretInfo(secret.Id, secret.TenantId, secret.ClientIdentity, ReadValue(secret),
            secret.Description, secret.CreatedAtUtc, secret.UpdatedAtUtc);
    }

    private string ReadValue(SecretRecord secret)
    {
        if (!secret.ValueProtected)
            throw new InvalidOperationException("Secret protection migration is incomplete.");

        var protector = secret.VersionBoundProtection ? VersionProtector(secret, secret.Version) : Protector(secret);
        return protector.Unprotect(secret.Value);
    }

    private void ReplaceValue(SecretRecord secret, string value)
    {
        if (secret.TenantId is not null)
        {
            db.SecretVersions.Add(new SecretVersionRecord
            {
                SecretId = secret.Id,
                Version = secret.Version,
                ProtectedValue = secret.VersionBoundProtection
                    ? secret.Value
                    : VersionProtector(secret, secret.Version).Protect(ReadValue(secret))
            });
        }
        secret.Version++;
        ProtectVersion(secret, value);
    }

    private void ProtectVersion(SecretRecord secret, string value)
    {
        secret.Value = VersionProtector(secret, secret.Version).Protect(value);
        secret.ValueProtected = true;
        secret.VersionBoundProtection = true;
    }

    private async Task SaveChangesAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw SecretProviderValidation.Conflict(); }
    }

    private IDataProtector VersionProtector(SecretRecord secret, long version) => protection.CreateProtector(
        "NetRatel.GeneralSecret.Version.v1",
        secret.TenantId?.ToString(CultureInfo.InvariantCulture) ?? "instance",
        secret.Id.ToString(CultureInfo.InvariantCulture),
        version.ToString(CultureInfo.InvariantCulture));

    private IDataProtector Protector(SecretRecord secret) => protection.CreateProtector(
        "NetRatel.GeneralSecret.Value.v1",
        secret.TenantId?.ToString(CultureInfo.InvariantCulture) ?? "instance",
        secret.Id.ToString(CultureInfo.InvariantCulture));

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SecretMetadata Metadata(SecretRecord secret) =>
        new(secret.Id, secret.TenantId, secret.ClientIdentity, secret.Description, secret.CreatedAtUtc, secret.UpdatedAtUtc);
}
