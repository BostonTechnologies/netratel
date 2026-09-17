namespace NetRatel.Infrastructure.Persistence;

public enum ClientUpdateAttemptState : short
{
    Claimed = 0,
    Downloading = 1,
    Staged = 2,
    Activating = 3,
    GatewayReadmitted = 4,
    Accepted = 5,
    FailedPreActivation = 6,
    RolledBack = 7,
    RollbackUnverified = 8
}

public sealed class ClientUpdateReleaseRecord
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public long Revision { get; set; }
    public string RuntimeId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Channel { get; set; } = "stable";
    public string ArtifactKey { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ManifestJson { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset PublishedAtUtc { get; set; }
    public string? PublishedBy { get; set; }
    public DateTimeOffset? DisabledAtUtc { get; set; }
    public string? DisabledBy { get; set; }
    public List<ClientUpdateAttemptRecord> Attempts { get; set; } = [];
}

public sealed class ClientUpdateAttemptRecord
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int ReleaseId { get; set; }
    public ClientUpdateReleaseRecord Release { get; set; } = null!;
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public string FromVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public string RuntimeId { get; set; } = string.Empty;
    public ClientUpdateAttemptState State { get; set; }
    public string AdmissionNonceHash { get; set; } = string.Empty;
    public Guid? GatewayConnectionId { get; set; }
    public long? GatewayConnectionEpoch { get; set; }
    public Guid? ConfirmationId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ReadmittedAtUtc { get; set; }
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public string? FailureCode { get; set; }
    public string? Message { get; set; }
}

public sealed class AgentClientUpdateStateRecord
{
    public Guid AgentId { get; set; }
    public int TenantId { get; set; }
    public DateTimeOffset? SuspendedAtUtc { get; set; }
    public string? SuspensionReason { get; set; }
    public Guid? SuspensionAttemptId { get; set; }
    public Guid? SuppressedReleaseId { get; set; }
    public long PolicyRevision { get; set; }
    public DateTimeOffset? ResumedAtUtc { get; set; }
    public string? ResumedBy { get; set; }
}

public sealed class ClientUpdateCatalogRevision
{
    public int Id { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
