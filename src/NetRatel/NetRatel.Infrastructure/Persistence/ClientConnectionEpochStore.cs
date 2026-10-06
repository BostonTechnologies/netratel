// Draft committed-owner integration. Compile/provider/physical acceptance remains pending.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Presence;
using Npgsql;

namespace NetRatel.Infrastructure.Persistence;

public sealed class ClientConnectionEpochStore(
    IServiceScopeFactory scopes, TimeProvider clock, OwnershipPolicy policy)
    : IClientConnectionEpochStore
{
    private const short Pending = 1, Cancelled = 2, Committed = 3;
    private const string AcceptanceGuardConstraint = "CK_ClientConnectionOwners_AcceptanceGuard";
    private readonly OwnershipPolicy _policy = policy.Validate();
    private static DateTimeOffset Normalize(DateTimeOffset value) => new(value.UtcTicks / 10 * 10, TimeSpan.Zero);

    public Task<long> AllocateAsync(ClientKey client,long minimumEpoch,CancellationToken ct)
    {
        RequireClient(client);
        if(minimumEpoch<0) throw new ArgumentOutOfRangeException(nameof(minimumEpoch));
        return OperationAsync(async (connection,token)=>
        {
            await using var tx=await connection.BeginTransactionAsync(token);
            var old=await LockCounterAsync(connection,tx,client,minimumEpoch,token);
            var epoch=checked(Math.Max(old,minimumEpoch)+1);
            await ExecuteAsync(connection,tx,"""
                UPDATE "ClientConnectionEpochs" SET "LastIssuedEpoch"=@epoch WHERE "TenantId"=@tenant AND "AgentId"=@agent
                """,token,("tenant",client.TenantId),("agent",client.AgentId),("epoch",epoch));
            await tx.CommitAsync(token);
            return epoch; // compatibility allocation cannot create/replace owner
        },ct);
    }

    public Task<ReserveResult> ReserveAsync(AdmissionRequest input, CancellationToken ct)
    {
        RequireClient(input.Client);
        if (input.ConnectionId == Guid.Empty || input.OperationId == Guid.Empty || input.MinimumEpoch < 0)
            throw new ArgumentException("invalid-admission-identity");
        RequireMetadata(input.Metadata);
        var request = input with {
            ReceivedAtUtc = Normalize(input.ReceivedAtUtc),
            AdmissionExpiresAtUtc = Normalize(input.AdmissionExpiresAtUtc),
            AuthenticationExpiresAtUtc = Normalize(input.AuthenticationExpiresAtUtc) };
        var hash = PayloadHash(request);
        return OperationAsync(async (connection, token) =>
        {
            await using var tx = await connection.BeginTransactionAsync(token);
            var lastIssued = await LockCounterAsync(connection, tx, request.Client, request.MinimumEpoch, token);
            var now = await EffectiveNowAsync(connection, tx, clock, token);
            if (!WithinAdmission(request, now)) return new ReserveResult(OwnershipDisposition.AdmissionExpired);
            await PruneAdmissionsAsync(connection, tx, request.Client, now, token);
            var existing = await ReadAdmissionAsync(connection, tx, request.Client, request.ConnectionId, token);
            if (existing is { Status: Cancelled }) return new ReserveResult(OwnershipDisposition.AdmissionCancelled);
            if (existing is not null)
            {
                if (existing.Hash != hash || existing.OperationId != request.OperationId)
                    return new ReserveResult(OwnershipDisposition.AdmissionBodyConflict);
                if (existing.Status == Committed)
                {
                    var current = await ReadOwnerAsync(connection, tx, request.Client, false, token);
                    if (current is null || current.Owner.ConnectionId != request.ConnectionId || current.Owner.Epoch != existing.Epoch ||
                        !current.IsEffective(await EffectiveNowAsync(connection, tx, clock, token)))
                        return new ReserveResult(OwnershipDisposition.StaleEpoch);
                }
                return new ReserveResult(OwnershipDisposition.Duplicate, ToReservation(request.Client, existing));
            }
            // The owner row is read without a write lock. A retry of the exact
            // currently committed admission may outlive the small journal.
            var owner = await ReadOwnerAsync(connection, tx, request.Client, false, token);
            if (owner is not null && owner.Owner.ConnectionId == request.ConnectionId)
            {
                if (owner.StartOperationId != request.OperationId || owner.AdmissionPayloadHash != hash)
                    return new ReserveResult(OwnershipDisposition.AdmissionBodyConflict);
                return owner.IsEffective(await EffectiveNowAsync(connection, tx, clock, token))
                    ? new ReserveResult(OwnershipDisposition.Duplicate, new(owner.Owner, request.OperationId, hash,
                        request.ReceivedAtUtc, request.AdmissionExpiresAtUtc, request.AuthenticationExpiresAtUtc))
                    : new ReserveResult(OwnershipDisposition.NoActiveSession);
            }
            var blocked = await ScalarAsync<bool>(connection, tx, """
                SELECT COALESCE("CancellationBarrierUntilUtc" > @now, FALSE) OR
                  (SELECT count(*) FROM "ClientConnectionAdmissions" WHERE "TenantId"=@tenant AND "AgentId"=@agent
                    AND "Status"=1 AND "AdmissionExpiresAtUtc">@now AND "AuthenticationExpiresAtUtc">@now) >= @maximum
                  OR (SELECT count(*) FROM "ClientConnectionAdmissions" WHERE "TenantId"=@tenant AND "AgentId"=@agent
                    AND "Status"=2 AND "RetainUntilUtc">@now) >= @cancelMaximum
                FROM "ClientConnectionEpochs" WHERE "TenantId"=@tenant AND "AgentId"=@agent
                """, token, ("tenant", request.Client.TenantId), ("agent", request.Client.AgentId),
                ("now", now), ("maximum", _policy.MaximumPending), ("cancelMaximum", _policy.MaximumCancelled));
            if (blocked) return new ReserveResult(OwnershipDisposition.AdmissionCapacityExceeded);
            var epoch = checked(Math.Max(lastIssued, request.MinimumEpoch) + 1);
            await ExecuteAsync(connection, tx, """
                UPDATE "ClientConnectionEpochs" SET "LastIssuedEpoch"=@epoch
                WHERE "TenantId"=@tenant AND "AgentId"=@agent;
                INSERT INTO "ClientConnectionAdmissions" ("TenantId","AgentId","ConnectionId","ConnectionEpoch","OperationId",
                  "PayloadHash","ReceivedAtUtc","AdmissionExpiresAtUtc","AuthenticationExpiresAtUtc","MetadataJson","Status","RetainUntilUtc")
                VALUES (@tenant,@agent,@connection,@epoch,@operation,@hash,@received,@deadline,@authentication,CAST(@metadata AS jsonb),1,@deadline)
                """, token, ("tenant", request.Client.TenantId), ("agent", request.Client.AgentId),
                ("connection", request.ConnectionId), ("epoch", epoch), ("operation", request.OperationId),
                ("hash", hash), ("received", request.ReceivedAtUtc), ("deadline", request.AdmissionExpiresAtUtc),
                ("authentication", request.AuthenticationExpiresAtUtc), ("metadata", JsonSerializer.Serialize(request.Metadata)));
            await tx.CommitAsync(token);
            return new ReserveResult(OwnershipDisposition.Accepted, new(new(request.Client, request.ConnectionId, epoch),
                request.OperationId, hash, request.ReceivedAtUtc, request.AdmissionExpiresAtUtc, request.AuthenticationExpiresAtUtc));
        }, ct);
    }

    public Task<WriteResult> CommitAsync(Reservation reservation, HeartbeatRequest heartbeat, CancellationToken ct)
    {
        RequireOwner(reservation.Owner);
        if (heartbeat.Owner != reservation.Owner || heartbeat.Sequence == 0) throw new ArgumentException("invalid-first-heartbeat");
        return OperationAsync(async (connection, token) =>
        {
            await using var tx = await connection.BeginTransactionAsync(token);
            // Admission journal/cancellation serialization is separate from
            // committed owner writes. Never compare LastIssuedEpoch here.
            await LockCounterAsync(connection, tx, reservation.Owner.Client, 0, token);
            var admission = await ReadAdmissionAsync(connection, tx, reservation.Owner.Client, reservation.Owner.ConnectionId, token);
            if (admission is { Status: Cancelled }) return new WriteResult(OwnershipDisposition.AdmissionCancelled);
            if (admission is null || admission.Epoch != reservation.Owner.Epoch || admission.OperationId != reservation.OperationId ||
                admission.Hash != reservation.PayloadHash || admission.Received != Normalize(reservation.ReceivedAtUtc) ||
                admission.Deadline != Normalize(reservation.AdmissionExpiresAtUtc) ||
                admission.Authentication != Normalize(reservation.AuthenticationExpiresAtUtc))
                return new WriteResult(OwnershipDisposition.AdmissionBodyConflict);
            await ExecuteAsync(connection, tx, """
                INSERT INTO "ClientConnectionOwners" ("TenantId","AgentId") VALUES (@tenant,@agent)
                ON CONFLICT ("TenantId","AgentId") DO NOTHING
                """, token, ("tenant", reservation.Owner.Client.TenantId), ("agent", reservation.Owner.Client.AgentId));
            var current = await ReadOwnerAsync(connection, tx, reservation.Owner.Client, true, token);
            var now = await EffectiveNowAsync(connection, tx, clock, token);
            if (admission.Status == Committed)
                return current is not null && current.Owner == reservation.Owner && current.StartOperationId == reservation.OperationId &&
                    current.AdmissionPayloadHash == reservation.PayloadHash && current.IsEffective(now)
                    ? new WriteResult(OwnershipDisposition.Duplicate, current)
                    : new WriteResult(OwnershipDisposition.StaleEpoch, current);
            var received = Normalize(heartbeat.ReceivedAtUtc);
            var presenceUntil = Normalize(received + _policy.HeartbeatTimeout);
            if (admission.Deadline <= now || admission.Authentication <= now || received < admission.Received ||
                received > now || received >= admission.Deadline || presenceUntil <= now)
                return new WriteResult(OwnershipDisposition.AdmissionExpired, current);
            if (heartbeat.ValidatedRenewedAuthenticationExpiresAtUtc is { } renewal &&
                (Normalize(renewal) <= admission.Authentication || Normalize(renewal) <= now))
                return new WriteResult(OwnershipDisposition.InvalidRenewal, current);
            if (current is not null && current.Owner.Epoch >= reservation.Owner.Epoch)
                return new WriteResult(OwnershipDisposition.StaleEpoch, current);
            var authUntil = Normalize(heartbeat.ValidatedRenewedAuthenticationExpiresAtUtc ?? admission.Authentication!.Value);
            var changed = await ExecuteAsync(connection, tx, """
                UPDATE "ClientConnectionOwners" SET "ConnectionEpoch"=@epoch,"ConnectionId"=@connection,"Active"=TRUE,
                  "LastHeartbeatSequence"=@sequence,"LastReceivedAtUtc"=@received,"PresenceExpiresAtUtc"=@presence,
                  "AuthenticationExpiresAtUtc"=@authentication,"OwnerRevision"="OwnerRevision"+1,
                  "StartOperationId"=@operation,"AdmissionPayloadHash"=@hash,"MetadataJson"=CAST(@metadata AS jsonb),
                  "AcceptanceClockFloorUtc"=@now,"AcceptanceGuardAtUtc"=NULL
                WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "ConnectionEpoch"<@epoch AND "OwnerRevision"=@revision
                  AND @deadline > GREATEST(@now,clock_timestamp()) AND @original > GREATEST(@now,clock_timestamp())
                  AND @presence > GREATEST(@now,clock_timestamp())
                """, token, OwnerParameters(reservation.Owner).Concat([
                    ("sequence", (object)(decimal)heartbeat.Sequence), ("received", received), ("presence", presenceUntil),
                    ("authentication", authUntil), ("operation", reservation.OperationId), ("hash", reservation.PayloadHash),
                    ("metadata", admission.Metadata!), ("revision", current?.Revision ?? 0L), ("deadline", admission.Deadline!.Value),
                    ("original", admission.Authentication!.Value), ("now", now)]).ToArray());
            if (changed != 1) return new WriteResult(OwnershipDisposition.StaleEpoch, current);
            if (current is not null)
                await RetainPredecessorAsync(connection,tx,current.Owner,token);
            await ExecuteAsync(connection, tx, """
                UPDATE "ClientConnectionAdmissions" SET "Status"=3 WHERE "TenantId"=@tenant AND "AgentId"=@agent
                  AND "ConnectionId"=@connection AND "ConnectionEpoch"=@epoch AND "Status"=1
                """, token, OwnerParameters(reservation.Owner));
            // Preserve the actor's original owned cancellation authority.
            // A cancelled token prevents initiating COMMIT; in-flight database
            // cancellation still has an ordered/possibly-unknown outcome.
            token.ThrowIfCancellationRequested();
            var commitFloor = await EffectiveNowAsync(connection, tx, clock, token);
            if (admission.Deadline <= commitFloor || admission.Authentication <= commitFloor || presenceUntil <= commitFloor)
                return new WriteResult(OwnershipDisposition.AdmissionExpired, current);
            await ExecuteAsync(connection, tx, """
                UPDATE "ClientConnectionOwners" SET "AcceptanceClockFloorUtc"=GREATEST("AcceptanceClockFloorUtc",@floor)
                WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "ConnectionId"=@connection AND "ConnectionEpoch"=@epoch
                """, token, OwnerParameters(reservation.Owner).Concat([("floor", (object)commitFloor)]).ToArray());
            token.ThrowIfCancellationRequested();
            try
            {
                // Deferred PostgreSQL guard validates inside this exact
                // standalone COMMIT. Nothing before successful CommitAsync is
                // an accepted owner or a reason to publish/ACK.
                await tx.CommitAsync(token);
            }
            catch (PostgresException error) when (error.SqlState == "NR001" &&
                error.ConstraintName == AcceptanceGuardConstraint)
            {
                // This exact deferred-guard ErrorResponse is proof of an
                // aborted candidate transaction. All tentative owner/journal
                // writes rolled back. Network loss, query cancellation and
                // other errors are deliberately NOT translated to this result.
                return new WriteResult(OwnershipDisposition.AdmissionExpired, current);
            }
            // Read the actual persisted guard proof after commit. A competing
            // newer owner or a failed/late read remains observable to the
            // actor's existing confirmation/unknown-outcome cleanup path.
            var accepted = await ReadOwnerAsync(connection, null, reservation.Owner.Client, false, token);
            if (accepted is null || accepted.Owner != reservation.Owner || accepted.StartOperationId != reservation.OperationId ||
                accepted.AdmissionPayloadHash != reservation.PayloadHash)
                return new WriteResult(OwnershipDisposition.StaleEpoch, accepted);
            if (!accepted.AcceptanceGuardAtUtc.HasValue)
                throw new InvalidOperationException("committed-owner-guard-proof-missing");
            return new WriteResult(OwnershipDisposition.Accepted, accepted);
        }, ct);
    }

    public Task<WriteResult> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct)
    {
        RequireOwner(request.Owner);
        if (request.Sequence == 0) throw new ArgumentException("invalid-heartbeat-sequence");
        return OperationAsync(async (connection, token) =>
        {
            await using var tx = await connection.BeginTransactionAsync(token);
            var current = await ReadOwnerAsync(connection, tx, request.Owner.Client, true, token);
            var now = await EffectiveNowAsync(connection, tx, clock, token);
            var denied = CheckCurrent(current, request.Owner, now);
            if (denied is not null) return new WriteResult(denied.Value, current);
            if (request.Sequence == current!.Sequence) return new WriteResult(OwnershipDisposition.Duplicate, current);
            if (request.Sequence < current.Sequence) return new WriteResult(OwnershipDisposition.StaleSequence, current);
            var received = Normalize(request.ReceivedAtUtc);
            var presenceUntil = Normalize(received + _policy.HeartbeatTimeout);
            if (received > now || received < current.LastReceivedAtUtc || presenceUntil <= now)
                return new WriteResult(OwnershipDisposition.HeartbeatExpired, current);
            var authUntil = current.AuthenticationExpiresAtUtc;
            if (request.ValidatedRenewedAuthenticationExpiresAtUtc is { } renewal)
            {
                authUntil = Normalize(renewal);
                if (authUntil <= current.AuthenticationExpiresAtUtc || authUntil <= now)
                    return new WriteResult(OwnershipDisposition.InvalidRenewal, current);
            }
            var changed = await ExecuteAsync(connection, tx, """
                UPDATE "ClientConnectionOwners" SET "LastHeartbeatSequence"=@sequence,"LastReceivedAtUtc"=@received,
                  "PresenceExpiresAtUtc"=@presence,"AuthenticationExpiresAtUtc"=@authentication,"OwnerRevision"="OwnerRevision"+1
                WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "ConnectionId"=@connection AND "ConnectionEpoch"=@epoch
                  AND "Active"=TRUE AND "AuthenticationExpiresAtUtc">GREATEST(@now,clock_timestamp())
                  AND "PresenceExpiresAtUtc">GREATEST(@now,clock_timestamp()) AND @presence>GREATEST(@now,clock_timestamp())
                  AND "OwnerRevision"=@revision AND "LastHeartbeatSequence"<@sequence
                """, token, OwnerParameters(request.Owner).Concat([
                    ("sequence", (object)(decimal)request.Sequence), ("received", received), ("presence", presenceUntil),
                    ("authentication", authUntil), ("now", now), ("revision", current.Revision)]).ToArray());
            if (changed != 1) return new WriteResult(OwnershipDisposition.NoActiveSession, current);
            var accepted = await ReadOwnerAsync(connection, tx, request.Owner.Client, false, token);
            await tx.CommitAsync(token);
            return new WriteResult(OwnershipDisposition.Accepted, accepted);
        }, ct);
    }

    public Task<WriteResult> RenewAsync(HeartbeatRequest request, CancellationToken ct) =>
        request.ValidatedRenewedAuthenticationExpiresAtUtc is null
            ? Task.FromResult(new WriteResult(OwnershipDisposition.InvalidRenewal))
            : RecordHeartbeatAsync(request, ct);

    public Task<WriteResult> CancelAdmissionAsync(ClientKey client, Guid connectionId, long? exactEpoch,
        DateTimeOffset receivedAtUtc, CancellationToken ct)
    {
        RequireClient(client);
        if (connectionId == Guid.Empty || exactEpoch is <= 0) throw new ArgumentException("invalid-cancellation-identity");
        return OperationAsync(async (connection, token) =>
        {
            await using var tx = await connection.BeginTransactionAsync(token);
            await LockCounterAsync(connection, tx, client, 0, token);
            var now = await EffectiveNowAsync(connection, tx, clock, token);
            await PruneAdmissionsAsync(connection, tx, client, now, token);
            var admission = await ReadAdmissionAsync(connection, tx, client, connectionId, token);
            if (admission is not null && exactEpoch.HasValue && admission.Epoch.HasValue && admission.Epoch != exactEpoch)
                return new WriteResult(OwnershipDisposition.StaleEpoch);
            // First cancellation fixes its tombstone expiry. Repeats never
            // renew an existing tombstone or resurrect a pending admission.
            if(Normalize(receivedAtUtc)>now) throw new ArgumentException("invalid-cancellation-receipt");
            var until = Normalize(receivedAtUtc + _policy.AdmissionLifetime);
            var cancelledCount = await ScalarAsync<long>(connection, tx, """
                SELECT count(*) FROM "ClientConnectionAdmissions" WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "Status"=2 AND "RetainUntilUtc">@now
                """, token, ("tenant", client.TenantId), ("agent", client.AgentId), ("now", now));
            if (admission is not null && (admission.Status == Cancelled || cancelledCount < _policy.MaximumCancelled))
            {
                await ExecuteAsync(connection, tx, """
                    UPDATE "ClientConnectionAdmissions" SET
                      "RetainUntilUtc"=CASE WHEN "Status"=2 THEN "RetainUntilUtc" ELSE GREATEST("RetainUntilUtc",@until) END,
                      "Status"=2 WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "ConnectionId"=@connection
                    """, token, ("tenant", client.TenantId), ("agent", client.AgentId), ("connection", connectionId), ("until", until));
            }
            else if (until > now || admission is not null)
            {
                if (cancelledCount < _policy.MaximumCancelled)
                    await ExecuteAsync(connection, tx, """
                        INSERT INTO "ClientConnectionAdmissions" ("TenantId","AgentId","ConnectionId","Status","RetainUntilUtc")
                        VALUES (@tenant,@agent,@connection,2,@until)
                        """, token, ("tenant", client.TenantId), ("agent", client.AgentId), ("connection", connectionId), ("until", until));
                else
                {
                    // Delete an unrecordable pending admission and retain the
                    // finite client barrier. Its late commit can find neither
                    // a usable admission nor a way to allocate under this ID.
                    if (admission is not null)
                    {
                        if (admission.Deadline is { } fixedDeadline && fixedDeadline > until) until = fixedDeadline;
                        await ExecuteAsync(connection, tx, """
                            DELETE FROM "ClientConnectionAdmissions" WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "ConnectionId"=@connection
                            """, token, ("tenant", client.TenantId), ("agent", client.AgentId), ("connection", connectionId));
                    }
                    await ExecuteAsync(connection, tx, """
                        UPDATE "ClientConnectionEpochs" SET "CancellationBarrierUntilUtc"=GREATEST("CancellationBarrierUntilUtc",@until)
                        WHERE "TenantId"=@tenant AND "AgentId"=@agent
                        """, token, ("tenant", client.TenantId), ("agent", client.AgentId), ("until", until));
                }
            }
            // Unknown epoch is allowed only for the exact physical connection
            // ID, which is generated by the server. It cannot select a successor.
            await ExecuteAsync(connection, tx, """
                UPDATE "ClientConnectionOwners" SET "Active"=FALSE,"OwnerRevision"="OwnerRevision"+1
                WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "ConnectionId"=@connection AND "Active"=TRUE
                  AND (@epoch IS NULL OR "ConnectionEpoch"=@epoch)
                """, token, ("tenant", client.TenantId), ("agent", client.AgentId), ("connection", connectionId),
                ("epoch", exactEpoch.HasValue ? (object)exactEpoch.Value : DBNull.Value));
            var current = await ReadOwnerAsync(connection, tx, client, false, token);
            await tx.CommitAsync(token);
            return new WriteResult(OwnershipDisposition.Accepted, current);
        }, ct);
    }

    public Task<WriteResult> RetireAsync(OwnerKey owner, RetirementReason reason, DateTimeOffset? expectedDeadline, CancellationToken ct)
    {
        RequireOwner(owner);
        if (reason != RetirementReason.ExplicitClose && expectedDeadline is null) throw new ArgumentException("expiry-needs-exact-deadline");
        return OperationAsync(async (connection, token) =>
        {
            await using var tx = await connection.BeginTransactionAsync(token);
            var current = await ReadOwnerAsync(connection, tx, owner.Client, true, token);
            if (current is null || current.Owner != owner) return new WriteResult(OwnershipDisposition.StaleEpoch, current);
            var now = await EffectiveNowAsync(connection, tx, clock, token);
            if (reason == RetirementReason.AuthenticationExpiry &&
                (current.AuthenticationExpiresAtUtc != Normalize(expectedDeadline!.Value) || current.AuthenticationExpiresAtUtc > now) ||
                reason == RetirementReason.HeartbeatExpiry &&
                (current.PresenceExpiresAtUtc != Normalize(expectedDeadline!.Value) || current.PresenceExpiresAtUtc > now))
                return new WriteResult(OwnershipDisposition.Duplicate, current);
            await ExecuteAsync(connection, tx, """
                UPDATE "ClientConnectionOwners" SET "Active"=FALSE,"OwnerRevision"="OwnerRevision"+1
                WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "ConnectionId"=@connection AND "ConnectionEpoch"=@epoch AND "Active"=TRUE
                """, token, OwnerParameters(owner));
            var retired = await ReadOwnerAsync(connection, tx, owner.Client, false, token);
            await tx.CommitAsync(token);
            return new WriteResult(OwnershipDisposition.Accepted, retired);
        }, ct);
    }

    public Task<OwnerSnapshot?> GetCurrentAsync(ClientKey client, CancellationToken ct)
    {
        RequireClient(client);
        return OperationAsync(async (connection, token) =>
        {
            var current = await ReadOwnerAsync(connection, null, client, false, token);
            return current is null ? null : current with { Active = current.IsEffective(await EffectiveNowAsync(connection, null, clock, token)) };
        }, ct);
    }

    private async Task<T> OperationAsync<T>(Func<NpgsqlConnection,CancellationToken,Task<T>> action, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_policy.OperationBudget, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        if (!db.Database.IsNpgsql()) throw new InvalidOperationException("committed-ownership-requires-postgresql");
        await db.Database.OpenConnectionAsync(linked.Token);
        return await action((NpgsqlConnection)db.Database.GetDbConnection(), linked.Token);
    }

    private static async Task<long> LockCounterAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        ClientKey client, long minimum, CancellationToken ct)
    {
        await ExecuteAsync(connection, tx, """
            INSERT INTO "ClientConnectionEpochs" ("TenantId","AgentId","LastIssuedEpoch")
            VALUES (@tenant,@agent,GREATEST(@minimum,COALESCE((SELECT "ConnectionEpoch" FROM "ClientServicesSnapshots"
              WHERE "TenantId"=@tenant AND "AgentId"=@agent),0))) ON CONFLICT ("TenantId","AgentId") DO NOTHING
            """, ct, ("tenant", client.TenantId), ("agent", client.AgentId), ("minimum", minimum));
        return await ScalarAsync<long>(connection, tx, """
            /* nr-owner-reserve */ SELECT GREATEST("LastIssuedEpoch", COALESCE((SELECT "ConnectionEpoch" FROM "ClientServicesSnapshots"
              WHERE "TenantId"=@tenant AND "AgentId"=@agent),0)) FROM "ClientConnectionEpochs"
            WHERE "TenantId"=@tenant AND "AgentId"=@agent FOR UPDATE
            """, ct, ("tenant", client.TenantId), ("agent", client.AgentId));
    }

    private static Task<int> PruneAdmissionsAsync(NpgsqlConnection c, NpgsqlTransaction tx, ClientKey key, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(c, tx, """
            DELETE FROM "ClientConnectionAdmissions" WHERE "TenantId"=@tenant AND "AgentId"=@agent AND
              ("RetainUntilUtc"<=@now OR ("Status"=1 AND ("AuthenticationExpiresAtUtc"<=@now OR "AdmissionExpiresAtUtc"<=@now)))
            """, ct, ("tenant", key.TenantId), ("agent", key.AgentId), ("now", now));

    private async Task RetainPredecessorAsync(NpgsqlConnection c,NpgsqlTransaction tx,OwnerKey old,CancellationToken ct)
    {
        var record=await ReadAdmissionAsync(c,tx,old.Client,old.ConnectionId,ct);
        if(record is null || record.Status!=Committed) return;
        var now=await EffectiveNowAsync(c,tx,clock,ct);
        var count=await ScalarAsync<long>(c,tx,"""
            SELECT count(*) FROM "ClientConnectionAdmissions" WHERE "TenantId"=@tenant AND "AgentId"=@agent
              AND "Status"=2 AND "RetainUntilUtc">@now
            """,ct,("tenant",old.Client.TenantId),("agent",old.Client.AgentId),("now",now));
        var until=Normalize(now+_policy.AdmissionLifetime);
        if(record.Deadline!.Value>until) until=record.Deadline.Value;
        if(count<_policy.MaximumCancelled)
            await ExecuteAsync(c,tx,"""
                UPDATE "ClientConnectionAdmissions" SET "Status"=2,"RetainUntilUtc"=GREATEST("RetainUntilUtc",@until)
                WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "ConnectionId"=@connection
                """,ct,("tenant",old.Client.TenantId),("agent",old.Client.AgentId),("connection",old.ConnectionId),("until",until));
        else
            await ExecuteAsync(c,tx,"""
                UPDATE "ClientConnectionEpochs" SET "CancellationBarrierUntilUtc"=GREATEST("CancellationBarrierUntilUtc",@until)
                WHERE "TenantId"=@tenant AND "AgentId"=@agent;
                DELETE FROM "ClientConnectionAdmissions" WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "ConnectionId"=@connection
                """,ct,("tenant",old.Client.TenantId),("agent",old.Client.AgentId),("connection",old.ConnectionId),("until",until));
        // At most one committed journal (current owner), 16 pending and 64
        // retained tombstones. Overflow creates a finite barrier instead of
        // dropping a usable replay guard or accumulating completed journals.
    }

    private sealed record Admission(Guid ConnectionId, long? Epoch, Guid? OperationId, string? Hash,
        DateTimeOffset? Received, DateTimeOffset? Deadline, DateTimeOffset? Authentication, string? Metadata, short Status);
    private static async Task<Admission?> ReadAdmissionAsync(NpgsqlConnection c, NpgsqlTransaction tx, ClientKey key, Guid id, CancellationToken ct)
    {
        await using var cmd = Command(c, tx, """
            SELECT "ConnectionEpoch","OperationId","PayloadHash","ReceivedAtUtc","AdmissionExpiresAtUtc","AuthenticationExpiresAtUtc","MetadataJson","Status"
            FROM "ClientConnectionAdmissions" WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "ConnectionId"=@connection
            """, ("tenant", key.TenantId), ("agent", key.AgentId), ("connection", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(id, Nullable<long>(reader,0), Nullable<Guid>(reader,1), reader.IsDBNull(2)?null:reader.GetString(2),
            Nullable<DateTimeOffset>(reader,3), Nullable<DateTimeOffset>(reader,4), Nullable<DateTimeOffset>(reader,5),
            reader.IsDBNull(6)?null:reader.GetString(6), reader.GetInt16(7));
    }

    internal static async Task<OwnerSnapshot?> ReadOwnerAsync(NpgsqlConnection c, NpgsqlTransaction? tx,
        ClientKey key, bool forUpdate, CancellationToken ct, bool forShare = false)
    {
        var suffix = forUpdate ? " FOR UPDATE" : forShare ? " FOR SHARE" : "";
        await using var cmd = Command(c, tx, """
            SELECT "ConnectionEpoch","ConnectionId","Active","LastHeartbeatSequence","LastReceivedAtUtc","PresenceExpiresAtUtc",
              "AuthenticationExpiresAtUtc","OwnerRevision","StartOperationId","AdmissionPayloadHash","MetadataJson","AcceptanceGuardAtUtc"
            FROM "ClientConnectionOwners" WHERE "TenantId"=@tenant AND "AgentId"=@agent
            """ + suffix, ("tenant",key.TenantId),("agent",key.AgentId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct) || r.GetInt64(0)==0) return null;
        return new(new(key,r.GetGuid(1),r.GetInt64(0)),r.GetBoolean(2),checked((ulong)r.GetDecimal(3)),
            r.GetFieldValue<DateTimeOffset>(4),r.GetFieldValue<DateTimeOffset>(5),r.GetFieldValue<DateTimeOffset>(6),r.GetInt64(7),
            r.GetGuid(8),r.GetString(9),JsonSerializer.Deserialize<PresenceMetadata>(r.GetString(10)) ?? throw new InvalidOperationException("invalid-owner-metadata"),
            Nullable<DateTimeOffset>(r,11));
    }

    // Ingress/action writers call this in THEIR existing DbContext transaction,
    // not a separately scoped store read. The shared owner lock lasts until
    // their accepted write commits, so replacement has a real linearization.
    public static async Task<bool> LockEffectiveOwnerAsync(OrchestratorDbContext db, OwnerKey expected, TimeProvider clock, CancellationToken ct) =>
        await LockEffectiveOwnerSnapshotAsync(db, expected, clock, ct).ConfigureAwait(false) is not null;

    // Proposed additive helper: the revision is observed under THIS transaction's
    // owner lock. It orders observations; it is not a captured renewal fence.
    public static async Task<OwnerSnapshot?> LockEffectiveOwnerSnapshotAsync(OrchestratorDbContext db, OwnerKey expected, TimeProvider clock, CancellationToken ct)
    {
        RequireOwner(expected);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL" || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("owner-check-needs-caller-postgres-transaction");
        var current = await ReadOwnerAsync((NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)db.Database.CurrentTransaction.GetDbTransaction(),expected.Client,false,ct,forShare:true);
        return current is not null && current.Owner==expected && current.IsEffective(await EffectiveNowAsync((NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)db.Database.CurrentTransaction.GetDbTransaction(),clock,ct)) ? current : null;
    }

    // The injected TimeProvider still controls deterministic advancement/timers.
    // A slower replica clock may not extend real deadlines: use the later of
    // that trusted clock and PostgreSQL's current clock, AFTER acquiring locks.
    // New relational tests seed a manual clock from real UTC, then advance it;
    // actor-only #160 tests keep their existing fixed clock unchanged.
    internal static async Task<DateTimeOffset> EffectiveNowAsync(NpgsqlConnection c,NpgsqlTransaction? tx,TimeProvider clock,CancellationToken ct)
    {
        await using var cmd=Command(c,tx,"SELECT clock_timestamp()");
        var databaseValue=await cmd.ExecuteScalarAsync(ct);
        var databaseNow=Normalize(databaseValue is DateTimeOffset offset ? offset : new DateTimeOffset((DateTime)databaseValue!));
        var injectedNow=Normalize(clock.GetUtcNow());
        return databaseNow>injectedNow?databaseNow:injectedNow;
    }

    private static OwnershipDisposition? CheckCurrent(OwnerSnapshot? current, OwnerKey expected, DateTimeOffset now) =>
        current is null ? OwnershipDisposition.NoActiveSession :
        current.Owner.Epoch!=expected.Epoch ? OwnershipDisposition.StaleEpoch :
        current.Owner.ConnectionId!=expected.ConnectionId ? OwnershipDisposition.ConnectionMismatch :
        !current.Active || !current.AcceptanceGuardAtUtc.HasValue ? OwnershipDisposition.NoActiveSession :
        current.AuthenticationExpiresAtUtc<=now ? OwnershipDisposition.AuthenticationExpired :
        current.PresenceExpiresAtUtc<=now ? OwnershipDisposition.HeartbeatExpired : null;
    private bool WithinAdmission(AdmissionRequest r, DateTimeOffset now) => r.ReceivedAtUtc<=now &&
        r.AdmissionExpiresAtUtc>now && r.AdmissionExpiresAtUtc>r.ReceivedAtUtc &&
        r.AdmissionExpiresAtUtc<=r.ReceivedAtUtc+_policy.AdmissionLifetime && r.AuthenticationExpiresAtUtc>now;
    private static Reservation ToReservation(ClientKey key, Admission a) => new(new(key,a.ConnectionId,a.Epoch!.Value),
        a.OperationId!.Value,a.Hash!,a.Received!.Value,a.Deadline!.Value,a.Authentication!.Value);
    private static string PayloadHash(AdmissionRequest r) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(r with { MinimumEpoch=0 }))));
    private static void RequireClient(ClientKey key) { if (!key.IsValid) throw new ArgumentException("invalid-client-key"); }
    private static void RequireOwner(OwnerKey key) { RequireClient(key.Client); if(key.ConnectionId==Guid.Empty||key.Epoch<=0) throw new ArgumentException("invalid-owner-key"); }
    private static void RequireMetadata(PresenceMetadata m)
    {
        if (m is null || m.Capabilities is null || (m.AgentVersion?.Length??0)>128 || m.Capabilities.Count>64 || m.Capabilities.Any(c=>c is null||c.Length>128) ||
            m.LegacySpacetimeIdentity is { } legacy && (legacy.Length!=64||!legacy.All(Uri.IsHexDigit)))
            throw new ArgumentException("invalid-presence-metadata");
    }
    private static T? Nullable<T>(NpgsqlDataReader r,int i) where T:struct => r.IsDBNull(i)?null:r.GetFieldValue<T>(i);
    private static (string,object)[] OwnerParameters(OwnerKey key) => [("tenant",key.Client.TenantId),("agent",key.Client.AgentId),("connection",key.ConnectionId),("epoch",key.Epoch)];
    private static NpgsqlCommand Command(NpgsqlConnection c,NpgsqlTransaction? tx,string sql,params (string,object)[] values)
    {
        var cmd=new NpgsqlCommand(sql,c,tx);
        foreach(var (name,value) in values)
        {
            // PostgreSQL cannot infer a type from a standalone NULL parameter.
            if(name=="epoch" && value==DBNull.Value) cmd.Parameters.AddWithValue(name,NpgsqlTypes.NpgsqlDbType.Bigint,DBNull.Value);
            else cmd.Parameters.AddWithValue(name,value);
        }
        return cmd;
    }
    private static async Task<int> ExecuteAsync(NpgsqlConnection c,NpgsqlTransaction tx,string sql,CancellationToken ct,params (string,object)[] values)
    { await using var cmd=Command(c,tx,sql,values); return await cmd.ExecuteNonQueryAsync(ct); }
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection c,NpgsqlTransaction tx,string sql,CancellationToken ct,params (string,object)[] values)
    { await using var cmd=Command(c,tx,sql,values); return (T)(await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("missing-store-row")); }
}
