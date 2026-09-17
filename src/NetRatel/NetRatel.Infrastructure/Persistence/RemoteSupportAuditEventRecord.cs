namespace NetRatel.Infrastructure.Persistence;

/// <summary>Append-only redacted audit record. Signalling, SDP, ICE, media, and credentials are prohibited.</summary>
public sealed class RemoteSupportAuditEventRecord
{
    public Guid Id { get; set; }
    public Guid RemoteSupportSessionId { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public int ContractVersion { get; set; }
    public decimal AuditSequence { get; set; }
    public decimal LifecycleRevision { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string ActorKind { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public Guid? RequestId { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? FailureCode { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}
