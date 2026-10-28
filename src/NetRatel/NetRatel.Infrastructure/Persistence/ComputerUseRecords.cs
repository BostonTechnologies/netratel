using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

public sealed class ComputerUseSessionRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid ConnectionId { get; set; }
    public decimal ConnectionEpoch { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ServicePrincipal { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Instance { get; set; } = string.Empty;
    public string TargetJson { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public bool ControlAllowed { get; set; }
    public bool OperatorPaused { get; set; }
    public string State { get; set; } = "opening";
    public int? ActiveSlot { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset IdleExpiresAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int IdleTimeoutSeconds { get; set; }
    public long Revision { get; set; }
    public Guid? PendingOperationId { get; set; }
}

public sealed class ComputerUseOperationRecord
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ServicePrincipal { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Instance { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string? Code { get; set; }
    public bool MayHaveApplied { get; set; }
    public Guid? AcceptedAuditId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class ComputerUseAuditRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? OperationId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ServicePrincipal { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string Instance { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public Guid? AcceptedAuditId { get; set; }
    public string? PayloadHash { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

internal static class ComputerUsePersistenceModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ComputerUseSessionRecord>(entity =>
        {
            entity.ToTable("ComputerUseSessions");
            entity.HasKey(record => record.Id);
            entity.HasIndex(record => new { record.TenantId, record.AgentId, record.ActiveSlot }).IsUnique();
            entity.Property(record => record.ConnectionEpoch).HasPrecision(20, 0);
            entity.Property(record => record.Revision).IsConcurrencyToken();
            entity.Property(record => record.TargetJson).HasColumnType("jsonb").IsRequired();
            entity.Property(record => record.Provider).HasMaxLength(32);
            entity.Property(record => record.State).HasMaxLength(32);
            entity.Property(record => record.Subject).HasMaxLength(256);
            entity.Property(record => record.ClientId).HasMaxLength(256);
            entity.Property(record => record.ServicePrincipal).HasMaxLength(256);
            entity.Property(record => record.Resource).HasMaxLength(512);
            entity.Property(record => record.Instance).HasMaxLength(32);
        });
        modelBuilder.Entity<ComputerUseOperationRecord>(entity =>
        {
            entity.ToTable("ComputerUseOperations");
            entity.HasKey(record => record.Id);
            entity.HasIndex(record => new { record.SessionId, record.CreatedAtUtc });
            entity.Property(record => record.Status).IsConcurrencyToken().HasMaxLength(32);
            entity.Property(record => record.Operation).HasMaxLength(32);
            entity.Property(record => record.PayloadHash).HasMaxLength(64);
            entity.Property(record => record.Code).HasMaxLength(128);
            entity.Property(record => record.Subject).HasMaxLength(256);
            entity.Property(record => record.ClientId).HasMaxLength(256);
            entity.Property(record => record.ServicePrincipal).HasMaxLength(256);
            entity.Property(record => record.Resource).HasMaxLength(512);
            entity.Property(record => record.Instance).HasMaxLength(32);
            entity.Property(record => record.CorrelationId).HasMaxLength(256);
        });
        modelBuilder.Entity<ComputerUseAuditRecord>(entity =>
        {
            entity.ToTable("ComputerUseAudits");
            entity.HasKey(record => record.Id);
            entity.HasIndex(record => new { record.TenantId, record.AgentId, record.OccurredAtUtc });
            entity.HasIndex(record => record.OperationId);
            entity.Property(record => record.Operation).HasMaxLength(32);
            entity.Property(record => record.Outcome).HasMaxLength(32);
            entity.Property(record => record.Code).HasMaxLength(128);
            entity.Property(record => record.PayloadHash).HasMaxLength(64);
            entity.Property(record => record.Subject).HasMaxLength(256);
            entity.Property(record => record.ClientId).HasMaxLength(256);
            entity.Property(record => record.ServicePrincipal).HasMaxLength(256);
            entity.Property(record => record.Resource).HasMaxLength(512);
            entity.Property(record => record.Instance).HasMaxLength(32);
            entity.Property(record => record.CorrelationId).HasMaxLength(256);
        });
    }
}
