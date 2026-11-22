using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class SecretService(OrchestratorDbContext db, IDataProtectionProvider protection) : ISecretService
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
        var now = DateTimeOffset.UtcNow;
        var secret = new SecretRecord
        {
            TenantId = command.TenantId,
            ClientIdentity = NormalizeNullable(command.ClientIdentity),
            Description = NormalizeNullable(command.Description),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        // Obtain the database-generated ID without ever persisting the clear value.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Secrets.Add(secret);
        await db.SaveChangesAsync(ct);
        Protect(secret, command.Value);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Metadata(secret);
    }

    public async Task<SecretMetadata?> UpdateAsync(UpdateSecretCommand command, int[]? authorizedTenantIds, CancellationToken ct = default)
    {
        var secret = await InScope(authorizedTenantIds).FirstOrDefaultAsync(x => x.Id == command.Id, ct);
        if (secret is null)
            return null;

        ValidateValue(command.Value);
        Protect(secret, command.Value);
        secret.Description = NormalizeNullable(command.Description);
        secret.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Metadata(secret);
    }

    public async Task<SecretMetadata?> DeleteAsync(int id, int[]? authorizedTenantIds, CancellationToken ct = default)
    {
        var secret = await InScope(authorizedTenantIds).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (secret is null)
            return null;

        db.Secrets.Remove(secret);
        await db.SaveChangesAsync(ct);
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
        if (!secret.ValueProtected)
            throw new InvalidOperationException("Secret protection migration is incomplete.");

        return new SecretInfo(secret.Id, secret.TenantId, secret.ClientIdentity, Protector(secret).Unprotect(secret.Value),
            secret.Description, secret.CreatedAtUtc, secret.UpdatedAtUtc);
    }

    private IDataProtector Protector(SecretRecord secret) => protection.CreateProtector(
        "NetRatel.GeneralSecret.Value.v1",
        secret.TenantId?.ToString(CultureInfo.InvariantCulture) ?? "instance",
        secret.Id.ToString(CultureInfo.InvariantCulture));

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SecretMetadata Metadata(SecretRecord secret) =>
        new(secret.Id, secret.TenantId, secret.ClientIdentity, secret.Description, secret.CreatedAtUtc, secret.UpdatedAtUtc);
}
