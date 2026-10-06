using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>Reservation state is deliberately separate from committed physical authority.</summary>
public sealed class ClientConnectionOwnerRecord
{
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public long ConnectionEpoch { get; set; }
    public Guid? ConnectionId { get; set; }
    public bool Active { get; set; }
    public decimal LastHeartbeatSequence { get; set; }
    public DateTimeOffset? LastReceivedAtUtc { get; set; }
    public DateTimeOffset? PresenceExpiresAtUtc { get; set; }
    public DateTimeOffset? AuthenticationExpiresAtUtc { get; set; }
    public long OwnerRevision { get; set; }
    public Guid? StartOperationId { get; set; }
    public string? AdmissionPayloadHash { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset? AcceptanceClockFloorUtc { get; set; }
    public DateTimeOffset? AcceptanceGuardAtUtc { get; set; }
}

public sealed class ClientConnectionAdmissionRecord
{
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid ConnectionId { get; set; }
    public long? ConnectionEpoch { get; set; }
    public Guid? OperationId { get; set; }
    public string? PayloadHash { get; set; }
    public DateTimeOffset? ReceivedAtUtc { get; set; }
    public DateTimeOffset? AdmissionExpiresAtUtc { get; set; }
    public DateTimeOffset? AuthenticationExpiresAtUtc { get; set; }
    public string? MetadataJson { get; set; }
    public short Status { get; set; }
    public DateTimeOffset RetainUntilUtc { get; set; }
}

public static class ClientConnectionOwnershipModel
{
    public static void ConfigureClientConnectionOwnershipModel(this ModelBuilder model)
    {
        model.Entity<ClientConnectionEpochRecord>().Property(x => x.CancellationBarrierUntilUtc);
        model.Entity<ClientConnectionOwnerRecord>(e =>
        {
            e.ToTable("ClientConnectionOwners", table =>
            {
                table.HasCheckConstraint("CK_ClientConnectionOwners_Identity",
                    """
                    "TenantId" > 0 AND "AgentId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    """);
                table.HasCheckConstraint("CK_ClientConnectionOwners_Epoch",
                    """
                    "ConnectionEpoch" >= 0 AND "OwnerRevision" >= 0
                    """);
                table.HasCheckConstraint("CK_ClientConnectionOwners_Sequence",
                    """
                    "LastHeartbeatSequence" BETWEEN 0 AND 18446744073709551615
                    """);
                table.HasCheckConstraint("CK_ClientConnectionOwners_CommittedShape",
                    """
                    ("ConnectionEpoch"=0 AND "ConnectionId" IS NULL) OR ("ConnectionEpoch">0 AND "ConnectionId" IS NOT NULL AND "StartOperationId" IS NOT NULL AND "AdmissionPayloadHash" IS NOT NULL AND "LastReceivedAtUtc" IS NOT NULL AND "PresenceExpiresAtUtc" IS NOT NULL AND "AuthenticationExpiresAtUtc" IS NOT NULL)
                    """);
                // Active is provisional until the deferred COMMIT guard stamps acceptance.
                table.HasCheckConstraint("CK_ClientConnectionOwners_Active",
                    """
                    "AcceptanceGuardAtUtc" IS NULL OR NOT "Active" OR ( "ConnectionEpoch" > 0 AND "ConnectionId" IS NOT NULL AND "ConnectionId" <> '00000000-0000-0000-0000-000000000000'::uuid AND "StartOperationId" IS NOT NULL AND "AdmissionPayloadHash" IS NOT NULL AND "LastReceivedAtUtc" IS NOT NULL AND "PresenceExpiresAtUtc" IS NOT NULL AND "AuthenticationExpiresAtUtc" IS NOT NULL AND "LastHeartbeatSequence" > 0)
                    """);
            });
            e.HasKey(x => new { x.TenantId, x.AgentId });
            e.Property(x => x.ConnectionEpoch).HasDefaultValue(0L);
            e.Property(x => x.Active).HasDefaultValue(false);
            e.Property(x => x.LastHeartbeatSequence).HasColumnType("numeric(20,0)").HasDefaultValue(0m);
            e.Property(x => x.OwnerRevision).HasDefaultValue(0L).IsConcurrencyToken();
            e.Property(x => x.AdmissionPayloadHash).HasMaxLength(64);
            e.Property(x => x.MetadataJson).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
        });
        model.Entity<ClientConnectionAdmissionRecord>(e =>
        {
            e.ToTable("ClientConnectionAdmissions", table =>
            {
                table.HasCheckConstraint("CK_ClientConnectionAdmissions_Identity",
                    """
                    "TenantId" > 0 AND "AgentId" <> '00000000-0000-0000-0000-000000000000'::uuid AND "ConnectionId" <> '00000000-0000-0000-0000-000000000000'::uuid
                    """);
                table.HasCheckConstraint("CK_ClientConnectionAdmissions_Status",
                    """
                    "Status" IN (1,2,3)
                    """);
                table.HasCheckConstraint("CK_ClientConnectionAdmissions_Epoch",
                    """
                    "ConnectionEpoch" IS NULL OR "ConnectionEpoch">0
                    """);
                table.HasCheckConstraint("CK_ClientConnectionAdmissions_Pending",
                    """
                    "Status" = 2 OR ( "ConnectionEpoch" IS NOT NULL AND "ConnectionEpoch">0 AND "OperationId" IS NOT NULL AND "OperationId"<>'00000000-0000-0000-0000-000000000000'::uuid AND "PayloadHash" IS NOT NULL AND "ReceivedAtUtc" IS NOT NULL AND "AdmissionExpiresAtUtc" IS NOT NULL AND "AuthenticationExpiresAtUtc" IS NOT NULL AND "AdmissionExpiresAtUtc">"ReceivedAtUtc" AND "AuthenticationExpiresAtUtc">"ReceivedAtUtc" AND "MetadataJson" IS NOT NULL)
                    """);
            });
            e.HasKey(x => new { x.TenantId, x.AgentId, x.ConnectionId });
            e.Property(x => x.PayloadHash).HasMaxLength(64);
            e.Property(x => x.MetadataJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.TenantId, x.AgentId, x.ConnectionEpoch }).IsUnique()
                .HasFilter("\"ConnectionEpoch\" IS NOT NULL").HasDatabaseName("UX_ClientConnectionAdmissions_Epoch");
            e.HasIndex(x => new { x.RetainUntilUtc, x.TenantId, x.AgentId });
        });
    }
}
