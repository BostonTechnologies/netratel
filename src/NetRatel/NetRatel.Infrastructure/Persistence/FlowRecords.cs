using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Persistence;

public sealed class FlowDefinitionRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public string Name { get; set; } = "";
    public long Revision { get; set; }
    public bool Enabled { get; set; }
    public string DraftJson { get; set; } = "";
    public Guid? PublishedVersionId { get; set; }
    public int PublishedVersionNumber { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
public sealed class FlowVersionRecord
{
    public Guid Id { get; init; }
    public Guid FlowId { get; init; }
    public int TenantId { get; init; }
    public int VersionNumber { get; init; }
    public string GraphJson { get; init; } = "";
    public string ConfigurationHash { get; init; } = "";
    public string PublishedBy { get; init; } = "";
    public DateTimeOffset PublishedAtUtc { get; init; }
}
public sealed class FlowRunRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid FlowId { get; set; }
    public Guid FlowVersionId { get; set; }
    public Guid EventId { get; set; }
    public Guid OccurrenceId { get; set; }
    public string EventJson { get; set; } = "";
    public string EventFingerprint { get; set; } = "";
    public FlowRunStatus Status { get; set; }
    public long Fence { get; set; }
    public Guid? LeaseToken { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public string? Code { get; set; }
}
public sealed class FlowActionRecord
{
    public Guid RunId { get; set; }
    public Guid NodeId { get; set; }
    public int TenantId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public FlowActionStatus Status { get; set; }
    public string DraftJson { get; set; } = "";
    public string? PreparedJson { get; set; }
    public long? ConnectorRevision { get; set; }
    public string? SemanticFingerprint { get; set; }
    public string? ReceiptJson { get; set; }
    public long LeaseFence { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public string? Code { get; set; }
}
public sealed class FlowAuditRecord
{
    public Guid Id { get; init; }
    public int TenantId { get; init; }
    public Guid FlowId { get; init; }
    public long Revision { get; init; }
    public string ActorId { get; init; } = "";
    public string Operation { get; init; } = "";
    public DateTimeOffset AtUtc { get; init; }
}
public sealed class FlowRuntimeIdentityRecord
{
    public int Id { get; init; } = 1;
    public Guid SourceInstanceId { get; init; }
}
