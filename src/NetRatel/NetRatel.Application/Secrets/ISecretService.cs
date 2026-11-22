namespace NetRatel.Application.Secrets;

public sealed record SecretInfo(
    int Id,
    int? TenantId,
    string? ClientIdentity,
    string Value,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SecretMetadata(
    int Id,
    int? TenantId,
    string? ClientIdentity,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateSecretCommand(
    int? TenantId,
    string? ClientIdentity,
    string Value,
    string? Description);

public sealed record UpdateSecretCommand(
    int Id,
    string Value,
    string? Description);

public interface ISecretService
{
    Task<IReadOnlyList<SecretInfo>> ListAsync(int[]? authorizedTenantIds, CancellationToken ct = default);
    Task<SecretInfo?> GetAsync(int id, int[]? authorizedTenantIds, CancellationToken ct = default);
    Task<SecretMetadata> CreateAsync(CreateSecretCommand command, CancellationToken ct = default);
    Task<SecretMetadata?> UpdateAsync(UpdateSecretCommand command, int[]? authorizedTenantIds, CancellationToken ct = default);
    Task<SecretMetadata?> DeleteAsync(int id, int[]? authorizedTenantIds, CancellationToken ct = default);
}
