using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>One policy's share of a whole reviewed plan, committed with its confirmation admission.</summary>
public sealed class McpJobTargetReservationRecord
{
    public Guid IdempotencyId { get; set; }
    public Guid PolicyId { get; set; }
    public int TargetCount { get; set; }
    public string TargetPlanDigest { get; set; } = string.Empty;
}

public static class McpJobTargetReservationModel
{
    public static void ConfigureMcpJobTargetReservations(this ModelBuilder model)
    {
        model.Entity<McpReviewedJobAdmissionRecord>(entity =>
        {
            entity.ToTable("McpReviewedJobAdmissions");
            entity.HasKey(row => row.IdempotencyId);
            entity.HasIndex(row => new { row.TenantId, row.JobId });
            entity.Property(row => row.ProtectedSubmissionJson).IsRequired();
            entity.HasOne<McpOperatorAcceptedAuditRecord>().WithMany().HasForeignKey(row => row.AcceptedAuditId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorIdempotencyRecord>().WithMany().HasForeignKey(row => row.IdempotencyId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<McpJobTargetReservationRecord>(entity =>
        {
            entity.ToTable("McpJobTargetReservations", table => table.HasCheckConstraint(
                "CK_McpJobTargetReservations_Count", "\"TargetCount\" > 0 AND \"TargetCount\" <= 10000"));
            entity.HasKey(row => new { row.IdempotencyId, row.PolicyId });
            entity.HasIndex(row => row.PolicyId);
            entity.Property(row => row.TargetPlanDigest).HasMaxLength(64).IsRequired();
            entity.HasOne<McpOperatorIdempotencyRecord>().WithMany().HasForeignKey(row => row.IdempotencyId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<McpOperatorPolicyRecord>().WithMany().HasForeignKey(row => row.PolicyId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Protected original plan and execution material, durable before a parent or physical work exists.</summary>
public sealed class McpReviewedJobAdmissionRecord
{
    public Guid AcceptedAuditId { get; set; }
    public int TenantId { get; set; }
    public long JobId { get; set; }
    public Guid IdempotencyId { get; set; }
    public string ProtectedSubmissionJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
