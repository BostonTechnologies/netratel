using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>Search-safe associations from trusted admissions and verified receipts, never arbitrary result JSON.</summary>
public sealed class ExternalResourceReferenceRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public string PeerInstanceId { get; set; } = "";
    public string PeerOrganizationId { get; set; } = "";
    public string ReferenceKind { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Reference { get; set; } = "";
    public string NormalizedReference { get; set; } = "";
    public string? ExternalUrl { get; set; }
    public string SafeTitle { get; set; } = "";
    public string ResourceKind { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public int? NativeRequestId { get; set; }
    public long? JobRunId { get; set; }
    public long? JobDefinitionId { get; set; }
    public Guid? FlowRunId { get; set; }
    public Guid? ActionId { get; set; }
    public string EvidenceKind { get; set; } = "";
}
public sealed class AutomationReferenceBackfillCheckpoint
{
    public int Id { get; set; } = 1;
    public int LastRequestId { get; set; }
    public bool AdmissionsCompleted { get; set; }
    public int ReceiptOffset { get; set; }
    public bool Completed { get; set; }
    public int UnavailableReceipts { get; set; }
    public int UnavailableAdmissions { get; set; }
}
public static class ExternalResourceReferenceModel
{
    public static void ConfigureExternalResourceReferences(this ModelBuilder model)
    {
        model.Entity<ExternalResourceReferenceRecord>(e =>
        {
            e.ToTable("ExternalResourceReferences"); e.HasKey(x => x.Id);
            e.Property(x => x.PeerInstanceId).HasMaxLength(36); e.Property(x => x.PeerOrganizationId).HasMaxLength(64);
            e.Property(x => x.ReferenceKind).HasMaxLength(32); e.Property(x => x.ExternalId).HasMaxLength(256);
            e.Property(x => x.Reference).HasMaxLength(128); e.Property(x => x.NormalizedReference).HasMaxLength(128).UseCollation("C");
            e.Property(x => x.ExternalUrl).HasMaxLength(2048); e.Property(x => x.SafeTitle).HasMaxLength(256);
            e.Property(x => x.ResourceKind).HasMaxLength(32); e.Property(x => x.ResourceId).HasMaxLength(256);
            e.Property(x => x.EvidenceKind).HasMaxLength(32);
            e.HasIndex(x => new { x.TenantId, x.NormalizedReference, x.ResourceKind, x.ResourceId });
            e.HasIndex(x => new { x.TenantId, x.ResourceKind, x.ResourceId });
            e.HasIndex(x => new { x.TenantId, x.PeerInstanceId, x.PeerOrganizationId, x.ReferenceKind, x.ExternalId });
        });
        model.Entity<AutomationReferenceBackfillCheckpoint>(e => { e.ToTable("AutomationReferenceBackfill"); e.HasKey(x => x.Id); });
    }
}
