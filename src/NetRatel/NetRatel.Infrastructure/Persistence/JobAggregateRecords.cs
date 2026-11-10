using Microsoft.EntityFrameworkCore;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>A durable slot/queue entry. Native identity is allocated and bound before physical dispatch.</summary>
public sealed class JobRunNodeRecord
{
    public long ParentRunId { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public int Ordinal { get; set; }
    public long? NativeRunId { get; set; }
    public JobRunNodeState State { get; set; }
    public string TargetJson { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTimeOffset? ClaimedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

/// <summary>Serializes slot claims with stop/cancel decisions; native controls remain single-agent.</summary>
public sealed class JobAggregateControlRecord
{
    public long ParentRunId { get; set; }
    public long Revision { get; set; } = 1;
    public bool DispatchClosed { get; set; }
    public string? AdmissionKey { get; set; }
    public string? PrincipalId { get; set; }
    public string? IntegrationCredentialId { get; set; }
    public string? SourceKind { get; set; }
    public string? GateReason { get; set; }
    public DateTimeOffset? CancellationRequestedAtUtc { get; set; }
    public DateTimeOffset? ActionDeadlineAtUtc { get; set; }
    public DateTimeOffset? TerminalReadyAtUtc { get; set; }
    public string? TerminalResultHash { get; set; }
}

public static class JobAggregateModelConfiguration
{
    public static void ConfigureJobAggregateModel(this ModelBuilder model)
    {
        model.Entity<JobDefinition>(entity =>
        {
            entity.Property(row => row.TargetJson).HasColumnType("text");
            entity.Property(row => row.ExecutionPolicyJson).HasColumnType("text");
        });
        model.Entity<JobRunRecord>(entity =>
        {
            entity.Property(row => row.TargetPlanJson).HasColumnType("text");
            entity.Property(row => row.TargetPlanDigest).HasMaxLength(64);
            entity.Property(row => row.DefinitionDigest).HasMaxLength(64);
            entity.HasIndex(row => row.ParentRunId);
            entity.HasIndex(row => new { row.Id, row.TenantId }).IsUnique();
            entity.HasOne<JobRunRecord>().WithMany().HasForeignKey(row => row.ParentRunId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable("JobRuns", table => table.HasCheckConstraint("CK_JobRuns_AggregateTarget",
                "NOT \"IsAggregate\" OR (\"AgentId\" IS NULL AND \"ParentRunId\" IS NULL AND \"TenantId\" IS NOT NULL)"));
        });
        model.Entity<JobRunNodeRecord>(entity =>
        {
            entity.ToTable("JobRunNodes", table =>
            {
                table.HasCheckConstraint("CK_JobRunNodes_Identity", "\"TenantId\" > 0 AND \"AgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"Ordinal\" > 0");
                table.HasCheckConstraint("CK_JobRunNodes_State", "\"State\" BETWEEN 0 AND 8");
            });
            entity.HasKey(row => new { row.ParentRunId, row.AgentId });
            entity.HasIndex(row => new { row.ParentRunId, row.Ordinal }).IsUnique();
            entity.HasIndex(row => row.NativeRunId).IsUnique();
            entity.HasIndex(row => new { row.ParentRunId, row.State });
            entity.Property(row => row.TargetJson).HasColumnType("text");
            entity.Property(row => row.Reason).HasMaxLength(256);
            entity.HasOne<JobRunRecord>().WithMany().HasForeignKey(row => row.ParentRunId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<JobRunRecord>().WithMany().HasForeignKey(row => row.NativeRunId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<JobAggregateControlRecord>(entity =>
        {
            entity.ToTable("JobAggregateControls");
            entity.HasKey(row => row.ParentRunId);
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.Property(row => row.AdmissionKey).HasMaxLength(256);
            entity.Property(row => row.PrincipalId).HasMaxLength(256);
            entity.Property(row => row.IntegrationCredentialId).HasMaxLength(256);
            entity.Property(row => row.SourceKind).HasMaxLength(32);
            entity.HasIndex(row => row.AdmissionKey).IsUnique();
            entity.Property(row => row.GateReason).HasMaxLength(256);
            entity.Property(row => row.TerminalResultHash).HasMaxLength(64);
            entity.HasOne<JobRunRecord>().WithOne().HasForeignKey<JobAggregateControlRecord>(row => row.ParentRunId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
