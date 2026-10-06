// Draft committed-owner contracts. Compile/provider/physical acceptance remains pending.
namespace NetRatel.Application.Presence;

public readonly record struct OwnerKey(ClientKey Client, Guid ConnectionId, long Epoch);

// Server ingress time and the fixed admission deadline are immutable. None of
// these timestamps, identities, or renewal expiries come from an unvalidated JWT.
public sealed record AdmissionRequest(ClientKey Client, Guid ConnectionId,
    Guid OperationId, long MinimumEpoch, DateTimeOffset ReceivedAtUtc,
    DateTimeOffset AdmissionExpiresAtUtc, DateTimeOffset AuthenticationExpiresAtUtc,
    PresenceMetadata Metadata);

public sealed record PresenceMetadata(string? AgentVersion,
    IReadOnlyList<string> Capabilities, string? LegacySpacetimeIdentity);

public sealed record Reservation(OwnerKey Owner, Guid OperationId,
    string PayloadHash, DateTimeOffset ReceivedAtUtc,
    DateTimeOffset AdmissionExpiresAtUtc, DateTimeOffset AuthenticationExpiresAtUtc);

public sealed record HeartbeatRequest(OwnerKey Owner, ulong Sequence,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? ValidatedRenewedAuthenticationExpiresAtUtc = null);

public sealed record OwnerSnapshot(OwnerKey Owner, bool Active, ulong Sequence,
    DateTimeOffset LastReceivedAtUtc, DateTimeOffset PresenceExpiresAtUtc,
    DateTimeOffset AuthenticationExpiresAtUtc, long Revision,
    Guid StartOperationId, string AdmissionPayloadHash, PresenceMetadata Metadata,
    DateTimeOffset? AcceptanceGuardAtUtc = null)
{
    public bool IsEffective(DateTimeOffset now) => AcceptanceGuardAtUtc.HasValue && Active &&
        PresenceExpiresAtUtc > now && AuthenticationExpiresAtUtc > now;
}

public enum OwnershipDisposition
{
    Accepted, Duplicate, AdmissionExpired, AdmissionCancelled,
    AdmissionCapacityExceeded, AdmissionBodyConflict, StaleEpoch,
    ConnectionMismatch, NoActiveSession, AuthenticationExpired,
    HeartbeatExpired, StaleSequence, InvalidRenewal
}
public sealed record ReserveResult(OwnershipDisposition Disposition, Reservation? Reservation = null);
public sealed record WriteResult(OwnershipDisposition Disposition, OwnerSnapshot? Current = null);
public enum RetirementReason { ExplicitClose, AuthenticationExpiry, HeartbeatExpiry }

public sealed record OwnershipPolicy(TimeSpan AdmissionLifetime,
    TimeSpan HeartbeatTimeout, TimeSpan OperationBudget,
    int MaximumPending = 16, int MaximumCancelled = 64)
{
    public OwnershipPolicy Validate()
    {
        // Existing NetRatelAkka option bounds; capacities cannot exceed #160.
        if(AdmissionLifetime<TimeSpan.FromSeconds(1)||AdmissionLifetime>TimeSpan.FromSeconds(300)||
            HeartbeatTimeout<TimeSpan.FromSeconds(5)||HeartbeatTimeout>TimeSpan.FromSeconds(3060)||
            OperationBudget<=TimeSpan.Zero||OperationBudget>TimeSpan.FromSeconds(30)||
            MaximumPending is <1 or >16||MaximumCancelled is <1 or >64)
            throw new ArgumentException("invalid-bounded-ownership-policy");
        return this;
    }
}

// Integrate by extending #148's IClientConnectionEpochStore. Keep AllocateAsync
// as counter-only compatibility API; it can never grant business authority.
public interface IClientConnectionEpochStore
{
    Task<long> AllocateAsync(ClientKey client, long minimumEpoch, CancellationToken ct);
    Task<ReserveResult> ReserveAsync(AdmissionRequest request, CancellationToken ct);
    Task<WriteResult> CommitAsync(Reservation reservation, HeartbeatRequest firstHeartbeat, CancellationToken ct);
    Task<WriteResult> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct);
    Task<WriteResult> RenewAsync(HeartbeatRequest request, CancellationToken ct);
    Task<WriteResult> CancelAdmissionAsync(ClientKey client, Guid connectionId,
        long? exactEpoch, DateTimeOffset receivedAtUtc, CancellationToken ct);
    Task<WriteResult> RetireAsync(OwnerKey owner, RetirementReason reason,
        DateTimeOffset? expectedDeadline, CancellationToken ct);
    Task<OwnerSnapshot?> GetCurrentAsync(ClientKey client, CancellationToken ct);
}
