using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Persistence;
using System.Text.RegularExpressions;

namespace NetRatel.Infrastructure.Services;

public sealed class TenantService(OrchestratorDbContext db) : ITenantService
{
    private readonly OrchestratorDbContext _db = db;

    public async Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken ct = default)
        => await _db.Tenants
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(Map())
            .ToListAsync(ct);

    public async Task<TenantInfo?> GetAsync(int tenantId, CancellationToken ct = default)
        => await _db.Tenants
            .AsNoTracking()
            .Where(x => x.Id == tenantId)
            .Select(Map())
            .FirstOrDefaultAsync(ct);

    public Task<bool> ExistsAsync(int tenantId, CancellationToken ct = default)
        => _db.Tenants.AsNoTracking().AnyAsync(x => x.Id == tenantId, ct);

    public async Task<TenantInfo> CreateAsync(CreateTenantCommand command, CancellationToken ct = default)
    {
        var normalizedName = command.Name.Trim();
        var normalizedDomains = NormalizeDomains(command.Domains);

        if (await _db.Tenants.AnyAsync(x => x.Name == normalizedName, ct))
        {
            throw new InvalidOperationException($"Tenant '{normalizedName}' already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant
        {
            Name = normalizedName,
            Description = NormalizeNullable(command.Description),
            Location = NormalizeNullable(command.Location),
            Domains = normalizedDomains,
            ContactPerson = NormalizeNullable(command.ContactPerson),
            ContactEmail = NormalizeNullable(command.ContactEmail),
            AutoUpdate = command.AutoUpdate,
            AutoUpdateChannel = NormalizeChannel(command.AutoUpdateChannel),
            AutoUpdateTargetVersion = NormalizeTarget(command.AutoUpdateTargetVersion),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1
        };

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);

        return Map(tenant);
    }

    public async Task<TenantInfo?> UpdateAsync(UpdateTenantCommand command, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(x => x.Id == command.TenantId, ct);
        if (tenant is null)
        {
            return null;
        }

        if (command.ExpectedVersion is { } expectedVersion && tenant.Version != expectedVersion)
        {
            throw new TenantConcurrencyException();
        }

        var normalizedName = command.Name.Trim();
        var duplicateName = await _db.Tenants
            .AnyAsync(x => x.Id != command.TenantId && x.Name == normalizedName, ct);

        if (duplicateName)
        {
            throw new InvalidOperationException($"Tenant '{normalizedName}' already exists.");
        }

        tenant.Name = normalizedName;
        tenant.Description = NormalizeNullable(command.Description);
        tenant.Location = NormalizeNullable(command.Location);
        tenant.Domains = NormalizeDomains(command.Domains);
        tenant.ContactPerson = NormalizeNullable(command.ContactPerson);
        tenant.ContactEmail = NormalizeNullable(command.ContactEmail);
        tenant.AutoUpdate = command.AutoUpdate;
        tenant.AutoUpdateChannel = NormalizeChannel(command.AutoUpdateChannel);
        tenant.AutoUpdateTargetVersion = NormalizeTarget(command.AutoUpdateTargetVersion);
        tenant.UpdatedAtUtc = DateTimeOffset.UtcNow;
        tenant.Version++;

        await _db.SaveChangesAsync(ct);
        return Map(tenant);
    }

    public Task<TenantInfo?> DeleteAsync(int tenantId, CancellationToken ct = default) =>
        DeleteAsync(tenantId, null, ct);

    public Task<TenantInfo?> DeleteAsync(int tenantId, long expectedVersion, CancellationToken ct = default) =>
        DeleteAsync(tenantId, (long?)expectedVersion, ct);

    private async Task<TenantInfo?> DeleteAsync(int tenantId, long? expectedVersion, CancellationToken ct)
    {
        var tenant = await _db.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId, ct);
        if (tenant is null)
        {
            return null;
        }

        if (expectedVersion is { } currentVersion && tenant.Version != currentVersion)
        {
            throw new TenantConcurrencyException();
        }

        _db.Tenants.Remove(tenant);
        await _db.SaveChangesAsync(ct);
        return Map(tenant);
    }

    private static List<string> NormalizeDomains(IReadOnlyList<string> domains) =>
        domains
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TenantInfo Map(Tenant tenant) =>
        new(
            tenant.Id,
            tenant.Name,
            tenant.Description,
            tenant.Location,
            tenant.Domains,
            tenant.ContactPerson,
            tenant.ContactEmail,
            tenant.AutoUpdate,
            tenant.CreatedAtUtc,
            tenant.UpdatedAtUtc,
            tenant.AutoUpdateChannel,
            tenant.AutoUpdateTargetVersion,
            tenant.Version);

    private static System.Linq.Expressions.Expression<Func<Tenant, TenantInfo>> Map() =>
        tenant => new TenantInfo(
            tenant.Id,
            tenant.Name,
            tenant.Description,
            tenant.Location,
            tenant.Domains,
            tenant.ContactPerson,
            tenant.ContactEmail,
            tenant.AutoUpdate,
            tenant.CreatedAtUtc,
            tenant.UpdatedAtUtc,
            tenant.AutoUpdateChannel,
            tenant.AutoUpdateTargetVersion,
            tenant.Version);

    private static string NormalizeChannel(string? channel) =>
        string.Equals(channel, "prerelease", StringComparison.OrdinalIgnoreCase) ? "prerelease" : "stable";

    private static string? NormalizeTarget(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        if (!Regex.IsMatch(version, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Auto-update target version must be normalized semantic version.");
        return version;
    }
}
