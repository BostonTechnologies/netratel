using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed class AiPromptRecord
{
    public int TenantId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Body { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public bool Archived { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string UpdatedBy { get; set; } = string.Empty;
}

public sealed class AiPromptRevisionRecord
{
    public int TenantId { get; set; }
    public Guid PromptId { get; set; }
    public long Revision { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class AiPromptDefaultRecord
{
    public int TenantId { get; set; }
    public Guid? PromptId { get; set; }
    public long Revision { get; set; }
}

public static class AiPromptPersistenceModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<AiPromptRecord>(entity =>
        {
            entity.ToTable("AiPrompts"); entity.HasKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Id).ValueGeneratedNever(); entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.Property(x => x.Name).HasMaxLength(AiPromptLimits.MaxNameLength).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(AiPromptLimits.MaxDescriptionLength);
            entity.Property(x => x.Body).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(256); entity.Property(x => x.UpdatedBy).HasMaxLength(256);
            entity.HasIndex(x => new { x.TenantId, x.Archived, x.Name });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<AiPromptRevisionRecord>(entity =>
        {
            entity.ToTable("AiPromptRevisions"); entity.HasKey(x => new { x.TenantId, x.PromptId, x.Revision });
            entity.Property(x => x.Name).HasMaxLength(AiPromptLimits.MaxNameLength).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(AiPromptLimits.MaxDescriptionLength);
            entity.Property(x => x.Body).IsRequired(); entity.Property(x => x.CreatedBy).HasMaxLength(256);
            entity.HasOne<AiPromptRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.PromptId }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<AiPromptDefaultRecord>(entity =>
        {
            entity.ToTable("AiPromptDefaults"); entity.HasKey(x => x.TenantId);
            entity.Property(x => x.TenantId).ValueGeneratedNever(); entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AiPromptRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.PromptId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
