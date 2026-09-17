namespace NetRatel.Application.Agents;

public sealed class AgentAuthOptions
{
    public string Issuer { get; set; } = "https://netratel.example.invalid";
    public string Audience { get; set; } = "netratel-agent";
    public string[] LegacyAudiences { get; set; } = [];
    public int AccessTokenLifetimeMinutes { get; set; } = 15;
    public int RefreshTokenLifetimeDays { get; set; } = 365;
    public string SigningKeyId { get; set; } = "netratel-agent-es256";
    public string[] LegacySigningKeyIds { get; set; } = [];
    public string? PrivateKeyPath { get; set; }

    public string[] GetAcceptedAudiences() =>
        (LegacyAudiences ?? [])
            .Prepend(Audience)
            .Where(audience => !string.IsNullOrWhiteSpace(audience))
            .Select(audience => audience.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public string[] GetAcceptedSigningKeyIds() =>
        (LegacySigningKeyIds ?? [])
            .Prepend(SigningKeyId)
            .Where(keyId => !string.IsNullOrWhiteSpace(keyId))
            .Select(keyId => keyId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
