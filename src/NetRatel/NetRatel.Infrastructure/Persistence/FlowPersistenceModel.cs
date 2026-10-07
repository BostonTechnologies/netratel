using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

public static class FlowPersistenceModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<FlowDefinitionRecord>(entity =>
        {
            entity.ToTable("FlowDefinitions"); entity.HasKey(row => row.Id);
            entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Name).HasMaxLength(128).IsRequired();
            entity.Property(row => row.DraftJson).HasColumnType("jsonb").IsRequired();
            entity.Property(row => row.Revision).IsConcurrencyToken(); entity.HasIndex(row => new { row.TenantId, row.UpdatedAtUtc });
        });
        builder.Entity<FlowVersionRecord>(entity =>
        {
            entity.ToTable("FlowVersions"); entity.HasKey(row => row.Id); entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.GraphJson).HasColumnType("jsonb").IsRequired();
            entity.Property(row => row.ConfigurationHash).HasMaxLength(64).IsRequired();
            entity.Property(row => row.PublishedBy).HasMaxLength(256).IsRequired();
            entity.HasIndex(row => new { row.TenantId, row.FlowId, row.VersionNumber }).IsUnique();
            entity.HasOne<FlowDefinitionRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.FlowId }).HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<FlowRunRecord>(entity =>
        {
            entity.ToTable("FlowRuns"); entity.HasKey(row => row.Id); entity.HasAlternateKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.EventJson).HasColumnType("jsonb").IsRequired();
            entity.Property(row => row.EventFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Status).HasConversion<int>(); entity.Property(row => row.Code).HasMaxLength(128);
            entity.Property(row => row.Fence).IsConcurrencyToken();
            entity.HasIndex(row => new { row.TenantId, row.EventId, row.FlowVersionId }).IsUnique();
            entity.HasIndex(row => new { row.Status, row.NextAttemptAtUtc, row.LeaseExpiresAtUtc });
            entity.HasIndex(row => new { row.TenantId, row.FlowId, row.CreatedAtUtc });
            entity.HasOne<FlowVersionRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.FlowVersionId }).HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<FlowActionRecord>(entity =>
        {
            entity.ToTable("FlowActions"); entity.HasKey(row => new { row.RunId, row.NodeId });
            entity.HasAlternateKey(row => new { row.TenantId, row.RunId, row.NodeId });
            entity.Property(row => row.IdempotencyKey).HasMaxLength(200).IsRequired(); entity.HasIndex(row => new { row.TenantId, row.IdempotencyKey }).IsUnique();
            entity.Property(row => row.DraftJson).HasColumnType("jsonb").IsRequired(); entity.Property(row => row.PreparedJson).HasColumnType("jsonb");
            entity.Property(row => row.SemanticFingerprint).HasMaxLength(64); entity.Property(row => row.ReceiptJson).HasColumnType("jsonb");
            entity.Property(row => row.Code).HasMaxLength(128); entity.Property(row => row.Status).HasConversion<int>();
            entity.HasOne<FlowRunRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.RunId }).HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<FlowAuditRecord>(entity =>
        {
            entity.ToTable("FlowAudits"); entity.HasKey(row => row.Id);
            entity.Property(row => row.ActorId).HasMaxLength(256).IsRequired(); entity.Property(row => row.Operation).HasMaxLength(64).IsRequired();
            entity.HasIndex(row => new { row.TenantId, row.FlowId, row.AtUtc });
        });
        builder.Entity<FlowRuntimeIdentityRecord>(entity => { entity.ToTable("FlowRuntimeIdentity"); entity.HasKey(row => row.Id); entity.Property(row => row.Id).ValueGeneratedNever(); });
    }
}
