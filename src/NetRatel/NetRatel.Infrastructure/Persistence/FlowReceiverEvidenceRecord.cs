using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

public sealed class FlowReceiverEvidenceRecord
{
    public int TenantId { get; set; }
    public Guid RunId { get; set; }
    public Guid NodeId { get; set; }
    public int SchemaVersion { get; set; } = 2;
    public string PreparationJson { get; set; } = "";
    public string EvidenceFingerprint { get; set; } = "";
    public string ReceiverIdempotencyKey { get; set; } = "";
    public string ReceiverFingerprint { get; set; } = "";
    public DateTimeOffset OriginalCreatedAtUtc { get; set; }
    public DateTimeOffset AutomaticReplayUntilUtc { get; set; }
    // Monotonic until a verified original receipt is durably recorded.
    public bool MayHaveCommitted { get; set; }
    public DateTimeOffset? FirstPostAttemptAtUtc { get; set; }
    public long? LastPostLeaseFence { get; set; }
    // A final ambiguous fifth POST receives at most one subsequent automatic read-only turn.
    // Consumed durably before lookup; a crash cannot create unbounded reconciliation retries.
    public bool FinalReconciliationAttempted { get; set; }
    public string? FullReceiptJson { get; set; }
    public long RowVersion { get; set; } = 1;
}

public static class FlowReceiverEvidenceModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<FlowReceiverEvidenceRecord>(entity =>
        {
            entity.ToTable("FlowReceiverEvidence", table =>
            {
                table.HasCheckConstraint("CK_FlowReceiverEvidence_V2",
                    """
                    "SchemaVersion" = 2 AND "TenantId" > 0 AND "RowVersion" > 0 AND jsonb_typeof("PreparationJson") = 'object' AND "AutomaticReplayUntilUtc" > "OriginalCreatedAtUtc" AND "AutomaticReplayUntilUtc" <= "OriginalCreatedAtUtc" + INTERVAL '24 hours' AND ("LastPostLeaseFence" IS NULL OR "LastPostLeaseFence" > 0) AND (NOT "MayHaveCommitted" OR "FirstPostAttemptAtUtc" IS NOT NULL) AND ("FullReceiptJson" IS NULL OR jsonb_typeof("FullReceiptJson") = 'object')
                    """);
            });
            entity.HasKey(x => new { x.RunId, x.NodeId });
            entity.HasIndex(x => new { x.TenantId, x.RunId, x.NodeId }).IsUnique();
            entity.Property(x => x.PreparationJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.FullReceiptJson).HasColumnType("jsonb");
            entity.Property(x => x.EvidenceFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ReceiverFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ReceiverIdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(x => x.RowVersion).IsConcurrencyToken();
            entity.HasOne<FlowActionRecord>().WithOne().HasForeignKey<FlowReceiverEvidenceRecord>(
                x => new { x.TenantId, x.RunId, x.NodeId }).HasPrincipalKey<FlowActionRecord>(
                x => new { x.TenantId, x.RunId, x.NodeId }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
