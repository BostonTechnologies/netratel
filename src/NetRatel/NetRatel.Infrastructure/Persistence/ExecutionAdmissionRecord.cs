using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>Retained independently of flow history so pruning cannot turn an accepted action key into a fresh execution.</summary>
public sealed class ExecutionAdmissionRecord
{
    public Guid ActionKey { get; set; }
    public int TenantId { get; set; }
    public string PrincipalId { get; set; } = string.Empty;
    public string? IntegrationCredentialId { get; set; }
    public Guid FlowVersionId { get; set; }
    public Guid FlowRunId { get; set; }
    public Guid NodeId { get; set; }
    public int NodeAttempt { get; set; }
    public string? CausationJson { get; set; }
    public DateTimeOffset? AuthorityDeniedAtUtc { get; set; }
    public int Kind { get; set; }
    public string ExecutionId { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public string ProtectedPayloadJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal static class ExecutionAdmissionPersistenceModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExecutionAdmissionRecord>(entity =>
        {
            entity.ToTable("ExecutionAdmissions");
            entity.HasKey(record => record.ActionKey);
            entity.Property(record => record.ActionKey).ValueGeneratedNever();
            entity.Property(record => record.PrincipalId).HasMaxLength(256).IsRequired();
            entity.Property(record => record.IntegrationCredentialId).HasMaxLength(256);
            entity.Property(record => record.ExecutionId).HasMaxLength(128).IsRequired();
            entity.Property(record => record.PayloadHash).HasMaxLength(64).IsRequired();
            entity.Property(record => record.ProtectedPayloadJson).IsRequired();
            entity.HasIndex(record => new { record.Kind, record.ExecutionId }).IsUnique();
            entity.HasIndex(record => new { record.TenantId, record.FlowRunId, record.NodeId, record.NodeAttempt }).IsUnique();
        });
    }
}
