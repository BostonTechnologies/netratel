namespace NetRatel.Application.Tenants;

public sealed record TenantInfo(
    int TenantId,
    string Name,
    string? Description,
    string? Location,
    IReadOnlyList<string> Domains,
    string? ContactPerson,
    string? ContactEmail,
    bool AutoUpdate,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string AutoUpdateChannel = "stable",
    string? AutoUpdateTargetVersion = null,
    long Version = 1);

public sealed record CreateTenantCommand(
    string Name,
    string? Description,
    string? Location,
    IReadOnlyList<string> Domains,
    string? ContactPerson,
    string? ContactEmail,
    bool AutoUpdate,
    string AutoUpdateChannel = "stable",
    string? AutoUpdateTargetVersion = null);

/// <summary>Raised when a caller attempts to replace or delete a stale tenant revision.</summary>
public sealed class TenantConcurrencyException : InvalidOperationException
{
    public TenantConcurrencyException() : base("The tenant revision no longer matches the current persisted version.")
    {
    }
}

public sealed record UpdateTenantCommand(
    int TenantId,
    string Name,
    string? Description,
    string? Location,
    IReadOnlyList<string> Domains,
    string? ContactPerson,
    string? ContactEmail,
    bool AutoUpdate,
    string AutoUpdateChannel = "stable",
    string? AutoUpdateTargetVersion = null,
    long? ExpectedVersion = null);

public interface ITenantService
{
    Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken ct = default);
    Task<TenantInfo?> GetAsync(int tenantId, CancellationToken ct = default);
    Task<bool> ExistsAsync(int tenantId, CancellationToken ct = default);
    Task<TenantInfo> CreateAsync(CreateTenantCommand command, CancellationToken ct = default);
    Task<TenantInfo?> UpdateAsync(UpdateTenantCommand command, CancellationToken ct = default);
    Task<TenantInfo?> DeleteAsync(int tenantId, CancellationToken ct = default);
    Task<TenantInfo?> DeleteAsync(int tenantId, long expectedVersion, CancellationToken ct = default);
}
