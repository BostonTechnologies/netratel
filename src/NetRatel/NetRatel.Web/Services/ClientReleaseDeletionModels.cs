namespace NetRatel.Web.Services;

public sealed class ClientReleaseDeletionPlanModel
{
    public string Version { get; set; } = string.Empty;
    public string[] RuntimeIds { get; set; } = [];
    public Guid[] ImportIds { get; set; } = [];
    public Guid[] ReleaseIds { get; set; } = [];
    public long ReclaimableBytes { get; set; }
    public string[] Blockers { get; set; } = [];
    public int HistoricalAttempts { get; set; }
    public Guid[] UploadStagingIds { get; set; } = [];
}
public sealed class ClientReleaseDeletionStatusModel
{
    public Guid Id { get; set; }
    public string Version { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool Suppressed { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? Error { get; set; }
}
public sealed class ClientReleaseSourceStatusModel
{
    public string State { get; set; } = "Unchecked";
    public DateTimeOffset CheckedAtUtc { get; set; }
    public string Message { get; set; } = string.Empty;
}
