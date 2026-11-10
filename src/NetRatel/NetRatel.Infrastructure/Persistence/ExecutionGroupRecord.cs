using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

public sealed class ExecutionGroupRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public long Revision { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool Archived { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public List<ExecutionGroupMemberRecord> Members { get; set; } = [];
}

public sealed class ExecutionGroupMemberRecord
{
    public Guid GroupId { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public ExecutionGroupRecord Group { get; set; } = null!;
}

/// <summary>Retained independently of deleted groups; contains identities and revisions, never credentials.</summary>
public sealed class ExecutionGroupAuditRecord
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public int TenantId { get; set; }
    public string? Actor { get; set; }
    public string Operation { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string MembershipJson { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public static class ExecutionGroupModelConfiguration
{
    public static void ConfigureExecutionGroupModel(this ModelBuilder model)
    {
        // Tenant identity remains mutable for an unreferenced Agent. PostgreSQL composite
        // constraints enforce retained membership/node tenant integrity without making
        // TenantId an EF key (which would freeze every Agent, including legacy clients).
        model.Entity<Agent>().HasIndex(x => new { x.TenantId, x.Id }).IsUnique();
        model.Entity<JobRunNodeRecord>().HasIndex(x => new { x.TenantId, x.AgentId });
        model.Entity<JobRunNodeRecord>().HasOne<Agent>().WithMany()
            .HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ExecutionGroupRecord>(entity =>
        {
            entity.ToTable("ExecutionGroups", table => table.HasCheckConstraint("CK_ExecutionGroups_Revision", "\"Revision\" > 0"));
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(2048);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.Property(x => x.CreatedBy).HasMaxLength(256);
            entity.Property(x => x.UpdatedBy).HasMaxLength(256);
            entity.HasIndex(x => new { x.TenantId, x.NormalizedName }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Archived, x.Name });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<ExecutionGroupMemberRecord>(entity =>
        {
            entity.ToTable("ExecutionGroupMembers");
            entity.HasKey(x => new { x.GroupId, x.AgentId });
            entity.HasOne(x => x.Group).WithMany(x => x.Members).HasForeignKey(x => new { x.TenantId, x.GroupId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.AgentId });
        });
        model.Entity<ExecutionGroupAuditRecord>(entity =>
        {
            entity.ToTable("ExecutionGroupAudits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Actor).HasMaxLength(256);
            entity.Property(x => x.Operation).HasMaxLength(32);
            entity.Property(x => x.MembershipJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.TenantId, x.GroupId, x.CreatedAtUtc });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
