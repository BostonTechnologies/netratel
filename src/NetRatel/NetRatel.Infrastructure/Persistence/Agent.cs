namespace NetRatel.Infrastructure.Persistence;

public enum AgentStatus : short
{
    Active = 0,
    Disabled = 1
}

public sealed class Agent
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public string? Name { get; set; }
    public AgentStatus Status { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? DisabledReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset? LastTokenIssuedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
    public string? PublicKey { get; set; }
    public string? PublicKeyFingerprint { get; set; }
    public string KeyAlgorithm { get; set; } = "ecdsa-p256";
    public DateTimeOffset? KeyRegisteredAtUtc { get; set; }
    public string? AllowedScopesJson { get; set; }
    public string? DeviceInfoJson { get; set; }
    public string? MtlsThumbprint { get; set; }
    public Guid? SupersededByAgentId { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }

    public List<AgentCredential> Credentials { get; set; } = new();
    public List<AgentRefreshToken> RefreshTokens { get; set; } = new();
}
