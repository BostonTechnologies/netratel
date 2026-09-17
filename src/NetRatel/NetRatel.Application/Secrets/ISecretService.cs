namespace NetRatel.Application.Secrets;

public sealed record SecretInfo(
    int Id,
    int? TenantId,
    string? ClientIdentity,
    string Value,
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
    Task<IReadOnlyList<SecretInfo>> ListAsync(CancellationToken ct = default);
    Task<SecretInfo?> GetAsync(int id, CancellationToken ct = default);
    Task<SecretInfo> CreateAsync(CreateSecretCommand command, CancellationToken ct = default);
    Task<SecretInfo?> UpdateAsync(UpdateSecretCommand command, CancellationToken ct = default);
    Task<SecretInfo?> DeleteAsync(int id, CancellationToken ct = default);
}
