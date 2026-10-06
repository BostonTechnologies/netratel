namespace NetRatel.Infrastructure.Persistence;

/// <summary>One bounded offline cache and durable ingress fence per authenticated client.</summary>
public sealed class ClientServicesSnapshotRecord
{
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public long ConnectionEpoch { get; set; }
    public ulong LastAcceptedSequence { get; set; }
    public long Revision { get; set; }
    public string StateJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
