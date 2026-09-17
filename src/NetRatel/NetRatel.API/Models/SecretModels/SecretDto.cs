namespace NetRatel.API.Models.SecretModels;

public sealed record SecretDto(
    int Id,
    int? TenantId,
    string? ClientIdentity,
    string Value,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
