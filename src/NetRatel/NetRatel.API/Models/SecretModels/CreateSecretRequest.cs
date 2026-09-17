namespace NetRatel.API.Models.SecretModels;

public sealed record CreateSecretRequest(
    int? TenantId,
    string? ClientIdentity,
    string Value,
    string? Description);
