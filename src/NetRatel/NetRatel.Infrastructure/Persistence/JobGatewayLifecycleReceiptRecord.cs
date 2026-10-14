using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>Committed native lifecycle evidence used to acknowledge an exact retry without dispatching again.</summary>
public sealed class JobGatewayLifecycleReceiptRecord
{
    public long JobRunId { get; set; }
    public long JobStepRunId { get; set; }
    public long LifecycleSequence { get; set; }
    public long Version { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

public static class JobGatewayLifecycleReceiptModelConfiguration
{
    public static void ConfigureJobGatewayLifecycleReceiptModel(this ModelBuilder model) =>
        model.Entity<JobGatewayLifecycleReceiptRecord>(entity =>
        {
            entity.ToTable("JobGatewayLifecycleReceipts");
            entity.HasKey(x => new { x.JobRunId, x.JobStepRunId, x.LifecycleSequence });
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            entity.HasOne<JobRunRecord>().WithMany().HasForeignKey(x => x.JobRunId).OnDelete(DeleteBehavior.Cascade);
        });
}
