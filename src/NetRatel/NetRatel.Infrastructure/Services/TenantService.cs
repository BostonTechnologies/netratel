using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;

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
        var channel = NormalizeChannel(command.AutoUpdateChannel);
        var target = NormalizeTarget(command.AutoUpdateTargetVersion);
        ValidateNewPin(channel, target);

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
            AutoUpdateChannel = channel,
            AutoUpdateTargetVersion = target,
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

        var channel = NormalizeChannel(command.AutoUpdateChannel);
        var target = NormalizeTarget(command.AutoUpdateTargetVersion);
        // Retain legacy excluded pins during unrelated edits. Changing either
        // part of the policy must produce an eligible channel/pin combination.
        if (!string.Equals(target, tenant.AutoUpdateTargetVersion, StringComparison.Ordinal) ||
            !string.Equals(channel, tenant.AutoUpdateChannel, StringComparison.Ordinal))
            ValidateNewPin(channel, target);

        tenant.Name = normalizedName;
        tenant.Description = NormalizeNullable(command.Description);
        tenant.Location = NormalizeNullable(command.Location);
        tenant.Domains = NormalizeDomains(command.Domains);
        tenant.ContactPerson = NormalizeNullable(command.ContactPerson);
        tenant.ContactEmail = NormalizeNullable(command.ContactEmail);
        tenant.AutoUpdate = command.AutoUpdate;
        tenant.AutoUpdateChannel = channel;
        tenant.AutoUpdateTargetVersion = target;
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
        if (TenantUpdatePolicyValidation.TargetVersionError(version) is { } error)
            throw new InvalidOperationException(error);
        return version;
    }

    private static void ValidateNewPin(string channel, string? target)
    {
        if (!TenantUpdatePolicyValidation.IsTargetInChannel(channel, target))
            throw new InvalidOperationException("A prerelease target requires the prerelease channel. Select that channel or clear the pin.");
    }
}
