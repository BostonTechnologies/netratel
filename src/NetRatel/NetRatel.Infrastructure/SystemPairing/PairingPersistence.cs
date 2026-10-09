using Microsoft.EntityFrameworkCore;
namespace NetRatel.Infrastructure.SystemPairing;

public sealed class PairingCodeRecord
{
    public int Id { get; set; } = 1;
    public string CodeHash { get; set; } = "";
    public string AdministratorId { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int FailedAttempts { get; set; }
}
public sealed class SystemPairRecord
{
    public string Id { get; set; } = "";
    public string PeerInstanceId { get; set; } = "";
    public string PeerMetadataJson { get; set; } = "";
    public string AdministratorId { get; set; } = "";
    public string InboundSecretHash { get; set; } = "";
    public string ProtectedInboundSecret { get; set; } = "";
    public string? ProtectedOutboundSecret { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
}
public sealed class PairingRedemption
{
    public Guid OperationId { get; set; }
    public string PeerInstanceId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string PairId { get; set; } = "";
    public string ProtectedResponse { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
public sealed class PairingConnectAttempt
{
    public Guid OperationId { get; set; }
    public string PairId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string ProtectedRequest { get; set; } = "";
    public long PairRevision { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public bool Completed { get; set; }
}
public sealed class PairingConnectionRecord
{
    public Guid Id { get; set; }
    public string PairId { get; set; } = "";
    public string MappingJson { get; set; } = "";
    public string AdministratorId { get; set; } = "";
    public bool Active { get; set; }
    public long Revision { get; set; } = 1;
    public Guid OperationId { get; set; }
    public string SaveFingerprint { get; set; } = "";
    public Guid? InboundPrincipalId { get; set; }
    public string? ProtectedInboundCredential { get; set; }
    public string? ProtectedOutboundCredential { get; set; }
    public string? LastTestJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
}
public sealed class PairingCleanupRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PairId { get; set; } = "";
    public Guid? MappingId { get; set; }
    public string ProtectedPeerJson { get; set; } = "";
    public string ProtectedSecret { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public int Attempts { get; set; }
}
public sealed class InstallationIdentityRecord
{
    public int Id { get; set; } = 1;
    public Guid InstanceId { get; set; }
    public Guid? SourceInstanceId { get; set; }
    public long Revision { get; set; } = 1;
    public string? SourceAdoptedBy { get; set; }
    public long? SourceAdoptedAtUnixSeconds { get; set; }
}
public static class PairingPersistence
{
    public static void ConfigurePairingModel(this ModelBuilder model)
    {
        model.Entity<InstallationIdentityRecord>(e => { e.ToTable("ServiceLinkRuntimeIdentity"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.Revision).IsConcurrencyToken(); });
        model.Entity<PairingCodeRecord>(e => { e.ToTable("PairingCodes"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); });
        model.Entity<SystemPairRecord>(e => { e.ToTable("SystemPairs"); e.HasKey(x => x.Id); e.HasIndex(x => x.PeerInstanceId).IsUnique(); e.Property(x => x.Revision).IsConcurrencyToken(); });
        model.Entity<PairingRedemption>(e => { e.ToTable("PairingRedemptions"); e.HasKey(x => x.OperationId); });
        model.Entity<PairingConnectAttempt>(e => { e.ToTable("PairingConnectAttempts"); e.HasKey(x => x.OperationId); });
        model.Entity<PairingConnectionRecord>(e => { e.ToTable("PairingConnections"); e.HasKey(x => x.Id); e.HasIndex(x => x.PairId); e.Property(x => x.Revision).IsConcurrencyToken(); });
        model.Entity<PairingCleanupRecord>(e => { e.ToTable("PairingCleanup"); e.HasKey(x => x.Id); });
    }
}
