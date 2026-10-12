using Microsoft.EntityFrameworkCore;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>Durable service intent and correlated result; an uncertain dispatch is never replayed.</summary>
public sealed class ClientServiceOperationRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public ClientServicePlatform Platform { get; set; }
    public ClientServiceControlAction Action { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ResultJson { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public long Version { get; set; }
}

public static class ClientServiceOperationModel
{
    public static void Configure(ModelBuilder builder) => builder.Entity<ClientServiceOperationRecord>(entity =>
    {
        entity.ToTable("ClientServiceOperations");
        entity.HasKey(operation => operation.Id);
        entity.Property(operation => operation.ServiceName).HasMaxLength(ClientServicesLimits.MaximumNameLength).IsRequired();
        entity.Property(operation => operation.RequestedBy).HasMaxLength(256).IsRequired();
        entity.Property(operation => operation.Reason).HasMaxLength(256);
        entity.Property(operation => operation.Status).HasMaxLength(32).IsRequired();
        entity.Property(operation => operation.ResultJson).HasMaxLength(8192);
        entity.Property(operation => operation.FailureCode).HasMaxLength(64);
        entity.Property(operation => operation.Version).IsConcurrencyToken();
        entity.HasIndex(operation => new { operation.TenantId, operation.AgentId, operation.CreatedAtUtc });
    });
}
