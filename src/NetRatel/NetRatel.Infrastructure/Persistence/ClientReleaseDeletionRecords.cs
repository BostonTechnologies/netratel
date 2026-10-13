namespace NetRatel.Infrastructure.Persistence;

/// <summary>Minimal durable cleanup journal and automatic-import suppression; contains no package bytes.</summary>
public sealed class ClientReleaseDeletionReceipt
{
    public Guid Id { get; set; }
    public string Version { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public string State { get; set; } = "Pending";
    public string PlanJson { get; set; } = "{}";
    public string PreviousReceiptsJson { get; set; } = "[]";
    public bool Suppressed { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public string? Error { get; set; }
}

public sealed class ClientArtifactDownloadLease
{
    public Guid Id { get; set; }
    public string RuntimeId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
