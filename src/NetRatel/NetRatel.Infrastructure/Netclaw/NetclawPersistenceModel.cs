using Microsoft.EntityFrameworkCore;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed class NetclawConnectionRecord
{
    public int TenantId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Revision { get; set; }
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string ProtectedDeviceToken { get; set; } = string.Empty;
    public bool AllowPrivateHttp { get; set; }
    public string ProfileFingerprint { get; set; } = string.Empty;
    public string OwnerPrincipalId { get; set; } = string.Empty;
    public DateTimeOffset? LastTestedAtUtc { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public string? LastTestCode { get; set; }
}

public sealed class AiInvestigationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int TenantId { get; set; }
    public Guid RequestId { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid[] AgentIds { get; set; } = [];
    public Guid? OccurrenceId { get; set; }
    public Guid? FlowRunId { get; set; }
    public string? SourceActionKey { get; set; }
    public string? RemoteSessionId { get; set; }
    public string? ProfileFingerprint { get; set; }
    public InvestigationState State { get; set; }
    public long LastSequence { get; set; }
    public Guid? ActiveMessageId { get; set; }
    public string? ActiveCredentialId { get; set; }
    public bool Automated { get; set; }
    public DateTimeOffset LastActivityUtc { get; set; }
    public DateTimeOffset? LastTransportActivityAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class AiInvestigationEventRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvestigationId { get; set; }
    public long Sequence { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public Guid? MessageId { get; set; }
    public string? Actor { get; set; }
    public string? CallId { get; set; }
    public string? OptionsJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AiInvestigationApprovalRecord
{
    public Guid InvestigationId { get; set; }
    public Guid MessageId { get; set; }
    public string CallId { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = "[]";
    public string? SelectedKey { get; set; }
    public string? Actor { get; set; }
}

public static class NetclawPersistenceModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<NetclawConnectionRecord>(entity =>
        {
            entity.ToTable("NetclawConnections"); entity.HasKey(x => x.TenantId);
            entity.Property(x => x.TenantId).ValueGeneratedNever();
            entity.HasIndex(x => x.Id).IsUnique(); entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.Property(x => x.Endpoint).HasMaxLength(2048); entity.Property(x => x.ProfileFingerprint).HasMaxLength(64);
            entity.Property(x => x.OwnerPrincipalId).HasMaxLength(256); entity.Property(x => x.LastTestCode).HasMaxLength(128);
        });
        builder.Entity<AiInvestigationRecord>(entity =>
        {
            entity.ToTable("AiInvestigations"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(200); entity.Property(x => x.AgentIds).HasColumnType("uuid[]");
            entity.Property(x => x.SourceActionKey).HasMaxLength(256); entity.Property(x => x.RemoteSessionId).HasMaxLength(256);
            entity.Property(x => x.ProfileFingerprint).HasMaxLength(64); entity.Property(x => x.CreatedBy).HasMaxLength(256);
            entity.Property(x => x.ActiveCredentialId).HasMaxLength(256);
            entity.HasIndex(x => new { x.TenantId, x.RequestId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.SourceActionKey }).IsUnique().HasFilter("\"SourceActionKey\" IS NOT NULL");
            entity.HasIndex(x => new { x.TenantId, x.LastActivityUtc });
            entity.HasIndex(x => new { x.TenantId, x.OccurrenceId });
        });
        builder.Entity<AiInvestigationEventRecord>(entity =>
        {
            entity.ToTable("AiInvestigationEvents"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(64); entity.Property(x => x.Actor).HasMaxLength(256);
            entity.Property(x => x.CallId).HasMaxLength(256); entity.Property(x => x.OptionsJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.InvestigationId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.InvestigationId, x.MessageId }).IsUnique().HasFilter("\"MessageId\" IS NOT NULL");
            entity.HasOne<AiInvestigationRecord>().WithMany().HasForeignKey(x => x.InvestigationId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<AiInvestigationApprovalRecord>(entity =>
        {
            entity.ToTable("AiInvestigationApprovals"); entity.HasKey(x => new { x.InvestigationId, x.CallId });
            entity.Property(x => x.CallId).HasMaxLength(256); entity.Property(x => x.OptionsJson).HasColumnType("jsonb");
            entity.Property(x => x.SelectedKey).HasMaxLength(64); entity.Property(x => x.Actor).HasMaxLength(256);
            entity.HasOne<AiInvestigationRecord>().WithMany().HasForeignKey(x => x.InvestigationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
