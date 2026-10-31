using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

public sealed class SoftwareInventoryPolicyRecord
{
    public int TenantId { get; set; }
    public bool Enabled { get; set; } = true;
    public int Interval { get; set; } = 7;
    public string Unit { get; set; } = "days";
}

public sealed class SoftwareInventoryStateRecord
{
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid? LatestScanId { get; set; }
    public DateTimeOffset? LastSuccessfulScanUtc { get; set; }
    public DateTimeOffset? NextDueUtc { get; set; }
}

public sealed class SoftwareInventoryScanRecord
{
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid CollectionId { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? DetectedAtUtc { get; set; }
    public int TotalChunks { get; set; }
    public int ReceivedChunks { get; set; }
    public bool Complete { get; set; }
    public string Os { get; set; } = "";
    public string ErrorsJson { get; set; } = "[]";
}

public sealed class SoftwareInventoryChunkRecord
{
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid CollectionId { get; set; }
    public int Index { get; set; }
    public string Hash { get; set; } = "";
    public string PackagesJson { get; set; } = "[]";
}

public sealed class SoftwareInstallationRecord
{
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public string Identity { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Scope { get; set; } = "machine";
    public string? InstallLocation { get; set; }
    public string? InstallDate { get; set; }
    public string Os { get; set; } = "";
    public DateTimeOffset DetectedAtUtc { get; set; }
}

public sealed class SoftwareInventoryChangeRecord
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid CollectionId { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? PreviousVersion { get; set; }
    public string? Version { get; set; }
    public DateTimeOffset DetectedAtUtc { get; set; }
}

public static class SoftwareInventoryPersistenceModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<SoftwareInventoryPolicyRecord>(entity =>
        {
            entity.ToTable("SoftwareInventoryPolicies");
            entity.HasKey(row => row.TenantId);
            entity.Property(row => row.Unit).HasMaxLength(16);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(row => row.TenantId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<SoftwareInventoryStateRecord>(entity =>
        {
            entity.ToTable("SoftwareInventoryStates");
            entity.HasKey(row => new { row.TenantId, row.AgentId });
            entity.HasIndex(row => new { row.TenantId, row.NextDueUtc });
            entity.HasOne<Agent>().WithMany().HasForeignKey(row => row.AgentId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<SoftwareInventoryScanRecord>(entity =>
        {
            entity.ToTable("SoftwareInventoryScans");
            entity.HasKey(row => new { row.TenantId, row.AgentId, row.CollectionId });
            entity.Property(row => row.Status).HasMaxLength(16);
            entity.Property(row => row.Os).HasMaxLength(32);
            entity.HasIndex(row => new { row.TenantId, row.AgentId, row.RequestedAtUtc });
            entity.HasOne<SoftwareInventoryStateRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.AgentId }).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<SoftwareInventoryChunkRecord>(entity =>
        {
            entity.ToTable("SoftwareInventoryChunks");
            entity.HasKey(row => new { row.TenantId, row.AgentId, row.CollectionId, row.Index });
            entity.Property(row => row.Hash).HasMaxLength(64);
            entity.HasOne<SoftwareInventoryScanRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.AgentId, row.CollectionId }).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<SoftwareInstallationRecord>(entity =>
        {
            entity.ToTable("SoftwareInstallations");
            entity.HasKey(row => new { row.TenantId, row.AgentId, row.Identity });
            entity.Property(row => row.Identity).HasMaxLength(64);
            entity.Property(row => row.Name).HasMaxLength(512);
            entity.Property(row => row.Version).HasMaxLength(256);
            entity.Property(row => row.Publisher).HasMaxLength(512);
            entity.Property(row => row.Source).HasMaxLength(64);
            entity.Property(row => row.Os).HasMaxLength(32);
            entity.HasIndex(row => new { row.TenantId, row.Name, row.Version });
            entity.HasIndex(row => new { row.TenantId, row.Source, row.Os });
            entity.HasIndex(row => new { row.TenantId, row.Publisher });
            entity.HasOne<SoftwareInventoryStateRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.AgentId }).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<SoftwareInventoryChangeRecord>(entity =>
        {
            entity.ToTable("SoftwareInventoryChanges");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Kind).HasMaxLength(16);
            entity.HasIndex(row => new { row.TenantId, row.AgentId, row.Id });
            entity.HasOne<SoftwareInventoryScanRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.AgentId, row.CollectionId }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
