namespace NetRatel.Infrastructure.Persistence;

/// <summary>Instance-wide, opt-in policy and durable scheduler cursor.</summary>
public sealed class ClientReleaseAutomationSettings
{
    public int Id { get; set; } = 1;
    public int CheckEveryHours { get; set; }
    public bool DownloadStable { get; set; }
    public bool DownloadPrerelease { get; set; }
    public bool PublishAutomatically { get; set; }
    public bool DeployPrereleaseAutomatically { get; set; }
    public DateTimeOffset? NextCheckAtUtc { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? LastSuccessAtUtc { get; set; }
    public string? LastError { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public long Revision { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
