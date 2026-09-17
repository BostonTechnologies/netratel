using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class SecretService(OrchestratorDbContext db) : ISecretService
{
    private readonly OrchestratorDbContext _db = db;

    public async Task<IReadOnlyList<SecretInfo>> ListAsync(CancellationToken ct = default)
        => await _db.Secrets
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(Map())
            .ToListAsync(ct);

    public async Task<SecretInfo?> GetAsync(int id, CancellationToken ct = default)
        => await _db.Secrets
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(Map())
            .FirstOrDefaultAsync(ct);

    public async Task<SecretInfo> CreateAsync(CreateSecretCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Value))
        {
            throw new InvalidOperationException("Secret value is required.");
        }

        var now = DateTimeOffset.UtcNow;
        var secret = new SecretRecord
        {
            TenantId = command.TenantId,
            ClientIdentity = NormalizeNullable(command.ClientIdentity),
            Value = command.Value,
            Description = NormalizeNullable(command.Description),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.Secrets.Add(secret);
        await _db.SaveChangesAsync(ct);
        return Map(secret);
    }

    public async Task<SecretInfo?> UpdateAsync(UpdateSecretCommand command, CancellationToken ct = default)
    {
        var secret = await _db.Secrets.FirstOrDefaultAsync(x => x.Id == command.Id, ct);
        if (secret is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(command.Value))
        {
            throw new InvalidOperationException("Secret value is required.");
        }

        secret.Value = command.Value;
        secret.Description = NormalizeNullable(command.Description);
        secret.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Map(secret);
    }

    public async Task<SecretInfo?> DeleteAsync(int id, CancellationToken ct = default)
    {
        var secret = await _db.Secrets.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (secret is null)
        {
            return null;
        }

        _db.Secrets.Remove(secret);
        await _db.SaveChangesAsync(ct);
        return Map(secret);
    }

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SecretInfo Map(SecretRecord secret) =>
        new(
            secret.Id,
            secret.TenantId,
            secret.ClientIdentity,
            secret.Value,
            secret.Description,
            secret.CreatedAtUtc,
            secret.UpdatedAtUtc);

    private static Expression<Func<SecretRecord, SecretInfo>> Map() =>
        secret => new SecretInfo(
            secret.Id,
            secret.TenantId,
            secret.ClientIdentity,
            secret.Value,
            secret.Description,
            secret.CreatedAtUtc,
            secret.UpdatedAtUtc);
}
