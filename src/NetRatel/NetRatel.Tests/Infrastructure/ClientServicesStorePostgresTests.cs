using Akka.Actor;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Akka.Services;
using NetRatel.Akka.Presence;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Tests.Akka;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class ClientServicesStorePostgresTests(PostgreSqlPersistenceFixture fixture)
{
    [Fact]
    public async Task Migration_actor_and_process_restart_preserve_complete_inventory_partial_attempt_and_fences()
    {
        var connection = await fixture.CreateDatabaseAsync();
        await using var provider = CreateProvider(connection);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Database.HasPendingModelChanges().Should().BeFalse("the services cache migration snapshot must match the provider model");
            await db.Database.MigrateAsync();
        }
        var client = new ClientKey(20, Guid.NewGuid());
        var store = provider.GetRequiredService<IClientServicesStore>();
        var system = ActorSystem.Create($"services-postgres-{Guid.NewGuid():N}");
        try
        {
            var actor = system.ActorOf(ClientServicesActor.Props(client, store));
            var complete = ServicesTestData.Chunk(client, 100, ServicesTestData.Service("durable"));
            var presence = system.ActorOf(PresenceActor.Props(client, new NetRatelAkkaOptions(), ActorRefs.Nobody,
                provider.GetRequiredService<IClientConnectionEpochStore>()));
            var start = StartSession(client, complete.ConnectionId);
            var admission = await presence.Ask<GatewayPresenceSessionStarted>(start);
            admission.ConnectionEpoch.Should().Be(1);
            (await presence.Ask<GatewayPresenceSessionStarted>(start)).Disposition.Should().Be(PresenceMessageDisposition.Duplicate);
            (await actor.Ask<ClientServicesMessageResult>(new RecordClientServicesChunk(complete)))
                .Disposition.Should().Be(ClientServicesMessageDisposition.Accepted);
            await actor.Ask<ClientServicesState>(new UpdateClientServiceWatchPolicy(ServicesTestData.Policy(client, 2, "durable")));
            var partial = ServicesTestData.Chunk(client, 101, ServicesTestData.Service("unfinished")) with { IsFinal = false };
            await actor.Ask<ClientServicesMessageResult>(new RecordClientServicesChunk(partial));
            await actor.GracefulStop(TimeSpan.FromSeconds(3));
            await presence.GracefulStop(TimeSpan.FromSeconds(3));

            // A new service provider/store models process restart, with no in-memory projection retained.
            await using var restartedProvider = CreateProvider(connection);
            var restartedStore = restartedProvider.GetRequiredService<IClientServicesStore>();
            actor = system.ActorOf(ClientServicesActor.Props(client, restartedStore));
            var state = await actor.Ask<ClientServicesState>(new GetClientServices(client));
            state.LastCompleteInventory!.Services.Single().Name.Should().Be("durable");
            state.LatestAttempt!.Status.Should().Be(ServiceCollectionStatus.Partial);
            state.LastAcceptedSequence.Should().Be(101);
            state.WatchPolicyRevision.Should().Be(2);
            state.MonitoredServiceNames.Should().Equal("durable");
            (await actor.Ask<ClientServicesMessageResult>(new RecordClientServicesChunk(complete)))
                .Disposition.Should().Be(ClientServicesMessageDisposition.StaleSequence);
            (await actor.Ask<ClientServicesMessageResult>(new RecordClientServicesChunk(complete with
                { Sequence = 200, ConnectionId = Guid.NewGuid() }))).Disposition.Should().Be(ClientServicesMessageDisposition.ConnectionMismatch);
            await actor.GracefulStop(TimeSpan.FromSeconds(3));
            var twiceRecovered = await restartedStore.LoadAsync(client, CancellationToken.None);
            twiceRecovered!.LatestAttempt!.Status.Should().Be(ServiceCollectionStatus.Partial);
            twiceRecovered.LastCompleteInventory!.Services.Single().Name.Should().Be("durable");

            presence = system.ActorOf(PresenceActor.Props(client, new NetRatelAkkaOptions(), ActorRefs.Nobody,
                restartedProvider.GetRequiredService<IClientConnectionEpochStore>()));
            var newConnection = Guid.NewGuid();
            var newAdmission = await presence.Ask<GatewayPresenceSessionStarted>(StartSession(client, newConnection));
            newAdmission.ConnectionEpoch.Should().BeGreaterThan(admission.ConnectionEpoch);
            actor = system.ActorOf(ClientServicesActor.Props(client, restartedStore));
            (await actor.Ask<ClientServicesMessageResult>(new RecordClientServicesChunk(ServicesTestData.Chunk(client, 1,
                ServicesTestData.Service("reconnected")) with { ConnectionEpoch = newAdmission.ConnectionEpoch, ConnectionId = newConnection })))
                .Disposition.Should().Be(ClientServicesMessageDisposition.Accepted);
            (await actor.Ask<ClientServicesMessageResult>(new RecordClientServicesChunk(partial with { Sequence = 1000 })))
                .Disposition.Should().Be(ClientServicesMessageDisposition.StaleEpoch);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Epoch_allocation_is_atomic_across_processes_and_advances_existing_services_fence()
    {
        var connection = await fixture.CreateDatabaseAsync();
        await using var firstProvider = CreateProvider(connection);
        await using var secondProvider = CreateProvider(connection);
        await using (var scope = firstProvider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.MigrateAsync();
        var client = new ClientKey(23, Guid.NewGuid());
        await firstProvider.GetRequiredService<IClientServicesStore>().SaveAsync(ClientServicesState.Empty(client) with
            { Revision = 1, ConnectionEpoch = 100, LastAcceptedSequence = 10, ConnectionId = Guid.NewGuid() }, 0, CancellationToken.None);
        var first = firstProvider.GetRequiredService<IClientConnectionEpochStore>();
        var second = secondProvider.GetRequiredService<IClientConnectionEpochStore>();
        var allocations = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(index => (index % 2 == 0 ? first : second).AllocateAsync(client, 0, CancellationToken.None)));
        allocations.Order().Should().Equal(Enumerable.Range(101, 16).Select(value => (long)value));
        await using var restartedProvider = CreateProvider(connection);
        var restarted = restartedProvider.GetRequiredService<IClientConnectionEpochStore>();
        (await restarted.AllocateAsync(client, 130, CancellationToken.None)).Should().Be(131);
        (await restarted.AllocateAsync(new ClientKey(24, client.AgentId), 0, CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task Newly_allocated_admission_fences_old_ingress_before_first_new_snapshot_while_allowing_policy_updates()
    {
        var connection = await fixture.CreateDatabaseAsync();
        await using var provider = CreateProvider(connection);
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.MigrateAsync();
        var client = new ClientKey(25, Guid.NewGuid());
        var epochs = provider.GetRequiredService<IClientConnectionEpochStore>();
        var store = provider.GetRequiredService<IClientServicesStore>();
        (await epochs.AllocateAsync(client, 0, CancellationToken.None)).Should().Be(1);
        var current = ClientServicesState.Empty(client) with
            { Revision = 1, ConnectionEpoch = 1, ConnectionId = Guid.NewGuid(), LastAcceptedSequence = 1 };
        (await store.SaveAsync(current, 0, CancellationToken.None)).Disposition.Should().Be(ClientServicesStoreWriteDisposition.Stored);
        (await epochs.AllocateAsync(client, 0, CancellationToken.None)).Should().Be(2);

        // Another API's locally active epoch 1 is no longer sufficient, even though no epoch 2
        // services snapshot has arrived to advance the cached projection yet.
        (await store.SaveAsync(current with { Revision = 2, LastAcceptedSequence = 2 }, 1, CancellationToken.None))
            .Disposition.Should().Be(ClientServicesStoreWriteDisposition.Conflict);
        var policy = current with { Revision = 2, WatchPolicyRevision = 1, MonitoredServiceNames = ["selected"] };
        (await store.SaveAsync(policy, 1, CancellationToken.None)).Disposition.Should().Be(ClientServicesStoreWriteDisposition.Stored);
        (await store.SaveAsync(policy with { Revision = 3, ConnectionEpoch = 2, ConnectionId = Guid.NewGuid(), LastAcceptedSequence = 1 },
            2, CancellationToken.None)).Disposition.Should().Be(ClientServicesStoreWriteDisposition.Stored);
    }

    [Fact]
    public async Task Admission_allocation_waits_for_inflight_ingress_transaction_share_lock()
    {
        var connection = await fixture.CreateDatabaseAsync();
        await using var provider = CreateProvider(connection);
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.MigrateAsync();
        var client = new ClientKey(26, Guid.NewGuid());
        var epochs = provider.GetRequiredService<IClientConnectionEpochStore>();
        (await epochs.AllocateAsync(client, 0, CancellationToken.None)).Should().Be(1);
        var pause = new PauseServicesSave();
        await using var writer = CreateProvider(connection, pause);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var candidate = ClientServicesState.Empty(client) with
            { Revision = 1, ConnectionEpoch = 1, ConnectionId = Guid.NewGuid(), LastAcceptedSequence = 1 };
        var save = writer.GetRequiredService<IClientServicesStore>().SaveAsync(candidate, 0, timeout.Token);
        Task<long>? allocation = null;
        try
        {
            await pause.Entered.Task.WaitAsync(timeout.Token);
            allocation = epochs.AllocateAsync(client, 0, timeout.Token);
            await WaitForAllocatorLockAsync(connection, allocation, timeout.Token);
        }
        finally { pause.Resume.TrySetResult(); }
        (await save).Disposition.Should().Be(ClientServicesStoreWriteDisposition.Stored);
        (await allocation!).Should().Be(2);
        (await provider.GetRequiredService<IClientServicesStore>().LoadAsync(client, timeout.Token))!
            .ConnectionEpoch.Should().Be(1, "the accepted ingress committed before the next admission epoch was issued");
    }

    [Fact]
    public async Task Concurrent_inserts_and_updates_have_one_winner_and_reject_stale_epoch_sequence_and_policy()
    {
        var connection = await fixture.CreateDatabaseAsync();
        await using var provider = CreateProvider(connection);
        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.MigrateAsync();
        var store = provider.GetRequiredService<IClientServicesStore>();
        var client = new ClientKey(21, Guid.NewGuid());
        var initial = ClientServicesState.Empty(client) with
            { Revision = 1, ConnectionEpoch = 1, ConnectionId = Guid.NewGuid(), LastAcceptedSequence = ulong.MaxValue };
        var inserts = await Task.WhenAll(store.SaveAsync(initial, 0, CancellationToken.None),
            store.SaveAsync(initial with { ConnectionEpoch = 2, LastAcceptedSequence = 1 }, 0, CancellationToken.None));
        inserts.Count(result => result.Disposition == ClientServicesStoreWriteDisposition.Stored).Should().Be(1);
        inserts.Count(result => result.Disposition == ClientServicesStoreWriteDisposition.Conflict).Should().Be(1);
        var current = (await store.LoadAsync(client, CancellationToken.None))!;

        // Both writers start from the same durable revision, as two API processes could.
        var winner = current with { Revision = 2, ConnectionEpoch = 3, LastAcceptedSequence = 20, ConnectionId = Guid.NewGuid() };
        var writes = await Task.WhenAll(store.SaveAsync(winner, 1, CancellationToken.None),
            store.SaveAsync(winner with { LastAcceptedSequence = 21 }, 1, CancellationToken.None));
        writes.Count(result => result.Disposition == ClientServicesStoreWriteDisposition.Stored).Should().Be(1);
        writes.Count(result => result.Disposition == ClientServicesStoreWriteDisposition.Conflict).Should().Be(1);
        current = (await store.LoadAsync(client, CancellationToken.None))!;
        (await store.SaveAsync(current with { Revision = 3, ConnectionEpoch = 2, LastAcceptedSequence = 100 }, 2, CancellationToken.None))
            .Disposition.Should().Be(ClientServicesStoreWriteDisposition.Conflict);
        (await store.SaveAsync(current with { Revision = 3, LastAcceptedSequence = 1 }, 2, CancellationToken.None))
            .Disposition.Should().Be(ClientServicesStoreWriteDisposition.Conflict);
        (await store.SaveAsync(current with { Revision = 3, ConnectionId = Guid.NewGuid() }, 2, CancellationToken.None))
            .Disposition.Should().Be(ClientServicesStoreWriteDisposition.Conflict);

        var policy = current with { Revision = 3, WatchPolicyRevision = 2, MonitoredServiceNames = ["selected"] };
        (await store.SaveAsync(policy, 2, CancellationToken.None)).Disposition.Should().Be(ClientServicesStoreWriteDisposition.Stored);
        (await store.SaveAsync(policy with { Revision = 4, WatchPolicyRevision = 1 }, 3, CancellationToken.None))
            .Disposition.Should().Be(ClientServicesStoreWriteDisposition.Conflict);
        (await store.SaveAsync(policy with { Revision = 4, MonitoredServiceNames = ["changed-at-same-policy-revision"] }, 3, CancellationToken.None))
            .Disposition.Should().Be(ClientServicesStoreWriteDisposition.Conflict);
        (await store.LoadAsync(new ClientKey(22, client.AgentId), CancellationToken.None)).Should().BeNull();
    }

    private static ServiceProvider CreateProvider(string connection, SaveChangesInterceptor? interceptor = null) => new ServiceCollection()
        .AddDbContext<OrchestratorDbContext>(options =>
        {
            options.UseNpgsql(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
        })
        .AddNetRatelClientServicesPersistence().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static StartGatewayPresenceSession StartSession(ClientKey client, Guid connection) =>
        new(client, connection, Guid.NewGuid(), "v1", "tests", [ClientServicesLimits.Capability], null, DateTimeOffset.UtcNow);

    private static async Task WaitForAllocatorLockAsync(string connectionString, Task pendingAllocation, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        do
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity
                    WHERE datname = @database AND cardinality(pg_blocking_pids(pid)) > 0
                        AND query LIKE '%INSERT INTO "ClientConnectionEpochs"%')
                """, connection);
            command.Parameters.AddWithValue("database", connection.Database);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
            {
                pendingAllocation.IsCompleted.Should().BeFalse("the allocator must wait for the accepted ingress transaction's FOR SHARE lock");
                return;
            }
            pendingAllocation.IsCompleted.Should().BeFalse("an allocator without the transaction fence would already have completed");
        } while (await timer.WaitForNextTickAsync(cancellationToken));
        throw new InvalidOperationException("No allocator lock wait was observed.");
    }

    private sealed class PauseServicesSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
