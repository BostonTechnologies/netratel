using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>Server-owned ingress authority. Caller payloads never supply these fields.</summary>
public sealed class ManagedOrchestrationRequestBinding
{
    public int RequestId { get; set; }
    public Guid ServicePrincipalId { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public string JobDefinitionId { get; set; } = "";
    public string? ExecutionId { get; set; }
    public string ParentRequestId { get; set; } = "";
    public string RequestTaskId { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string IngestFingerprint { get; set; } = "";
    public string? LinkId { get; set; }
    public long LinkRevision { get; set; }
    public string? GrantHash { get; set; }
    public string PeerInstanceId { get; set; } = "";
    public string PeerTenantId { get; set; } = "";
    public string? CallbackUrl { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string SourceSystem => $"service:{ServicePrincipalId:N}";
}

/// <summary>Durable delivery lease and acknowledgement; HTTP delivery remains at least once.</summary>
public sealed class OrchestrationCallbackDelivery
{
    public int RequestId { get; set; }
    public string Phase { get; set; } = "";
    public long Revision { get; set; } = 1;
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
    public int Attempts { get; set; }
}

public static class ManagedOrchestrationModelConfiguration
{
    public static void ConfigureManagedOrchestrationModel(this ModelBuilder model)
    {
        model.Entity<ManagedOrchestrationRequestBinding>(e =>
        {
            e.ToTable("ManagedOrchestrationRequestBindings");
            e.HasKey(x => x.RequestId);
            e.Ignore(x => x.SourceSystem);
            e.HasIndex(x => new { x.ServicePrincipalId, x.ParentRequestId, x.RequestTaskId }).IsUnique();
            e.HasIndex(x => x.ExecutionId).IsUnique().HasFilter("\"ExecutionId\" IS NOT NULL");
            e.HasOne<RequestRecord>().WithOne().HasForeignKey<ManagedOrchestrationRequestBinding>(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
            foreach (var field in new[] { nameof(ManagedOrchestrationRequestBinding.JobDefinitionId), nameof(ManagedOrchestrationRequestBinding.ExecutionId), nameof(ManagedOrchestrationRequestBinding.ParentRequestId), nameof(ManagedOrchestrationRequestBinding.RequestTaskId), nameof(ManagedOrchestrationRequestBinding.CorrelationId), nameof(ManagedOrchestrationRequestBinding.LinkId), nameof(ManagedOrchestrationRequestBinding.PeerInstanceId), nameof(ManagedOrchestrationRequestBinding.PeerTenantId) })
                e.Property(field).HasMaxLength(256);
            e.Property(x => x.IngestFingerprint).HasMaxLength(64);
            e.Property(x => x.GrantHash).HasMaxLength(64);
            e.Property(x => x.CallbackUrl).HasMaxLength(2048);
        });
        model.Entity<OrchestrationCallbackDelivery>(e =>
        {
            e.ToTable("OrchestrationCallbackDeliveries");
            e.HasKey(x => new { x.RequestId, x.Phase });
            e.Property(x => x.Phase).HasMaxLength(32);
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasIndex(x => new { x.DeliveredAtUtc, x.NextAttemptAtUtc });
            e.HasOne<RequestRecord>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
