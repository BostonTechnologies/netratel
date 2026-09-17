namespace NetRatel.Application.Agents;

public sealed class SecurityHardeningOptions
{
    public bool EnablePoP { get; set; }
    public bool EnableRefreshRotation { get; set; }
    public bool EnableMTls { get; set; }
    public bool RequireMTls { get; set; }
    public bool RequireScopes { get; set; }
    public int NonceRetentionMinutes { get; set; } = 10;
    public int PopTimestampToleranceMinutes { get; set; } = 5;
    public int RefreshRotationRecoveryMinutes { get; set; } = 2;
}
