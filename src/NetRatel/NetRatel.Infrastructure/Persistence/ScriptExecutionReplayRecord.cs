using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>Trusted native replay metadata and encrypted immutable material; never cascades with mutable script definitions.</summary>
public sealed class ScriptExecutionReplayRecord
{
    public string RequestId { get; set; } = string.Empty;
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public long ScriptId { get; set; }
    public string ScriptName { get; set; } = string.Empty;
    public long ScriptRevision { get; set; }
    public string ScriptContentHash { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public string ProtectedSnapshotJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal static class ScriptExecutionReplayPersistenceModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScriptExecutionReplayRecord>(entity =>
        {
            entity.ToTable("ScriptExecutionReplays");
            entity.HasKey(record => record.RequestId);
            entity.Property(record => record.RequestId).HasMaxLength(256).ValueGeneratedNever();
            entity.Property(record => record.ScriptName).HasMaxLength(512).IsRequired();
            entity.Property(record => record.ScriptContentHash).HasMaxLength(64).IsRequired();
            entity.Property(record => record.PayloadHash).HasMaxLength(64).IsRequired();
            entity.Property(record => record.ProtectedSnapshotJson).IsRequired();
            entity.HasIndex(record => new { record.TenantId, record.AgentId, record.CreatedAtUtc });
        });
    }
}
