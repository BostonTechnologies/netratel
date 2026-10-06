// WORK-ONLY test candidate. Not compiled or run. Requires refreshed #148 owner
// model + ownership migration + acceptance-guard migration and guarded store.
// Direct store tests use genuine PostgreSQL transactions and lock barriers;
// they do not claim production actor/physical I/O acceptance coverage.
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class ClientConnectionOwnerGuardPostgresTests(PostgreSqlPersistenceFixture fixture)
{
    public enum BarrierPhase : short { BeforeCommit = 1, BeforeGuard = 2, AfterGuard = 3 }

    [Theory]
    [InlineData(BarrierPhase.BeforeCommit, false)]
    [InlineData(BarrierPhase.BeforeGuard, false)]
    [InlineData(BarrierPhase.BeforeGuard, true)]
    public async Task Fixed_cutoff_crossed_before_guard_rolls_back_entire_replacement(
        BarrierPhase phase, bool expireOriginalAuthentication)
    {
        var connection = await PrepareAsync();
        await using var a = Provider(connection); await using var b = Provider(connection);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = new ClientKey(61, Guid.NewGuid());
        var old = await ActivateAsync(Store(a), await RequestAsync(connection, client, TimeSpan.FromSeconds(30)));
        var input = await RequestAsync(connection, client,
            expireOriginalAuthentication ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(3));
        if (expireOriginalAuthentication) input = input with { AuthenticationExpiresAtUtc = input.ReceivedAtUtc.AddSeconds(3) };
        var reservation = (await Store(b).ReserveAsync(input, budget.Token)).Reservation!;
        await using var barrier = await PgBarrier.HoldAsync(connection, client.AgentId, phase, budget.Token);
        var first = new HeartbeatRequest(reservation.Owner, 1, input.ReceivedAtUtc,
            expireOriginalAuthentication ? input.ReceivedAtUtc.AddMinutes(20) : null);
        var committing = Store(b).CommitAsync(reservation, first, budget.Token);
        try
        {
            await barrier.WaitBlockedAsync(committing, phase == BarrierPhase.BeforeCommit ? "UPDATE" : "COMMIT", budget.Token);
            // A genuinely separate connection still sees the old committed
            // owner while tentative replacement/journal writes are blocked.
            AssertOwnerSame(old, await Store(a).GetCurrentAsync(client, budget.Token));
            await WaitPastDatabaseTimeAsync(connection,
                expireOriginalAuthentication ? input.AuthenticationExpiresAtUtc : input.AdmissionExpiresAtUtc, budget.Token);
        }
        finally { await barrier.ReleaseAsync(); }
        Assert.Equal(OwnershipDisposition.AdmissionExpired, (await committing).Disposition);
        AssertOwnerSame(old, await Store(a).GetCurrentAsync(client, budget.Token));
        Assert.Equal((short)3, await AdmissionStatusAsync(connection, old.Owner, budget.Token));
        Assert.Equal((short)1, await AdmissionStatusAsync(connection, reservation.Owner, budget.Token));
        Assert.Equal(reservation.Owner.Epoch, await LastIssuedAsync(connection, client, budget.Token));
    }

    [Fact]
    public async Task Durable_cancel_committed_before_candidate_lock_prevents_replacement()
    {
        var connection = await PrepareAsync();
        await using var a = Provider(connection); await using var b = Provider(connection);
        var client = new ClientKey(62, Guid.NewGuid());
        var old = await ActivateAsync(Store(a), await RequestAsync(connection, client, TimeSpan.FromSeconds(30)));
        var input = await RequestAsync(connection, client, TimeSpan.FromSeconds(30));
        var candidate = (await Store(b).ReserveAsync(input, default)).Reservation!;
        await Store(a).CancelAdmissionAsync(client, candidate.Owner.ConnectionId, candidate.Owner.Epoch,
            await DatabaseNowAsync(connection, default), default);
        Assert.Equal(OwnershipDisposition.AdmissionCancelled,
            (await Store(b).CommitAsync(candidate, new(candidate.Owner, 1, input.ReceivedAtUtc), default)).Disposition);
        AssertOwnerSame(old, await Store(a).GetCurrentAsync(client, default));
    }

    [Theory]
    [InlineData(BarrierPhase.BeforeGuard)]
    [InlineData(BarrierPhase.AfterGuard)]
    public async Task Owned_IO_cancel_before_durable_commit_then_durable_cancel_prevents_late_retry(BarrierPhase phase)
    {
        var connection = await PrepareAsync();
        await using var a = Provider(connection); await using var b = Provider(connection);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var admissionAuthority = new CancellationTokenSource();
        var client = new ClientKey(63, Guid.NewGuid());
        var old = await ActivateAsync(Store(a), await RequestAsync(connection, client, TimeSpan.FromSeconds(30)));
        var input = await RequestAsync(connection, client, TimeSpan.FromSeconds(30));
        var candidate = (await Store(b).ReserveAsync(input, budget.Token)).Reservation!;
        await using var barrier = await PgBarrier.HoldAsync(connection, client.AgentId, phase, budget.Token);
        var committing = Store(b).CommitAsync(candidate, new(candidate.Owner, 1, input.ReceivedAtUtc), admissionAuthority.Token);
        try
        {
            await barrier.WaitBlockedAsync(committing, "COMMIT", budget.Token);
            admissionAuthority.Cancel(); // exact owned candidate I/O, not another admission
            var error = await Record.ExceptionAsync(async () => await committing.WaitAsync(budget.Token));
            Assert.NotNull(error);
            Assert.True(error is OperationCanceledException or NpgsqlException);
            // A local exception alone is NOT proof of abort. Commit the exact
            // durable cancel, then inspect independent durable authority.
            await Store(a).CancelAdmissionAsync(client, candidate.Owner.ConnectionId, candidate.Owner.Epoch,
                await DatabaseNowAsync(connection, budget.Token), budget.Token);
            AssertOwnerSame(old, await Store(a).GetCurrentAsync(client, budget.Token));
            Assert.Equal((short)2, await AdmissionStatusAsync(connection, candidate.Owner, budget.Token));
            Assert.Equal(OwnershipDisposition.AdmissionCancelled,
                (await Store(b).CommitAsync(candidate, new(candidate.Owner, 1, input.ReceivedAtUtc), budget.Token)).Disposition);
        }
        finally { await barrier.ReleaseAsync(); }
    }

    [Fact]
    public async Task Valid_guard_then_later_commit_processing_delay_is_not_a_final_WAL_clock_promise()
    {
        var connection = await PrepareAsync();
        await using var a = Provider(connection); await using var b = Provider(connection);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = new ClientKey(64, Guid.NewGuid());
        var old = await ActivateAsync(Store(a), await RequestAsync(connection, client, TimeSpan.FromSeconds(30)));
        var input = await RequestAsync(connection, client, TimeSpan.FromSeconds(3));
        var candidate = (await Store(b).ReserveAsync(input, budget.Token)).Reservation!;
        await using var barrier = await PgBarrier.HoldAsync(connection, client.AgentId, BarrierPhase.AfterGuard, budget.Token);
        var committing = Store(b).CommitAsync(candidate, new(candidate.Owner, 1, input.ReceivedAtUtc), budget.Token);
        try
        {
            await barrier.WaitBlockedAsync(committing, "COMMIT", budget.Token);
            AssertOwnerSame(old, await Store(a).GetCurrentAsync(client, budget.Token));
            await WaitPastDatabaseTimeAsync(connection, input.AdmissionExpiresAtUtc, budget.Token);
        }
        finally { await barrier.ReleaseAsync(); }
        var result = await committing;
        Assert.Equal(OwnershipDisposition.Accepted, result.Disposition);
        var durable = (await Store(a).GetCurrentAsync(client, budget.Token))!;
        Assert.Equal(candidate.Owner, durable.Owner);
        Assert.NotNull(durable.AcceptanceGuardAtUtc);
        Assert.True(durable.AcceptanceGuardAtUtc < input.AdmissionExpiresAtUtc);
        Assert.True(await DatabaseNowAsync(connection, budget.Token) >= input.AdmissionExpiresAtUtc);
        // Direct store call uses a test budget, not the physical actor deadline.
        // Production actor expiry still forbids its late ACK/publication and
        // initiates exact cleanup; a committed successor is never rolled back
        // to old authority. This test documents the remaining PG boundary.
    }

    [Fact]
    public async Task Commit_wins_before_waiting_exact_cancel_and_never_restores_predecessor()
    {
        var connection = await PrepareAsync();
        await using var a = Provider(connection); await using var b = Provider(connection);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = new ClientKey(65, Guid.NewGuid());
        var old = await ActivateAsync(Store(a), await RequestAsync(connection, client, TimeSpan.FromSeconds(30)));
        var input = await RequestAsync(connection, client, TimeSpan.FromSeconds(30));
        var candidate = (await Store(b).ReserveAsync(input, budget.Token)).Reservation!;
        await using var barrier = await PgBarrier.HoldAsync(connection, client.AgentId, BarrierPhase.AfterGuard, budget.Token);
        var committing = Store(b).CommitAsync(candidate, new(candidate.Owner, 1, input.ReceivedAtUtc), budget.Token);
        Task<WriteResult>? cancelling = null;
        try
        {
            var committingPid = await barrier.WaitBlockedAsync(committing, "COMMIT", budget.Token);
            cancelling = Store(a).CancelAdmissionAsync(client, candidate.Owner.ConnectionId, candidate.Owner.Epoch,
                await DatabaseNowAsync(connection, budget.Token), budget.Token);
            await WaitForRowLockAsync(connection, cancelling, committingPid, budget.Token);
            Assert.False(cancelling.IsCompleted);
            AssertOwnerSame(old, await Store(a).GetCurrentAsync(client, budget.Token));
        }
        finally { await barrier.ReleaseAsync(); }
        Assert.Equal(OwnershipDisposition.Accepted, (await committing).Disposition);
        Assert.Equal(OwnershipDisposition.Accepted, (await cancelling!).Disposition);
        var retired = (await Store(a).GetCurrentAsync(client, budget.Token))!;
        Assert.Equal(candidate.Owner, retired.Owner); Assert.False(retired.Active);
        Assert.NotNull(retired.AcceptanceGuardAtUtc);
        Assert.Equal(OwnershipDisposition.StaleEpoch,
            (await Store(a).RecordHeartbeatAsync(new(old.Owner, 2, await DatabaseNowAsync(connection, budget.Token)), budget.Token)).Disposition);
    }

    [Fact]
    public async Task Lost_application_commit_reply_is_unknown_to_caller_and_retry_uses_same_durable_proof()
    {
        var connection = await PrepareAsync();
        await using var a = Provider(connection); await using var b = Provider(connection);
        var client = new ClientKey(66, Guid.NewGuid());
        var old = await ActivateAsync(Store(a), await RequestAsync(connection, client, TimeSpan.FromSeconds(30)));
        var input = await RequestAsync(connection, client, TimeSpan.FromSeconds(30));
        var candidate = (await Store(b).ReserveAsync(input, default)).Reservation!;
        async Task<WriteResult> DropApplicationReplyAsync()
        {
            await Store(b).CommitAsync(candidate, new(candidate.Owner, 1, input.ReceivedAtUtc), default);
            throw new IOException("test-lost-application-commit-reply");
        }
        await Assert.ThrowsAsync<IOException>(DropApplicationReplyAsync);
        // Deliberate application-result loss, NOT a claimed PG wire/WAL fault.
        var proof = (await Store(a).GetCurrentAsync(client, default))!;
        Assert.Equal(candidate.Owner, proof.Owner); Assert.True(proof.Active);
        Assert.NotNull(proof.AcceptanceGuardAtUtc);
        var retry = await Store(a).CommitAsync(candidate, new(candidate.Owner, 1, input.ReceivedAtUtc), default);
        Assert.Equal(OwnershipDisposition.Duplicate, retry.Disposition);
        Assert.Equal(proof.AcceptanceGuardAtUtc, retry.Current!.AcceptanceGuardAtUtc);
        await Store(a).CancelAdmissionAsync(client, candidate.Owner.ConnectionId, candidate.Owner.Epoch,
            await DatabaseNowAsync(connection, default), default);
        var retired = (await Store(b).GetCurrentAsync(client, default))!;
        Assert.Equal(candidate.Owner, retired.Owner); Assert.False(retired.Active);
        Assert.Equal(proof.AcceptanceGuardAtUtc, retired.AcceptanceGuardAtUtc);
        Assert.Equal(OwnershipDisposition.StaleEpoch,
            (await Store(a).RecordHeartbeatAsync(new(old.Owner, 2, await DatabaseNowAsync(connection, default)), default)).Disposition);
    }

    [Fact]
    public async Task Early_constraint_draining_rejects_and_rolls_back_raw_replacement()
    {
        var connection = await PrepareAsync(); await using var provider = Provider(connection);
        var client = new ClientKey(67, Guid.NewGuid());
        var old = await ActivateAsync(Store(provider), await RequestAsync(connection, client, TimeSpan.FromSeconds(30)));
        var input = await RequestAsync(connection, client, TimeSpan.FromSeconds(30));
        var candidate = (await Store(provider).ReserveAsync(input, default)).Reservation!;
        await using (var c = new NpgsqlConnection(connection))
        {
            await c.OpenAsync(); await using var tx = await c.BeginTransactionAsync();
            await StageRawReplacementAsync(c, tx, candidate, input.ReceivedAtUtc);
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(c, tx, "SET CONSTRAINTS ALL IMMEDIATE", default));
            Assert.Equal("NR003", error.SqlState);
            Assert.Equal("CK_ClientConnectionOwners_AcceptanceGuard", error.ConstraintName);
            await tx.RollbackAsync();
        }
        AssertOwnerSame(old, await Store(provider).GetCurrentAsync(client, default));
        Assert.Equal((short)1, await AdmissionStatusAsync(connection, candidate.Owner, default));
    }

    [Fact]
    public async Task Two_replacements_in_one_transaction_reject_and_preserve_original_owner()
    {
        var connection = await PrepareAsync(); await using var provider = Provider(connection);
        var client = new ClientKey(68, Guid.NewGuid());
        var old = await ActivateAsync(Store(provider), await RequestAsync(connection, client, TimeSpan.FromSeconds(30)));
        var a = await RequestAsync(connection, client, TimeSpan.FromSeconds(30));
        var b = await RequestAsync(connection, client, TimeSpan.FromSeconds(30));
        var first = (await Store(provider).ReserveAsync(a, default)).Reservation!;
        var second = (await Store(provider).ReserveAsync(b, default)).Reservation!;
        await using (var c = new NpgsqlConnection(connection))
        {
            await c.OpenAsync(); await using var tx = await c.BeginTransactionAsync();
            await StageRawReplacementAsync(c, tx, first, a.ReceivedAtUtc);
            await StageRawReplacementAsync(c, tx, second, b.ReceivedAtUtc);
            var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
            Assert.Equal("NR002", error.SqlState);
            Assert.Equal("CK_ClientConnectionOwners_AcceptanceGuard", error.ConstraintName);
        }
        AssertOwnerSame(old, await Store(provider).GetCurrentAsync(client, default));
        Assert.Equal((short)1, await AdmissionStatusAsync(connection, first.Owner, default));
        Assert.Equal((short)1, await AdmissionStatusAsync(connection, second.Owner, default));
    }

    [Fact]
    public async Task Reserved_original_times_cannot_be_extended_with_unchanged_operation_and_hash()
    {
        var connection = await PrepareAsync(); await using var provider = Provider(connection);
        var client = new ClientKey(70, Guid.NewGuid());
        var old = await ActivateAsync(Store(provider), await RequestAsync(connection, client, TimeSpan.FromSeconds(30)));
        var input = await RequestAsync(connection, client, TimeSpan.FromSeconds(30));
        var candidate = (await Store(provider).ReserveAsync(input, default)).Reservation!;
        await using (var c = new NpgsqlConnection(connection))
        {
            await c.OpenAsync(); await using var tx = await c.BeginTransactionAsync();
            var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(c, tx, """
                UPDATE "ClientConnectionAdmissions" SET
                  "AdmissionExpiresAtUtc"="AdmissionExpiresAtUtc"+INTERVAL '1 minute',
                  "AuthenticationExpiresAtUtc"="AuthenticationExpiresAtUtc"+INTERVAL '1 minute'
                WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "ConnectionId"=@connection
                """, default, ("tenant", client.TenantId), ("agent", client.AgentId), ("connection", candidate.Owner.ConnectionId)));
            Assert.Equal("NR004", error.SqlState);
            Assert.Equal("CK_ClientConnectionAdmissions_Immutable", error.ConstraintName);
            await tx.RollbackAsync();
        }
        var forged = candidate with { AdmissionExpiresAtUtc = candidate.AdmissionExpiresAtUtc.AddMinutes(1) };
        Assert.Equal(OwnershipDisposition.AdmissionBodyConflict,
            (await Store(provider).CommitAsync(forged, new(candidate.Owner, 1, input.ReceivedAtUtc), default)).Disposition);
        AssertOwnerSame(old, await Store(provider).GetCurrentAsync(client, default));
        Assert.Equal((short)1, await AdmissionStatusAsync(connection, candidate.Owner, default));
    }

    [Fact]
    public async Task Ordinary_heartbeat_renewal_duplicate_and_retirement_never_refresh_guard_stamp()
    {
        var connection = await PrepareAsync(); await using var provider = Provider(connection);
        var client = new ClientKey(69, Guid.NewGuid());
        var input = await RequestAsync(connection, client, TimeSpan.FromSeconds(30));
        var owner = await ActivateAsync(Store(provider), input);
        Assert.NotNull(owner.AcceptanceGuardAtUtc);
        var receipt = await DatabaseNowAsync(connection, default);
        var heartbeat = new HeartbeatRequest(owner.Owner, 2, receipt, owner.AuthenticationExpiresAtUtc.AddMinutes(1));
        Assert.Equal(OwnershipDisposition.Accepted, (await Store(provider).RenewAsync(heartbeat, default)).Disposition);
        var renewed = (await Store(provider).GetCurrentAsync(client, default))!;
        Assert.Equal(owner.AcceptanceGuardAtUtc, renewed.AcceptanceGuardAtUtc);
        Assert.Equal(owner.Revision + 1, renewed.Revision);
        Assert.Equal(OwnershipDisposition.Duplicate, (await Store(provider).RenewAsync(heartbeat, default)).Disposition);
        AssertOwnerSame(renewed, await Store(provider).GetCurrentAsync(client, default));
        await Store(provider).RetireAsync(owner.Owner, RetirementReason.ExplicitClose, null, default);
        var retired = (await Store(provider).GetCurrentAsync(client, default))!;
        Assert.False(retired.Active); Assert.Equal(owner.AcceptanceGuardAtUtc, retired.AcceptanceGuardAtUtc);
    }

    private static void AssertOwnerSame(OwnerSnapshot expected, OwnerSnapshot? actual)
    {
        Assert.NotNull(actual);
        // Include every persisted scalar, metadata/capability value and proof;
        // record equality alone compares separately deserialized list instances.
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    private async Task<string> PrepareAsync()
    {
        var connection = await fixture.CreateDatabaseAsync();
        await using var provider = Provider(connection); await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.MigrateAsync();
        // Copy the work-only test SQL to the integrated test output explicitly;
        // never install production guards from tests instead of real migration.
        var sql = TestBarrierSql;
        await using var c = new NpgsqlConnection(connection); await c.OpenAsync();
        await ExecuteAsync(c, null, sql, default);
        return connection;
    }
    private const string TestBarrierSql = """
-- TEST DATABASE ONLY. Unexecuted draft; never ship as a migration.
CREATE TABLE public."OwnershipCommitTestBarriers" (
    "AgentId" uuid PRIMARY KEY,
    "LockKey" bigint NOT NULL,
    -- 1 after replacement UPDATE, 2 before deferred guard, 3 after guard.
    "Phase" smallint NOT NULL CHECK ("Phase" IN (1,2,3))
);
CREATE FUNCTION public.nr_test_owner_commit_barrier()
RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public
AS $barrier$
DECLARE lock_key bigint;
BEGIN
    SELECT "LockKey" INTO lock_key FROM public."OwnershipCommitTestBarriers"
      WHERE "AgentId" = NEW."AgentId" AND "Phase" = TG_ARGV[0]::smallint;
    IF FOUND THEN
        PERFORM pg_catalog.pg_advisory_xact_lock(lock_key);
    END IF;
    RETURN NULL;
END;
$barrier$;

CREATE TRIGGER nr_test_owner_before_commit
AFTER UPDATE ON public."ClientConnectionOwners"
FOR EACH ROW
WHEN (OLD."ConnectionEpoch" IS DISTINCT FROM NEW."ConnectionEpoch")
EXECUTE FUNCTION public.nr_test_owner_commit_barrier('1');

-- Alphabetical order for same-kind/same-event triggers is documented by PG.
-- aaa precedes nr_owner_acceptance_guard_update; zzz follows it.
CREATE CONSTRAINT TRIGGER aaa_nr_test_owner_before_guard
AFTER UPDATE ON public."ClientConnectionOwners"
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
WHEN (OLD."ConnectionEpoch" IS DISTINCT FROM NEW."ConnectionEpoch")
EXECUTE FUNCTION public.nr_test_owner_commit_barrier('2');

CREATE CONSTRAINT TRIGGER zzz_nr_test_owner_after_guard
AFTER UPDATE ON public."ClientConnectionOwners"
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
WHEN (OLD."ConnectionEpoch" IS DISTINCT FROM NEW."ConnectionEpoch")
EXECUTE FUNCTION public.nr_test_owner_commit_barrier('3');

-- Guard stamp-only updates keep epoch unchanged, so none of these requeue.

""";

    private static ServiceProvider Provider(string connection) => new ServiceCollection()
        .AddDbContext<OrchestratorDbContext>(o => o.UseNpgsql(connection))
        .AddSingleton(TimeProvider.System)
        .AddSingleton(new OwnershipPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)))
        .AddSingleton<IClientConnectionEpochStore, ClientConnectionEpochStore>()
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    private static IClientConnectionEpochStore Store(IServiceProvider p) => p.GetRequiredService<IClientConnectionEpochStore>();
    private static async Task<AdmissionRequest> RequestAsync(string connection, ClientKey client, TimeSpan lifetime)
    {
        var now = await DatabaseNowAsync(connection, default);
        return new(client, Guid.NewGuid(), Guid.NewGuid(), 0, now, now + lifetime, now.AddMinutes(10),
            new("guard-regression", ["presence"], null));
    }
    private static async Task<OwnerSnapshot> ActivateAsync(IClientConnectionEpochStore store, AdmissionRequest input)
    {
        var reservation = (await store.ReserveAsync(input, default)).Reservation!;
        var result = await store.CommitAsync(reservation, new(reservation.Owner, 1, input.ReceivedAtUtc), default);
        Assert.Equal(OwnershipDisposition.Accepted, result.Disposition);
        Assert.NotNull(result.Current!.AcceptanceGuardAtUtc);
        return result.Current!;
    }
    private static async Task<DateTimeOffset> DatabaseNowAsync(string connection, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(connection); await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT clock_timestamp()", c);
        await using var r = await cmd.ExecuteReaderAsync(ct); Assert.True(await r.ReadAsync(ct));
        return r.GetFieldValue<DateTimeOffset>(0);
    }
    private static async Task WaitPastDatabaseTimeAsync(string connection, DateTimeOffset deadline, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        while (await DatabaseNowAsync(connection, ct) < deadline)
            Assert.True(await timer.WaitForNextTickAsync(ct));
    }
    private static async Task<short> AdmissionStatusAsync(string connection, OwnerKey owner, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(connection); await c.OpenAsync(ct);
        await using var cmd = Command(c, null, "SELECT \"Status\" FROM \"ClientConnectionAdmissions\" WHERE \"TenantId\"=@tenant AND \"AgentId\"=@agent AND \"ConnectionId\"=@connection",
            ("tenant", owner.Client.TenantId), ("agent", owner.Client.AgentId), ("connection", owner.ConnectionId));
        return (short)(await cmd.ExecuteScalarAsync(ct))!;
    }
    private static async Task<long> LastIssuedAsync(string connection, ClientKey client, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(connection); await c.OpenAsync(ct);
        await using var cmd = Command(c, null, "SELECT \"LastIssuedEpoch\" FROM \"ClientConnectionEpochs\" WHERE \"TenantId\"=@tenant AND \"AgentId\"=@agent",
            ("tenant", client.TenantId), ("agent", client.AgentId));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
    private static async Task StageRawReplacementAsync(NpgsqlConnection c, NpgsqlTransaction tx, Reservation candidate, DateTimeOffset received)
    {
        // Intentional direct SQL boundary abuse, not a substitute store used by
        // normal lifecycle tests. Keep lock ordering identical to production.
        await ExecuteAsync(c, tx, "SELECT 1 FROM \"ClientConnectionEpochs\" WHERE \"TenantId\"=@tenant AND \"AgentId\"=@agent FOR UPDATE", default,
            ("tenant", candidate.Owner.Client.TenantId), ("agent", candidate.Owner.Client.AgentId));
        await ExecuteAsync(c, tx, """
            UPDATE "ClientConnectionOwners" o SET
              "ConnectionEpoch"=a."ConnectionEpoch","ConnectionId"=a."ConnectionId","Active"=TRUE,
              "LastHeartbeatSequence"=1,"LastReceivedAtUtc"=@received,"PresenceExpiresAtUtc"=@received+INTERVAL '60 seconds',
              "AuthenticationExpiresAtUtc"=a."AuthenticationExpiresAtUtc","OwnerRevision"=o."OwnerRevision"+1,
              "StartOperationId"=a."OperationId","AdmissionPayloadHash"=a."PayloadHash","MetadataJson"=a."MetadataJson",
              "AcceptanceClockFloorUtc"=clock_timestamp(),"AcceptanceGuardAtUtc"=NULL
            FROM "ClientConnectionAdmissions" a WHERE o."TenantId"=@tenant AND o."AgentId"=@agent
              AND a."TenantId"=o."TenantId" AND a."AgentId"=o."AgentId" AND a."ConnectionId"=@connection AND a."Status"=1;
            UPDATE "ClientConnectionAdmissions" SET "Status"=3
              WHERE "TenantId"=@tenant AND "AgentId"=@agent AND "ConnectionId"=@connection
            """, default, ("tenant", candidate.Owner.Client.TenantId), ("agent", candidate.Owner.Client.AgentId),
            ("connection", candidate.Owner.ConnectionId), ("received", received));
    }
    private static async Task WaitForRowLockAsync(string connection, Task operation, int committingPid, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(connection); await c.OpenAsync(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        do
        {
            await using var cmd = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=@database AND query LIKE '%nr-owner-reserve%' AND @committing=ANY(pg_blocking_pids(pid)))", c);
            cmd.Parameters.AddWithValue("database", c.Database);
            cmd.Parameters.AddWithValue("committing", committingPid);
            if ((bool)(await cmd.ExecuteScalarAsync(ct))!) { Assert.False(operation.IsCompleted); return; }
            Assert.False(operation.IsCompleted); Assert.True(await timer.WaitForNextTickAsync(ct));
        } while (true);
    }
    private static NpgsqlCommand Command(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params (string, object)[] values)
    {
        var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
    private static async Task ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, CancellationToken ct, params (string, object)[] values)
    { await using var cmd = Command(c, tx, sql, values); await cmd.ExecuteNonQueryAsync(ct); }

    private sealed class PgBarrier(NpgsqlConnection blocker, string connection, long key) : IAsyncDisposable
    {
        private bool _released;
        private static long _nextKey = DateTime.UtcNow.Ticks;
        public static async Task<PgBarrier> HoldAsync(string connection, Guid agent, BarrierPhase phase, CancellationToken ct)
        {
            var blocker = new NpgsqlConnection(connection); await blocker.OpenAsync(ct);
            var key = Interlocked.Increment(ref _nextKey);
            try
            {
                await ExecuteAsync(blocker, null, "SELECT pg_advisory_lock(@key)", ct, ("key", key));
                await ExecuteAsync(blocker, null, "INSERT INTO \"OwnershipCommitTestBarriers\" (\"AgentId\",\"LockKey\",\"Phase\") VALUES (@agent,@key,@phase)", ct,
                    ("agent", agent), ("key", key), ("phase", (short)phase));
                return new(blocker, connection, key);
            }
            catch { await blocker.DisposeAsync(); throw; }
        }
        public async Task<int> WaitBlockedAsync(Task operation, string queryFragment, CancellationToken ct)
        {
            await using var observer = new NpgsqlConnection(connection); await observer.OpenAsync(ct);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            do
            {
                await using var cmd = Command(observer, null, """
                    SELECT pid FROM pg_stat_activity WHERE datname=@database
                      AND @blocker = ANY(pg_blocking_pids(pid)) AND wait_event='advisory'
                      AND query LIKE @query ORDER BY pid LIMIT 1
                    """, ("database", observer.Database), ("blocker", blocker.ProcessID), ("query", "%" + queryFragment + "%"));
                if (await cmd.ExecuteScalarAsync(ct) is int committingPid) { Assert.False(operation.IsCompleted); return committingPid; }
                Assert.False(operation.IsCompleted); Assert.True(await timer.WaitForNextTickAsync(ct));
            } while (true);
        }
        public async Task ReleaseAsync()
        {
            if (_released) return;
            _released = true;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ExecuteAsync(blocker, null, "SELECT pg_advisory_unlock(@key)", budget.Token, ("key", key));
        }
        public async ValueTask DisposeAsync()
        {
            try { await ReleaseAsync(); }
            finally { await blocker.DisposeAsync(); }
        }
    }
}
