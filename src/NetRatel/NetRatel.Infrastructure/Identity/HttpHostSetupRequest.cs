namespace NetRatel.Infrastructure.Identity;

public enum HttpHostSetupState { Pending, Approved, Denied, Expired, Cancelled }

/// <summary>Only a generation digest is registered: deployment secrets never leave their protected local files.</summary>
public sealed class HttpHostSetupRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserCodeHash { get; set; } = string.Empty;
    public string DeviceCapabilityHash { get; set; } = string.Empty;
    public string PublicOrigin { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string ServicePrincipal { get; set; } = string.Empty;
    public string RegistrationFingerprint { get; set; } = string.Empty;
    public string? OwnerPrincipalId { get; set; }
    public HttpHostSetupState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset NextPollAtUtc { get; set; }
    public int PollIntervalSeconds { get; set; } = 5;
    public long Revision { get; set; }
}
