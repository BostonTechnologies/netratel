// Work-only regression draft. Intended for NetRatel.Tests/Infrastructure after
// the refreshed #148 migration/model are integrated. NOT run or compiled.
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class ClientConnectionOwnershipPostgresTests(PostgreSqlPersistenceFixture fixture)
{
    [Fact]
    public async Task Reservation_does_not_fence_committed_owner_and_cancel_never_reuses_epoch()
    {
        var connection = await PrepareAsync();
        var clock = new AdvancingClock();
        await using var a = Provider(connection,clock);
        await using var b = Provider(connection,clock);
        var first = Store(a); var second = Store(b); var client = new ClientKey(31,Guid.NewGuid());
        var current = await ActivateAsync(first,Request(client,clock));
        (await second.AllocateAsync(client,100,default)).Should().Be(101);
        var candidate = (await second.ReserveAsync(Request(client,clock),default)).Reservation!;
        candidate.Owner.Epoch.Should().BeGreaterThan(current.Owner.Epoch);
        (await first.RecordHeartbeatAsync(new(current.Owner,2,clock.GetUtcNow()),default)).Disposition.Should().Be(OwnershipDisposition.Accepted);
        var snapshot = (await second.GetCurrentAsync(client,default))!;
        snapshot.Owner.Should().Be(current.Owner); snapshot.Active.Should().BeTrue(); snapshot.Sequence.Should().Be(2);
        (await second.CancelAdmissionAsync(client,candidate.Owner.ConnectionId,candidate.Owner.Epoch,clock.GetUtcNow(),default))
            .Disposition.Should().Be(OwnershipDisposition.Accepted);
        (await second.CommitAsync(candidate,new(candidate.Owner,1,clock.GetUtcNow()),default))
            .Disposition.Should().Be(OwnershipDisposition.AdmissionCancelled);
        var after = (await second.ReserveAsync(Request(client,clock),default)).Reservation!;
        after.Owner.Epoch.Should().BeGreaterThan(candidate.Owner.Epoch);
        (await first.GetCurrentAsync(client,default))!.Owner.Should().Be(current.Owner);
    }

    [Fact]
    public async Task First_heartbeat_commit_fences_old_replica_renewal_close_and_timer()
    {
        var connection = await PrepareAsync(); var clock = new AdvancingClock();
        await using var a = Provider(connection,clock); await using var b = Provider(connection,clock);
        var first = Store(a); var second = Store(b); var client = new ClientKey(32,Guid.NewGuid());
        var old = await ActivateAsync(first,Request(client,clock));
        var candidate = (await second.ReserveAsync(Request(client,clock),default)).Reservation!;
        (await second.CommitAsync(candidate,new(candidate.Owner,1,clock.GetUtcNow()),default)).Disposition.Should().Be(OwnershipDisposition.Accepted);
        (await first.RenewAsync(new(old.Owner,2,clock.GetUtcNow(),old.AuthenticationExpiresAtUtc.AddHours(1)),default))
            .Disposition.Should().Be(OwnershipDisposition.StaleEpoch);
        await first.RetireAsync(old.Owner,RetirementReason.ExplicitClose,null,default);
        await first.RetireAsync(old.Owner,RetirementReason.AuthenticationExpiry,old.AuthenticationExpiresAtUtc,default);
        await first.CancelAdmissionAsync(client,old.Owner.ConnectionId,null,clock.GetUtcNow(),default);
        var active=(await first.GetCurrentAsync(client,default))!;
        active.Owner.Should().Be(candidate.Owner); active.Active.Should().BeTrue(); active.Sequence.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_reservations_are_unique_and_older_candidate_can_commit_despite_newer_reservation()
    {
        var connection=await PrepareAsync(); var clock=new AdvancingClock();
        await using var a=Provider(connection,clock); await using var b=Provider(connection,clock);
        var client=new ClientKey(33,Guid.NewGuid());
        var requests=Enumerable.Range(0,16).Select(_=>Request(client,clock)).ToArray();
        var reservations=await Task.WhenAll(requests.Select((r,i)=>(i%2==0?Store(a):Store(b)).ReserveAsync(r,default)));
        reservations.Should().OnlyContain(r=>r.Disposition==OwnershipDisposition.Accepted);
        var ordered=reservations.Select(r=>r.Reservation!).OrderBy(r=>r.Owner.Epoch).ToArray();
        ordered.Select(r=>r.Owner.Epoch).Should().Equal(Enumerable.Range(1,16).Select(i=>(long)i));
        (await Store(a).CommitAsync(ordered[0],new(ordered[0].Owner,1,clock.GetUtcNow()),default))
            .Disposition.Should().Be(OwnershipDisposition.Accepted,"a higher reservation is not a committed replacement");
        (await Store(b).GetCurrentAsync(client,default))!.Owner.Should().Be(ordered[0].Owner);
        (await Store(b).CommitAsync(ordered[^1],new(ordered[^1].Owner,1,clock.GetUtcNow()),default))
            .Disposition.Should().Be(OwnershipDisposition.Accepted);
        (await Store(a).CommitAsync(ordered[1],new(ordered[1].Owner,1,clock.GetUtcNow()),default))
            .Disposition.Should().Be(OwnershipDisposition.StaleEpoch);
    }

    [Fact]
    public async Task Delayed_reserve_does_not_block_valid_heartbeat_or_extend_its_fixed_deadline()
    {
        var connection=await PrepareAsync(); var clock=new AdvancingClock();
        await using var a=Provider(connection,clock); await using var b=Provider(connection,clock);
        var client=new ClientKey(34,Guid.NewGuid()); var current=await ActivateAsync(Store(a),Request(client,clock));
        var candidate=Request(client,clock);
        await using var blocker=new NpgsqlConnection(connection); await blocker.OpenAsync();
        await using var tx=await blocker.BeginTransactionAsync();
        await LockCounterAsync(blocker,tx,client);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var waiting=Store(b).ReserveAsync(candidate,timeout.Token);
        try
        {
            await WaitBlockedAsync(connection,"ClientConnectionEpochs",waiting,timeout.Token);
            var heartbeat=await Store(a).RecordHeartbeatAsync(new(current.Owner,2,clock.GetUtcNow()),timeout.Token);
            heartbeat.Disposition.Should().Be(OwnershipDisposition.Accepted);
            clock.Advance(TimeSpan.FromSeconds(31));
        }
        finally { await tx.RollbackAsync(); }
        (await waiting).Disposition.Should().Be(OwnershipDisposition.AdmissionExpired);
        var active=(await Store(a).GetCurrentAsync(client,timeout.Token))!;
        active.Owner.Should().Be(current.Owner); active.Active.Should().BeTrue(); active.Sequence.Should().Be(2);
    }

    [Fact]
    public async Task Cancel_before_reserve_completion_survives_independent_provider_and_late_start()
    {
        var connection=await PrepareAsync(); var clock=new AdvancingClock();
        await using var a=Provider(connection,clock); await using var b=Provider(connection,clock);
        var client=new ClientKey(35,Guid.NewGuid()); var request=Request(client,clock);
        await Store(a).CancelAdmissionAsync(client,request.ConnectionId,null,clock.GetUtcNow(),default);
        (await Store(b).ReserveAsync(request,default)).Disposition.Should().Be(OwnershipDisposition.AdmissionCancelled);
        await using var restarted=Provider(connection,clock);
        (await Store(restarted).ReserveAsync(request,default)).Disposition.Should().Be(OwnershipDisposition.AdmissionCancelled);
        clock.Advance(TimeSpan.FromSeconds(31));
        (await Store(restarted).ReserveAsync(request,default)).Disposition.Should().Be(OwnershipDisposition.AdmissionExpired);
        (await Store(restarted).ReserveAsync(Request(client,clock),default)).Disposition.Should().Be(OwnershipDisposition.Accepted);
    }

    [Fact]
    public async Task Renewal_preserves_owner_and_duplicate_sequence_cannot_extend_expiry_or_retire_renewed_owner()
    {
        var connection=await PrepareAsync(); var clock=new AdvancingClock();
        await using var a=Provider(connection,clock); await using var b=Provider(connection,clock);
        var client=new ClientKey(36,Guid.NewGuid());
        var initial=Request(client,clock) with { AuthenticationExpiresAtUtc=clock.GetUtcNow().AddSeconds(30) };
        var current=await ActivateAsync(Store(a),initial);
        clock.Advance(TimeSpan.FromSeconds(2));
        var renewedExpiry=current.AuthenticationExpiresAtUtc.AddMinutes(10);
        var renewal=new HeartbeatRequest(current.Owner,2,clock.GetUtcNow(),renewedExpiry);
        (await Store(a).RenewAsync(renewal,default)).Disposition.Should().Be(OwnershipDisposition.Accepted);
        (await Store(b).RenewAsync(renewal with { ValidatedRenewedAuthenticationExpiresAtUtc=renewedExpiry.AddHours(1) },default))
            .Disposition.Should().Be(OwnershipDisposition.Duplicate);
        clock.Advance(TimeSpan.FromSeconds(58));
        await Store(b).RetireAsync(current.Owner,RetirementReason.AuthenticationExpiry,current.AuthenticationExpiresAtUtc,default);
        await Store(b).RetireAsync(current.Owner,RetirementReason.HeartbeatExpiry,current.PresenceExpiresAtUtc,default);
        var active=(await Store(b).GetCurrentAsync(client,default))!;
        active.Owner.Should().Be(current.Owner); active.Active.Should().BeTrue(); active.Sequence.Should().Be(2);
        active.AuthenticationExpiresAtUtc.Should().Be(renewedExpiry);
        active.Revision.Should().Be(current.Revision+1);
        await Store(a).RetireAsync(current.Owner,RetirementReason.ExplicitClose,null,default);
        (await Store(b).RenewAsync(new(current.Owner,3,clock.GetUtcNow(),renewedExpiry.AddMinutes(1)),default))
            .Disposition.Should().Be(OwnershipDisposition.NoActiveSession);
    }

    [Fact]
    public async Task Accepted_ingress_share_lock_orders_replacement_commit_but_not_new_reservation()
    {
        var connection=await PrepareAsync(); var clock=new AdvancingClock();
        await using var a=Provider(connection,clock); await using var b=Provider(connection,clock);
        var client=new ClientKey(37,Guid.NewGuid()); var current=await ActivateAsync(Store(a),Request(client,clock));
        await using var ingress=new NpgsqlConnection(connection); await ingress.OpenAsync();
        await using var tx=await ingress.BeginTransactionAsync();
        await using var locked=new NpgsqlCommand("SELECT * FROM \"ClientConnectionOwners\" WHERE \"TenantId\"=@tenant AND \"AgentId\"=@agent FOR SHARE",ingress,tx);
        locked.Parameters.AddWithValue("tenant",client.TenantId); locked.Parameters.AddWithValue("agent",client.AgentId);
        await using(var reader=await locked.ExecuteReaderAsync()) (await reader.ReadAsync()).Should().BeTrue();
        var reserved=(await Store(b).ReserveAsync(Request(client,clock),default)).Reservation!;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var takeover=Store(b).CommitAsync(reserved,new(reserved.Owner,1,clock.GetUtcNow()),timeout.Token);
        await WaitBlockedAsync(connection,"ClientConnectionOwners",takeover,timeout.Token);
        (await Store(a).GetCurrentAsync(client,timeout.Token))!.Owner.Should().Be(current.Owner);
        // In production this is the actual Services/Monitoring accepted
        // write under its owner FOR SHARE lock, not a bare unlocked read.
        await tx.CommitAsync(timeout.Token);
        (await takeover).Disposition.Should().Be(OwnershipDisposition.Accepted);
        (await Store(a).GetCurrentAsync(client,timeout.Token))!.Owner.Should().Be(reserved.Owner);
    }

    [Fact]
    public async Task First_late_cancellation_fixes_its_window_and_repeat_does_not_extend_it()
    {
        var connection=await PrepareAsync();var clock=new AdvancingClock();
        await using var a=Provider(connection,clock);await using var b=Provider(connection,clock);
        var client=new ClientKey(40,Guid.NewGuid());var request=Request(client,clock);
        (await Store(a).ReserveAsync(request,default)).Disposition.Should().Be(OwnershipDisposition.Accepted);
        clock.Advance(TimeSpan.FromSeconds(29));
        await Store(a).CancelAdmissionAsync(client,request.ConnectionId,null,clock.GetUtcNow(),default);
        clock.Advance(TimeSpan.FromSeconds(2));
        var retimestamped=request with { ReceivedAtUtc=clock.GetUtcNow(),AdmissionExpiresAtUtc=clock.GetUtcNow().AddSeconds(30) };
        (await Store(b).ReserveAsync(retimestamped,default)).Disposition.Should().Be(OwnershipDisposition.AdmissionCancelled,
            "first cancellation at t29 retains the exact connection until t59, beyond the original t30 deadline");
        await Store(b).CancelAdmissionAsync(client,request.ConnectionId,null,clock.GetUtcNow(),default);
        clock.Advance(TimeSpan.FromSeconds(29));
        (await Store(a).ReserveAsync(request,default)).Disposition.Should().Be(OwnershipDisposition.AdmissionExpired);
        retimestamped=request with { ReceivedAtUtc=clock.GetUtcNow(),AdmissionExpiresAtUtc=clock.GetUtcNow().AddSeconds(30) };
        (await Store(a).ReserveAsync(retimestamped,default)).Disposition.Should().Be(OwnershipDisposition.Accepted,
            "the repeated t31 cancellation must not extend the original t59 tombstone to t61");
    }

    [Fact]
    public async Task Provider_check_constraint_rejects_null_epoch_and_expiry_in_pending_row()
    {
        var connection=await PrepareAsync();
        await using var c=new NpgsqlConnection(connection);await c.OpenAsync();
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO "ClientConnectionAdmissions" ("TenantId","AgentId","ConnectionId","ConnectionEpoch","OperationId","PayloadHash",
              "ReceivedAtUtc","AdmissionExpiresAtUtc","AuthenticationExpiresAtUtc","MetadataJson","Status","RetainUntilUtc")
            VALUES (41,@agent,@connection,NULL,@operation,@hash,clock_timestamp(),NULL,NULL,'{}'::jsonb,1,clock_timestamp()+interval '30 seconds')
            """,c);
        cmd.Parameters.AddWithValue("agent",Guid.NewGuid());cmd.Parameters.AddWithValue("connection",Guid.NewGuid());
        cmd.Parameters.AddWithValue("operation",Guid.NewGuid());cmd.Parameters.AddWithValue("hash",new string('a',64));
        Func<Task> write=async()=>{await cmd.ExecuteNonQueryAsync();};
        var failure=await write.Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        failure.Which.ConstraintName.Should().Be("CK_ClientConnectionAdmissions_Pending");
    }

    [Fact]
    public async Task Pending_and_cancelled_history_bounds_hold_across_replicas_and_expiry_releases_barrier()
    {
        var connection=await PrepareAsync(); var clock=new AdvancingClock();
        await using var a=Provider(connection,clock); await using var b=Provider(connection,clock);
        var client=new ClientKey(39,Guid.NewGuid());
        var requests=Enumerable.Range(0,17).Select(_=>Request(client,clock)).ToArray();
        for(var i=0;i<16;i++)
            (await Store(i%2==0?a:b).ReserveAsync(requests[i],default)).Disposition.Should().Be(OwnershipDisposition.Accepted);
        (await Store(b).ReserveAsync(requests[16],default)).Disposition.Should().Be(OwnershipDisposition.AdmissionCapacityExceeded);
        // Sixteen pending cancellations plus unknown exact IDs fill the same
        // durable bounded history; repeated cancel does not renew its expiry.
        for(var i=0;i<16;i++) await Store(a).CancelAdmissionAsync(client,requests[i].ConnectionId,null,clock.GetUtcNow(),default);
        for(var i=16;i<64;i++) await Store(b).CancelAdmissionAsync(client,Guid.NewGuid(),null,clock.GetUtcNow(),default);
        var overflow=Request(client,clock);
        await Store(a).CancelAdmissionAsync(client,overflow.ConnectionId,null,clock.GetUtcNow(),default);
        (await Store(b).ReserveAsync(overflow,default)).Disposition.Should().Be(OwnershipDisposition.AdmissionCapacityExceeded);
        await using(var c=new NpgsqlConnection(connection))
        {
            await c.OpenAsync();
            await using var count=new NpgsqlCommand("SELECT count(*) FROM \"ClientConnectionAdmissions\" WHERE \"TenantId\"=@tenant AND \"AgentId\"=@agent",c);
            count.Parameters.AddWithValue("tenant",client.TenantId);count.Parameters.AddWithValue("agent",client.AgentId);
            ((long)(await count.ExecuteScalarAsync())!).Should().Be(64);
        }
        clock.Advance(TimeSpan.FromSeconds(31));
        (await Store(b).ReserveAsync(overflow,default)).Disposition.Should().Be(OwnershipDisposition.AdmissionExpired);
        (await Store(b).ReserveAsync(Request(client,clock),default)).Disposition.Should().Be(OwnershipDisposition.Accepted);
    }

    [Fact]
    public async Task Migration_reservation_seed_does_not_reconstruct_active_owner_and_restart_keeps_committed_revision()
    {
        var connection=await PrepareAsync(); var clock=new AdvancingClock();
        await using var a=Provider(connection,clock);
        var client=new ClientKey(38,Guid.NewGuid());
        await using(var c=new NpgsqlConnection(connection))
        {
            await c.OpenAsync();
            await using var seed=new NpgsqlCommand("INSERT INTO \"ClientConnectionEpochs\" (\"TenantId\",\"AgentId\",\"LastIssuedEpoch\") VALUES (@tenant,@agent,100)",c);
            seed.Parameters.AddWithValue("tenant",client.TenantId); seed.Parameters.AddWithValue("agent",client.AgentId); await seed.ExecuteNonQueryAsync();
        }
        (await Store(a).GetCurrentAsync(client,default)).Should().BeNull();
        var request=Request(client,clock); var reserved=(await Store(a).ReserveAsync(request,default)).Reservation!;
        reserved.Owner.Epoch.Should().Be(101);
        (await Store(a).GetCurrentAsync(client,default)).Should().BeNull();
        var committed=(await Store(a).CommitAsync(reserved,new(reserved.Owner,1,clock.GetUtcNow()),default)).Current!;
        await using var restarted=Provider(connection,clock);
        (await Store(restarted).GetCurrentAsync(client,default)).Should().BeEquivalentTo(committed);
        (await Store(restarted).ReserveAsync(request with { Metadata=request.Metadata with { AgentVersion="changed-body" } },default))
            .Disposition.Should().Be(OwnershipDisposition.AdmissionBodyConflict);
    }

    private async Task<string> PrepareAsync()
    {
        var connection=await fixture.CreateDatabaseAsync();
        await using var provider=Provider(connection,new AdvancingClock());
        await using var scope=provider.CreateAsyncScope();
        var db=scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Database.HasPendingModelChanges().Should().BeFalse("the refreshed #148 owner/admission migration must match the model");
        await db.Database.MigrateAsync();
        return connection;
    }
    private static ServiceProvider Provider(string connection,TimeProvider clock) => new ServiceCollection()
        .AddDbContext<OrchestratorDbContext>(o=>o.UseNpgsql(connection))
        .AddSingleton(clock)
        .AddSingleton(new OwnershipPolicy(TimeSpan.FromSeconds(30),TimeSpan.FromSeconds(60),TimeSpan.FromSeconds(10)))
        .AddSingleton<IClientConnectionEpochStore,ClientConnectionEpochStore>()
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes=true });
    private static IClientConnectionEpochStore Store(IServiceProvider p)=>p.GetRequiredService<IClientConnectionEpochStore>();
    private static AdmissionRequest Request(ClientKey client,TimeProvider clock)=>new(client,Guid.NewGuid(),Guid.NewGuid(),0,
        clock.GetUtcNow(),clock.GetUtcNow().AddSeconds(30),clock.GetUtcNow().AddMinutes(10),new("ownership-regression",["presence"],null));
    private static async Task<OwnerSnapshot> ActivateAsync(IClientConnectionEpochStore store,AdmissionRequest request)
    {
        var reserved=(await store.ReserveAsync(request,default)).Reservation!;
        var result=await store.CommitAsync(reserved,new(reserved.Owner,1,request.ReceivedAtUtc),default);
        result.Disposition.Should().Be(OwnershipDisposition.Accepted);
        return result.Current!;
    }
    private static async Task LockCounterAsync(NpgsqlConnection c,NpgsqlTransaction tx,ClientKey key)
    {
        await using var cmd=new NpgsqlCommand("SELECT * FROM \"ClientConnectionEpochs\" WHERE \"TenantId\"=@tenant AND \"AgentId\"=@agent FOR UPDATE",c,tx);
        cmd.Parameters.AddWithValue("tenant",key.TenantId); cmd.Parameters.AddWithValue("agent",key.AgentId);
        await using var reader=await cmd.ExecuteReaderAsync(); (await reader.ReadAsync()).Should().BeTrue();
    }
    private static async Task WaitBlockedAsync(string connection,string queryFragment,Task operation,CancellationToken ct)
    {
        await using var c=new NpgsqlConnection(connection); await c.OpenAsync(ct);
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        do
        {
            await using var cmd=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=@database AND cardinality(pg_blocking_pids(pid))>0 AND query LIKE @pattern)",c);
            cmd.Parameters.AddWithValue("database",c.Database); cmd.Parameters.AddWithValue("pattern","%"+queryFragment+"%");
            if((bool)(await cmd.ExecuteScalarAsync(ct))!) { operation.IsCompleted.Should().BeFalse(); return; }
            operation.IsCompleted.Should().BeFalse("the tested operation must wait on the real PostgreSQL row lock");
        } while(await timer.WaitForNextTickAsync(ct));
        throw new InvalidOperationException("No expected PostgreSQL lock wait observed.");
    }
    private sealed class AdvancingClock : TimeProvider
    {
        private long _ticks=DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow()=>new(Interlocked.Read(ref _ticks),TimeSpan.Zero);
        public void Advance(TimeSpan delta)=>Interlocked.Add(ref _ticks,delta.Ticks);
    }
}
