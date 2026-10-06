using System.Text.Json;
using Akka.Actor;
using AwesomeAssertions;
using NetRatel.Akka.Services;
using NetRatel.Akka.Presence;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class ClientServicesActorTests : IAsyncLifetime
{
    private ActorSystem _system = null!;
    private readonly MemoryServicesStore _store = new();
    public ValueTask InitializeAsync() { _system = ActorSystem.Create($"services-tests-{Guid.NewGuid():N}"); return ValueTask.CompletedTask; }
    public async ValueTask DisposeAsync() => await _system.Terminate();

    [Fact]
    public async Task Restart_recovers_complete_cache_latest_partial_attempt_and_durable_fences()
    {
        var client = new ClientKey(7, Guid.NewGuid());
        var complete = ServicesTestData.Chunk(client, 1, ServicesTestData.Service("original"));
        var actor = _system.ActorOf(ClientServicesActor.Props(client, _store));
        (await Record(actor, complete)).Disposition.Should().Be(ClientServicesMessageDisposition.Accepted);
        var partial = ServicesTestData.Chunk(client, 2, ServicesTestData.Service("incomplete")) with { IsFinal = false };
        await Record(actor, partial);
        await actor.GracefulStop(TimeSpan.FromSeconds(3));
        actor = _system.ActorOf(ClientServicesActor.Props(client, _store));

        var recovered = await actor.Ask<ClientServicesState>(new GetClientServices(client));
        recovered.LastCompleteInventory!.Services.Single().Name.Should().Be("original");
        recovered.LatestAttempt!.Status.Should().Be(ServiceCollectionStatus.Partial);
        recovered.LastAcceptedSequence.Should().Be(2);
        (await Record(actor, partial)).Disposition.Should().Be(ClientServicesMessageDisposition.Duplicate);
        (await Record(actor, partial with { Sequence = 3, ChunkIndex = 1, IsFinal = true })).Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        (await Record(actor, complete with { Sequence = 1 })).Disposition.Should().Be(ClientServicesMessageDisposition.StaleSequence);
        (await Record(actor, complete with { Sequence = 3, ConnectionId = Guid.NewGuid() })).Disposition.Should().Be(ClientServicesMessageDisposition.ConnectionMismatch);

        var newSession = ServicesTestData.Chunk(client, 1, ServicesTestData.Service("new-session")) with
            { ConnectionEpoch = 2, ConnectionId = Guid.NewGuid() };
        (await Record(actor, newSession)).Disposition.Should().Be(ClientServicesMessageDisposition.Accepted);
        (await Record(actor, complete with { Sequence = 100 })).Disposition.Should().Be(ClientServicesMessageDisposition.StaleEpoch);
        (await actor.Ask<ClientServicesState>(new GetClientServices(client))).LastCompleteInventory!.Services.Single().Name.Should().Be("new-session");
    }

    [Fact]
    public async Task Only_ordered_final_complete_inventory_replaces_cache()
    {
        var client = new ClientKey(8, Guid.NewGuid());
        var actor = _system.ActorOf(ClientServicesActor.Props(client, _store));
        await Record(actor, ServicesTestData.Chunk(client, 1, ServicesTestData.Service("original")));
        var first = ServicesTestData.Chunk(client, 2, ServicesTestData.Service("first")) with { IsFinal = false };
        await Record(actor, first);
        (await Record(actor, first with { Sequence = 3, ChunkIndex = 2, IsFinal = true, Services = [ServicesTestData.Service("skipped")] }))
            .Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        var final = first with { Sequence = 3, ChunkIndex = 1, IsFinal = true, Services = [ServicesTestData.Service("second")] };
        (await Record(actor, final)).Disposition.Should().Be(ClientServicesMessageDisposition.Accepted);
        var state = await actor.Ask<ClientServicesState>(new GetClientServices(client));
        state.LastCompleteInventory!.Services.Select(service => service.Name).Should().Equal("first", "second");

        await Record(actor, ServicesTestData.Chunk(client, 4, ServicesTestData.Service("partial")) with { Status = ServiceCollectionStatus.Partial });
        await Record(actor, ServicesTestData.Chunk(client, 5) with { Status = ServiceCollectionStatus.Error, ErrorCode = "permission_denied" });
        state = await actor.Ask<ClientServicesState>(new GetClientServices(client));
        state.LastCompleteInventory!.Services.Select(service => service.Name).Should().Equal("first", "second");
        state.LatestAttempt!.Status.Should().Be(ServiceCollectionStatus.Error);
        state.LatestAttempt.ErrorCode.Should().Be("permission_denied");
    }

    [Fact]
    public async Task Cross_chunk_windows_case_duplicates_and_changed_metadata_are_rejected()
    {
        var client = new ClientKey(9, Guid.NewGuid());
        var actor = _system.ActorOf(ClientServicesActor.Props(client, _store));
        var first = ServicesTestData.Chunk(client, 1, ServicesTestData.Service("Example")) with { IsFinal = false };
        await Record(actor, first);
        (await Record(actor, first with { Sequence = 2, ChunkIndex = 1, IsFinal = true, Services = [ServicesTestData.Service("example")] }))
            .Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        (await Record(actor, first with { Sequence = 2, ChunkIndex = 1, IsFinal = true, CollectionId = Guid.NewGuid(), Services = [] }))
            .Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        (await Record(actor, first with { Sequence = 2, ChunkIndex = 1, IsFinal = true, ObservedAtUtc = first.ObservedAtUtc.AddSeconds(1), Services = [] }))
            .Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        (await Record(actor, first with { Sequence = 2, ChunkIndex = 1, IsFinal = true, Status = ServiceCollectionStatus.Partial, Services = [] }))
            .Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        (await actor.Ask<ClientServicesState>(new GetClientServices(client))).LastCompleteInventory.Should().BeNull();
    }

    [Fact]
    public async Task Selected_watch_requires_current_policy_successful_missing_evidence_and_complete_coverage()
    {
        var client = new ClientKey(10, Guid.NewGuid());
        var actor = _system.ActorOf(ClientServicesActor.Props(client, _store));
        await Record(actor, ServicesTestData.Chunk(client, 1, ServicesTestData.Service("inventory")));
        await actor.Ask<ClientServicesState>(new UpdateClientServiceWatchPolicy(ServicesTestData.Policy(client, 1, "Expected")));
        var watch = ServicesTestData.Chunk(client, 2, ServicesTestData.Service("expected", ClientServiceState.Missing, true)) with
            { Kind = ServiceSnapshotKind.Watch, WatchPolicyRevision = 1 };
        (await Record(actor, watch with { Services = [ServicesTestData.Service("expected", ClientServiceState.Missing)] }))
            .Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        (await Record(actor, watch with { Status = ServiceCollectionStatus.Partial })).Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        (await Record(actor, watch with { Services = [] })).Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        (await Record(actor, watch)).Disposition.Should().Be(ClientServicesMessageDisposition.Accepted);
        var missing = await actor.Ask<ClientServicesState>(new GetClientServices(client));
        missing.WatchedServices.Single().State.Should().Be(ClientServiceState.Missing);
        missing.LastCompleteInventory!.Services.Single().Name.Should().Be("inventory");

        var error = watch with { Sequence = 3, CollectionId = Guid.NewGuid(), Status = ServiceCollectionStatus.Error, Services = [], ErrorCode = "access_denied" };
        (await Record(actor, error)).Disposition.Should().Be(ClientServicesMessageDisposition.Accepted);
        var failed = await actor.Ask<ClientServicesState>(new GetClientServices(client));
        failed.WatchedServices.Single().State.Should().Be(ClientServiceState.Unknown);
        failed.WatchedServices.Single().AuthoritativeMissing.Should().BeFalse();
        failed.LastCompleteInventory!.Services.Single().Name.Should().Be("inventory");

        await actor.Ask<ClientServicesState>(new UpdateClientServiceWatchPolicy(ServicesTestData.Policy(client, 2, "NeverObserved")));
        (await Record(actor, error with { Sequence = 4 })).Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        await Record(actor, error with { Sequence = 4, WatchPolicyRevision = 2 });
        (await actor.Ask<ClientServicesState>(new GetClientServices(client))).WatchedServices.Single()
            .Should().Match<ClientServiceObservation>(service => service.Name == "NeverObserved" && service.State == ClientServiceState.Unknown);
    }

    [Fact]
    public async Task Chunk_assembly_byte_count_chunk_count_and_deadline_are_bounded()
    {
        var client = new ClientKey(11, Guid.NewGuid());
        var clock = new ServicesTestClock();
        var actor = _system.ActorOf(ClientServicesActor.Props(client, _store, clock));
        var first = ServicesTestData.Chunk(client, 1) with { IsFinal = false, PayloadBytes = ClientServicesLimits.MaximumChunkPayloadBytes };
        await Record(actor, first);
        for (uint index = 1; index < 85; index++)
            (await Record(actor, first with { Sequence = index + 1, ChunkIndex = index })).Disposition.Should().Be(ClientServicesMessageDisposition.Accepted);
        (await Record(actor, first with { Sequence = 86, ChunkIndex = 85 })).Disposition.Should().Be(ClientServicesMessageDisposition.CapacityExceeded);
        (await Record(actor, first with { Sequence = 86, ChunkIndex = ClientServicesLimits.MaximumChunks })).Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        (await Record(actor, first with { Sequence = 86, PayloadBytes = ClientServicesLimits.MaximumChunkPayloadBytes + 1 })).Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        clock.Advance(ClientServicesLimits.AssemblyTimeout);
        (await Record(actor, first with { Sequence = 86, ChunkIndex = 85, IsFinal = true, PayloadBytes = 1 })).Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        var state = await actor.Ask<ClientServicesState>(new GetClientServices(client));
        state.LatestAttempt!.Status.Should().Be(ServiceCollectionStatus.Error);
        state.LatestAttempt.ErrorCode.Should().Be("assembly_timeout");
        state.LastCompleteInventory.Should().BeNull();
    }

    [Fact]
    public async Task Wrong_tenant_record_is_rejected_and_router_capacity_is_bounded()
    {
        var client = new ClientKey(12, Guid.NewGuid());
        var otherTenant = new ClientKey(13, client.AgentId);
        var actor = _system.ActorOf(ClientServicesActor.Props(client, _store));
        (await Record(actor, ServicesTestData.Chunk(otherTenant, 1))).Disposition.Should().Be(ClientServicesMessageDisposition.Invalid);
        var router = _system.ActorOf(ClientServicesRouterActor.Props(_store, maximumActiveClients: 1));
        await Record(router, ServicesTestData.Chunk(client, 1, ServicesTestData.Service("tenant-one")));
        (await Record(router, ServicesTestData.Chunk(otherTenant, 1))).Disposition.Should().Be(ClientServicesMessageDisposition.CapacityExceeded);
        var diagnostics = await router.Ask<ClientServicesRouteDiagnostics>(new GetClientServicesRouteDiagnostics());
        diagnostics.ActiveClientActors.Should().Be(1);
        diagnostics.BufferedRequests.Should().Be(0);
    }

    [Fact]
    public async Task Idle_region_passivates_and_lazily_recovers_cached_inventory()
    {
        var client = new ClientKey(14, Guid.NewGuid());
        var router = _system.ActorOf(ClientServicesRouterActor.Props(_store, idleTimeout: TimeSpan.FromMilliseconds(60)));
        await Record(router, ServicesTestData.Chunk(client, 1, ServicesTestData.Service("cached")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        while ((await router.Ask<ClientServicesRouteDiagnostics>(new GetClientServicesRouteDiagnostics())).ActiveClientActors != 0)
            await timer.WaitForNextTickAsync(deadline.Token);
        (await router.Ask<ClientServicesState>(new GetClientServices(client))).LastCompleteInventory!.Services.Single().Name.Should().Be("cached");
    }

    [Fact]
    public async Task Ambiguous_database_completion_reloads_durable_fence_before_retry()
    {
        var client = new ClientKey(15, Guid.NewGuid());
        var store = new CommitThenFailStore();
        var actor = _system.ActorOf(ClientServicesActor.Props(client, store));
        var chunk = ServicesTestData.Chunk(client, 1, ServicesTestData.Service("committed"));
        (await Record(actor, chunk)).Disposition.Should().Be(ClientServicesMessageDisposition.PersistenceUnavailable);
        (await Record(actor, chunk with { Services = [ServicesTestData.Service("retry-must-not-overwrite")] }))
            .Disposition.Should().Be(ClientServicesMessageDisposition.Duplicate);
        (await actor.Ask<ClientServicesState>(new GetClientServices(client))).LastCompleteInventory!.Services.Single().Name.Should().Be("committed");
    }

    [Fact]
    public async Task Failed_epoch_allocation_does_not_admit_or_publish_online_presence()
    {
        var client = new ClientKey(16, Guid.NewGuid());
        var readModel = _system.ActorOf(PresenceReadModelActor.Props());
        var actor = _system.ActorOf(PresenceActor.Props(client, new NetRatelAkkaOptions(), readModel, new FailingEpochStore()));
        var receivedAt = DateTimeOffset.UtcNow;
        var start = () => actor.Ask<GatewayPresenceSessionStarted>(new StartGatewayPresenceSession(client, Guid.NewGuid(),
            Guid.NewGuid(), "v1", "test", [], null, receivedAt,
            AuthenticationExpiresAtUtc: receivedAt.AddMinutes(10),
            AdmissionExpiresAtUtc: receivedAt.AddSeconds(10), ProvisionalAdmission: true));
        await start.Should().ThrowAsync<InvalidOperationException>();
        var state = await actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(client));
        state.Status.Should().Be(ClientPresenceStatus.Unknown);
        state.ConnectionId.Should().BeNull();
        state.ConnectionEpoch.Should().BeNull();
        (await readModel.Ask<ClientPresenceReadModelSnapshot>(new GetClientPresenceReadModel())).Items.Should().BeEmpty();
    }

    private static Task<ClientServicesMessageResult> Record(IActorRef actor, ClientServicesChunk chunk) =>
        actor.Ask<ClientServicesMessageResult>(new RecordClientServicesChunk(chunk), TimeSpan.FromSeconds(5));

    private sealed class CommitThenFailStore : IClientServicesStore
    {
        private readonly MemoryServicesStore _inner = new();
        private bool _fail = true;
        public Task<ClientServicesState?> LoadAsync(ClientKey client, CancellationToken cancellationToken) => _inner.LoadAsync(client, cancellationToken);
        public async Task<ClientServicesStoreWriteResult> SaveAsync(ClientServicesState candidate, long expectedRevision, CancellationToken cancellationToken)
        {
            var result = await _inner.SaveAsync(candidate, expectedRevision, cancellationToken);
            if (_fail) { _fail = false; throw new IOException("response lost after durable commit"); }
            return result;
        }
    }

    private sealed class FailingEpochStore : ConnectionOwnershipTestStore
    {
        public override Task<ReserveResult> ReserveAsync(AdmissionRequest request, CancellationToken cancellationToken) =>
            throw new IOException("database unavailable");
    }
}

internal static class ServicesTestData
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ConnectionId = new("1534b927-87f4-4e8f-bfdd-f3020c5aeccc");
    public static ClientServicesChunk Chunk(ClientKey client, ulong sequence, params ClientServiceObservation[] services) =>
        new(client, ConnectionId, 1, sequence, Guid.NewGuid(), 0, true, ServiceSnapshotKind.Inventory,
            ServiceCollectionStatus.Complete, ObservedAt, ObservedAt.AddSeconds(1), 0, services, 200);
    public static ClientServiceObservation Service(string name, ClientServiceState state = ClientServiceState.Running, bool missing = false) =>
        new(name, name + " display", ClientServicePlatform.Windows, state, state.ToString(), "Auto", null, null, null, null, ObservedAt, missing);
    public static ClientServiceWatchPolicy Policy(ClientKey client, ulong revision, params string[] names) =>
        new(client, new(revision, names, 30, 900, DateTimeOffset.UtcNow.AddMinutes(30)));
}

internal sealed class MemoryServicesStore : IClientServicesStore
{
    private readonly Dictionary<ClientKey, ClientServicesState> _states = [];
    public Task<ClientServicesState?> LoadAsync(ClientKey client, CancellationToken cancellationToken)
    {
        lock (_states) return Task.FromResult(_states.TryGetValue(client, out var state) ? Copy(state) : null);
    }
    public Task<ClientServicesStoreWriteResult> SaveAsync(ClientServicesState candidate, long expectedRevision, CancellationToken cancellationToken)
    {
        lock (_states)
        {
            var current = _states.GetValueOrDefault(candidate.Client) ?? ClientServicesState.Empty(candidate.Client);
            if (current.Revision != expectedRevision) return Task.FromResult(new ClientServicesStoreWriteResult(ClientServicesStoreWriteDisposition.Conflict, Copy(current)));
            _states[candidate.Client] = Copy(candidate);
            return Task.FromResult(new ClientServicesStoreWriteResult(ClientServicesStoreWriteDisposition.Stored, Copy(candidate)));
        }
    }
    private static ClientServicesState Copy(ClientServicesState state) => JsonSerializer.Deserialize<ClientServicesState>(JsonSerializer.Serialize(state))!;
}

internal sealed class ServicesTestClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
}

internal sealed class MemoryConnectionEpochStore : ConnectionOwnershipTestStore
{
    private readonly Dictionary<ClientKey, long> _epochs = [];
    public override Task<long> AllocateAsync(ClientKey client, long minimumEpoch, CancellationToken cancellationToken)
    {
        lock (_epochs)
        {
            var epoch = checked(Math.Max(_epochs.GetValueOrDefault(client), minimumEpoch) + 1);
            _epochs[client] = epoch;
            return Task.FromResult(epoch);
        }
    }
}
