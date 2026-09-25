namespace NetRatel.Infrastructure.Persistence;

public enum ClientReleaseImportState : short
{
    Queued = 0,
    Resolving = 1,
    Downloading = 2,
    Verifying = 3,
    Importing = 4,
    Imported = 5,
    Failed = 6,
    Cancelled = 7
}

public enum ClientReleaseImportAssetState : short
{
    Pending = 0,
    Downloading = 1,
    Verified = 2,
    Normalized = 3,
    Imported = 4,
    Failed = 5
}

public sealed class ClientReleaseImportOperation
{
    public Guid Id { get; set; }
    public long GitHubReleaseId { get; set; }
    public string SourceRepository { get; set; } = "BostonTechnologies/netratel";
    public string Tag { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? BuildCommit { get; set; }
    public string? PublicationSha256 { get; set; }
    public ClientReleaseImportState State { get; set; } = ClientReleaseImportState.Queued;
    public string RequestedBy { get; set; } = string.Empty;
    public bool IsAutomatic { get; set; }
    public DateTimeOffset? AutomaticPublishAttemptAtUtc { get; set; }
    public string? AutomaticPublishError { get; set; }
    public bool CancellationRequested { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ImportedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public string? PublishedBy { get; set; }
    public long? TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public int AttemptCount { get; set; }
    public string? Error { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public long LeaseGeneration { get; set; }
    public List<ClientReleaseImportAsset> Assets { get; set; } = [];
}

public sealed class ClientReleaseImportAsset
{
    public Guid OperationId { get; set; }
    public ClientReleaseImportOperation Operation { get; set; } = null!;
    public string RuntimeId { get; set; } = string.Empty;
    public long GitHubAssetId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public long SourceSizeBytes { get; set; }
    public string? LocalSha256 { get; set; }
    public long? LocalSizeBytes { get; set; }
    public string? ConversionContract { get; set; }
    public ClientReleaseImportAssetState State { get; set; } = ClientReleaseImportAssetState.Pending;
    public long DownloadedBytes { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
