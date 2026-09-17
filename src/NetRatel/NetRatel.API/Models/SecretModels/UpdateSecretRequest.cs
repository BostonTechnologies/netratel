namespace NetRatel.API.Models.SecretModels;

public sealed record UpdateSecretRequest(
    string Value,
    string? Description);
