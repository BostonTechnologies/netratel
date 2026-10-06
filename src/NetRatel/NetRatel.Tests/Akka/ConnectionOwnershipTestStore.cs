using System.Security.Cryptography;
using System.Text.Json;
using NetRatel.Application.Presence;

namespace NetRatel.Tests.Akka;

/// <summary>Explicit unit-only durable API seam. Provider/transport tests use the PostgreSQL implementation.</summary>
internal class ConnectionOwnershipTestStore(TimeProvider? time = null) : IClientConnectionEpochStore
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<ClientKey, long> _epochs = [];
    private readonly Dictionary<Guid, Reservation> _pending = [];
    private readonly Dictionary<Guid, PresenceMetadata> _metadata = [];
    private readonly HashSet<Guid> _cancelled = [];
    private readonly Dictionary<ClientKey, OwnerSnapshot> _owners = [];

    public virtual Task<long> AllocateAsync(ClientKey client, long minimumEpoch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var epoch = checked(Math.Max(_epochs.GetValueOrDefault(client), minimumEpoch) + 1);
            _epochs[client] = epoch; return Task.FromResult(epoch);
        }
    }
    public virtual async Task<ReserveResult> ReserveAsync(AdmissionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var epoch = await AllocateAsync(request.Client, request.MinimumEpoch, ct);
        lock (_gate)
        {
            if (_cancelled.Contains(request.ConnectionId)) return new(OwnershipDisposition.AdmissionCancelled);
            if (request.AdmissionExpiresAtUtc <= _time.GetUtcNow() || request.AuthenticationExpiresAtUtc <= _time.GetUtcNow())
                return new(OwnershipDisposition.AdmissionExpired);
            var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
            var reservation = new Reservation(new(request.Client, request.ConnectionId, epoch), request.OperationId,
                hash, request.ReceivedAtUtc, request.AdmissionExpiresAtUtc, request.AuthenticationExpiresAtUtc);
            _pending[request.ConnectionId] = reservation; _metadata[request.ConnectionId] = request.Metadata with { Capabilities = request.Metadata.Capabilities.ToArray() };
            return new(OwnershipDisposition.Accepted, reservation);
        }
    }
    public virtual Task<WriteResult> CommitAsync(Reservation reservation, HeartbeatRequest heartbeat, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_cancelled.Contains(reservation.Owner.ConnectionId)) return Task.FromResult(new WriteResult(OwnershipDisposition.AdmissionCancelled));
            if (!_pending.TryGetValue(reservation.Owner.ConnectionId, out var pending) || pending != reservation || heartbeat.Owner != reservation.Owner)
                return Task.FromResult(new WriteResult(OwnershipDisposition.AdmissionBodyConflict));
            if (reservation.AdmissionExpiresAtUtc <= _time.GetUtcNow() || reservation.AuthenticationExpiresAtUtc <= _time.GetUtcNow())
                return Task.FromResult(new WriteResult(OwnershipDisposition.AdmissionExpired));
            var old = _owners.GetValueOrDefault(reservation.Owner.Client);
            if (old is not null && old.Owner.Epoch >= reservation.Owner.Epoch)
                return Task.FromResult(new WriteResult(old.Owner == reservation.Owner ? OwnershipDisposition.Duplicate : OwnershipDisposition.StaleEpoch, old));
            var owner = new OwnerSnapshot(reservation.Owner, true, heartbeat.Sequence, heartbeat.ReceivedAtUtc,
                heartbeat.ReceivedAtUtc.AddSeconds(60), heartbeat.ValidatedRenewedAuthenticationExpiresAtUtc ?? reservation.AuthenticationExpiresAtUtc,
                (old?.Revision ?? 0) + 1, reservation.OperationId, reservation.PayloadHash,
                _metadata[reservation.Owner.ConnectionId], _time.GetUtcNow());
            _owners[owner.Owner.Client] = owner; return Task.FromResult(new WriteResult(OwnershipDisposition.Accepted, owner));
        }
    }
    public virtual Task<WriteResult> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var old = _owners.GetValueOrDefault(request.Owner.Client);
            if (old is null || old.Owner != request.Owner || !old.IsEffective(_time.GetUtcNow()))
                return Task.FromResult(new WriteResult(OwnershipDisposition.NoActiveSession, old));
            if (request.Sequence <= old.Sequence) return Task.FromResult(new WriteResult(OwnershipDisposition.StaleSequence, old));
            var next = old with { Sequence = request.Sequence, LastReceivedAtUtc = request.ReceivedAtUtc,
                PresenceExpiresAtUtc = request.ReceivedAtUtc.AddSeconds(60),
                AuthenticationExpiresAtUtc = request.ValidatedRenewedAuthenticationExpiresAtUtc ?? old.AuthenticationExpiresAtUtc,
                Revision = old.Revision + 1 };
            _owners[request.Owner.Client] = next; return Task.FromResult(new WriteResult(OwnershipDisposition.Accepted, next));
        }
    }
    public virtual Task<WriteResult> RenewAsync(HeartbeatRequest request, CancellationToken ct) => RecordHeartbeatAsync(request, ct);
    public virtual Task<WriteResult> CancelAdmissionAsync(ClientKey client, Guid connectionId, long? exactEpoch, DateTimeOffset receivedAtUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) _cancelled.Add(connectionId);
        return Task.FromResult(new WriteResult(OwnershipDisposition.Accepted));
    }
    public virtual Task<WriteResult> RetireAsync(OwnerKey owner, RetirementReason reason, DateTimeOffset? expectedDeadline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var old = _owners.GetValueOrDefault(owner.Client);
            if (old?.Owner == owner) _owners[owner.Client] = old with { Active = false, Revision = old.Revision + 1 };
            return Task.FromResult(new WriteResult(OwnershipDisposition.Accepted, _owners.GetValueOrDefault(owner.Client)));
        }
    }
    public virtual Task<OwnerSnapshot?> GetCurrentAsync(ClientKey client, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return Task.FromResult(_owners.GetValueOrDefault(client)); }
}
