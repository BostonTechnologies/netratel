namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Schema-only durable lifecycle record. RS2-3A's RemoteSupportSessionActor is
/// the only future writer; this phase intentionally registers no writer.
/// </summary>
public sealed class RemoteSupportSessionRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid OpenRequestId { get; set; }
    public int ContractVersion { get; set; }
    public string InitiatingOperatorId { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public int? TargetWindowsSessionId { get; set; }
    public string? TargetUserSidHash { get; set; }
    public decimal? TargetInventorySequence { get; set; }
    public string RequestedCapabilitiesJson { get; set; } = "[]";
    public string GrantedCapabilitiesJson { get; set; } = "[]";
    public string State { get; set; } = "requested";
    public decimal LifecycleRevision { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset? TerminalAtUtc { get; set; }
    public string? TerminalReasonCode { get; set; }
}
