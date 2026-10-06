namespace NetRatel.Infrastructure.Persistence;

public sealed class ClientConnectionEpochRecord
{
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public long LastIssuedEpoch { get; set; }
    public DateTimeOffset? CancellationBarrierUntilUtc { get; set; }
}
