using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Sharding;
using Akka.Configuration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Akka.RemoteSupport;
using NetRatel.Application.RemoteSupport;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.RemoteSupport;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.Akka;

/// <summary>
/// Black-box replica exercise for the RS2-3B authority boundary.  The two
/// ActorSystems use real loopback remoting and cluster sharding, while their
/// independently constructed stores share only PostgreSQL.
/// </summary>
public sealed class RemoteSupportReplicaSafeClusterPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly List<ActorSystem> _systems = [];
    private string _connectionString = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
        await using var db = new OrchestratorDbContext(DbOptions());
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var system in _systems.AsEnumerable().Reverse())
        {
            await system.Terminate();
        }

        await _postgres.DisposeAsync();
    }

    [Fact(Timeout = 90_000)]
    public async Task BrowserAndAgentEdges_CrossReplicaToTheShardOwner_UsingOnlyPostgresForDurableState()
    {
        var clusterName = $"remote-support-v2-{Guid.NewGuid():N}";
        var nodeB = await StartNodeAsync(clusterName, port: FreePort(), seedPort: null);
        await WaitForMembersAsync(nodeB.System, 1);

        // Open before node A joins: this makes B the existing shard/entity
        // owner. A joins afterwards and is deliberately used for both local
        // browser and agent connection edges.
        var command = OpenCommand();
        var opened = await nodeB.Router.Ask<RemoteSupportSessionSnapshot>(
            new OpenRemoteSupportSession(command, TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(15));
        opened.State.Should().Be(RemoteSupportV2SessionStates.Requested);

        var nodeA = await StartNodeAsync(clusterName, port: FreePort(), seedPort: nodeB.Port);
        await WaitForMembersAsync(nodeA.System, 2);
        await WaitForMembersAsync(nodeB.System, 2);

        var browser = await nodeA.Router.Ask<RemoteSupportBrowserEdgeLocalRegistration?>(
            new SubscribeRemoteSupportBrowserEdge(opened.Session, command.InitiatingOperator, 0, TimeSpan.FromSeconds(10)),
            TimeSpan.FromSeconds(15));
        browser.Should().NotBeNull();
        var initialBrowserEvent = await browser!.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        initialBrowserEvent.Snapshot.State.Should().Be(RemoteSupportV2SessionStates.Requested);

        var agentFrames = Channel.CreateBounded<RemoteSupportAgentRouteEnvelope>(32);
        var agentEdge = nodeA.System.ActorOf(RemoteSupportAgentEdgeActor.Props(agentFrames), "agent-edge-a");
        var routeId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var registered = await nodeA.Router.Ask<RemoteSupportAgentEdgeRegistration>(
            new RegisterRemoteSupportAgentEdge(opened.Session, opened.Session.AgentId, connectionId, 1, routeId, 1, agentEdge),
            TimeSpan.FromSeconds(15));
        registered.Accepted.Should().BeTrue();

        var renegotiation = await agentFrames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        renegotiation.Kind.Should().Be("renegotiation_required");
        renegotiation.Session.Should().Be(opened.Session);

        var advanced = await nodeA.Router.Ask<RemoteSupportLifecycleTransitionResult>(
            new AdvanceRemoteSupportSessionByKey(new AdvanceRemoteSupportSessionLifecycle(
                RemoteSupportV2ContractVersions.Current,
                opened.Session,
                Guid.NewGuid(),
                RemoteSupportV2SessionStates.PreparingTarget,
                opened.LifecycleRevision,
                DateTimeOffset.UtcNow)), TimeSpan.FromSeconds(15));
        advanced.Disposition.Should().Be(RemoteSupportLifecycleTransitionDisposition.Applied);

        var browserEvent = await browser.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        browserEvent.EventType.Should().Be(RemoteSupportV2AuditEventTypes.LifecycleChanged);
        browserEvent.Snapshot.State.Should().Be(RemoteSupportV2SessionStates.PreparingTarget);

        var payload = new byte[] { 1, 2, 3 };
        nodeA.Router.Tell(new RouteRemoteSupportAgentEnvelope(new(
            opened.Session, routeId, 1, "offer", payload)));
        var routed = await agentFrames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        routed.Kind.Should().Be("offer");
        routed.Payload.Should().Equal(payload);

        var fromIndependentStore = await nodeA.Store.LoadAsync(opened.Session, CancellationToken.None);
        fromIndependentStore.Should().NotBeNull();
        fromIndependentStore!.State.Should().Be(RemoteSupportV2SessionStates.PreparingTarget);
        var audit = await nodeA.Store.ReadAuditAsync(opened.Session, 0, CancellationToken.None);
        audit.Select(item => item.AuditSequence).Should().Equal(1, 2);

        nodeA.Router.Tell(new UnsubscribeRemoteSupportBrowserEdge(opened.Session, browser.EdgeRouteId, browser.Edge));
        nodeA.System.Stop(agentEdge);
    }

    [Fact(Timeout = 90_000)]
    public async Task ShardOwnerLoss_RehydratesOnlyDurableLifecycleState_AndForcesRenegotiation()
    {
        var clusterName = $"remote-support-v2-recovery-{Guid.NewGuid():N}";
        var nodeB = await StartNodeAsync(clusterName, port: FreePort(), seedPort: null);
        await WaitForMembersAsync(nodeB.System, 1);
        var command = OpenCommand();
        var opened = await nodeB.Router.Ask<RemoteSupportSessionSnapshot>(
            new OpenRemoteSupportSession(command, TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(15));
        var nodeA = await StartNodeAsync(clusterName, port: FreePort(), seedPort: nodeB.Port);
        await WaitForMembersAsync(nodeA.System, 2);
        await WaitForMembersAsync(nodeB.System, 2);

        var preparing = await nodeA.Router.Ask<RemoteSupportLifecycleTransitionResult>(
            new AdvanceRemoteSupportSessionByKey(new(
                RemoteSupportV2ContractVersions.Current,
                opened.Session,
                Guid.NewGuid(),
                RemoteSupportV2SessionStates.PreparingTarget,
                opened.LifecycleRevision,
                DateTimeOffset.UtcNow)), TimeSpan.FromSeconds(15));
        preparing.Disposition.Should().Be(RemoteSupportLifecycleTransitionDisposition.Applied);

        await nodeB.System.Terminate();
        await WaitForMembersAsync(nodeA.System, 1);

        var recovered = await nodeA.Router.Ask<RemoteSupportSessionSnapshot?>(
            new GetRemoteSupportSessionByKey(opened.Session, command.InitiatingOperator), TimeSpan.FromSeconds(30));
        recovered.Should().NotBeNull();
        recovered!.State.Should().Be(RemoteSupportV2SessionStates.RenegotiationRequired);
        recovered.LifecycleRevision.Should().Be(3);

        var audit = await nodeA.Store.ReadAuditAsync(opened.Session, 0, CancellationToken.None);
        audit.Should().HaveCount(3);
        audit[^1].FailureCode.Should().Be("renegotiation_required_after_recovery");
    }

    private async Task<TestNode> StartNodeAsync(string clusterName, int port, int? seedPort)
    {
        var seed = seedPort is null
            ? $"akka.tcp://{clusterName}@127.0.0.1:{port}"
            : $"akka.tcp://{clusterName}@127.0.0.1:{seedPort.Value}";
        var config = ConfigurationFactory.ParseString($$"""
            akka {
              actor.provider = "Akka.Cluster.ClusterActorRefProvider, Akka.Cluster"
              remote.dot-netty.tcp.hostname = "127.0.0.1"
              remote.dot-netty.tcp.port = {{port}}
              cluster.seed-nodes = ["{{seed}}"]
              cluster.downing-provider-class = "Akka.Cluster.SBR.SplitBrainResolverProvider, Akka.Cluster"
              cluster.auto-down-unreachable-after = off
              cluster.sharding.state-store-mode = ddata
              loglevel = WARNING
            }
            """);
        var system = ActorSystem.Create(clusterName, config);
        _systems.Add(system);
        var store = new RemoteSupportLifecycleStore(CreateProvider().GetRequiredService<IServiceScopeFactory>());
        var region = ClusterSharding.Get(system).Start(
            RemoteSupportSessionMessageExtractor.RegionName,
            RemoteSupportSessionActor.Props(store),
            ClusterShardingSettings.Create(system),
            new RemoteSupportSessionMessageExtractor());
        var router = system.ActorOf(RemoteSupportSessionRouterActor.Props(store, region), "remote-support-v2-lifecycle");
        await Task.Yield();
        return new TestNode(system, router, store, port);
    }

    private ServiceProvider CreateProvider() => new ServiceCollection()
        .AddDbContext<OrchestratorDbContext>(builder => builder.UseNpgsql(_connectionString))
        .BuildServiceProvider();

    private DbContextOptions<OrchestratorDbContext> DbOptions() => new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseNpgsql(_connectionString)
        .Options;

    private static async Task WaitForMembersAsync(ActorSystem system, int expected)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var probe = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            do
            {
                if (Cluster.Get(system).State.Members.Count == expected)
                {
                    return;
                }
            }
            while (await probe.WaitForNextTickAsync(cancellation.Token));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"Cluster did not reach {expected} members.");
        }

        throw new TimeoutException($"Cluster did not reach {expected} members.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static RemoteSupportOpenSessionCommand OpenCommand() => new(
        RemoteSupportV2ContractVersions.Current,
        71,
        Guid.NewGuid(),
        Guid.NewGuid(),
        new RemoteSupportOperatorBinding("operator-a"),
        new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.Console),
        ["view"],
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(10));

    private sealed record TestNode(ActorSystem System, IActorRef Router, IRemoteSupportLifecycleStore Store, int Port);
}
