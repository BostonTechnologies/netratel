namespace NetRatel.Infrastructure.Persistence;

public sealed class ClientInstallGrant
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string ProtectedToken { get; set; } = string.Empty;
    public string ProtectedScript { get; set; } = string.Empty;
    public string RequestKey { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public Guid EnrollmentCodeId { get; set; }
    public EnrollmentCode EnrollmentCode { get; set; } = null!;
    public int TenantId { get; set; }
    public string RuntimeId { get; set; } = string.Empty;
    public string ArtifactVersion { get; set; } = string.Empty;
    public string ArtifactSha256 { get; set; } = string.Empty;
    public bool InstallAsService { get; set; }
    public bool SilentInstall { get; set; }
    public string PublicWebBaseUrl { get; set; } = string.Empty;
    public string PublicApiBaseUrl { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int MaxUses { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
}
