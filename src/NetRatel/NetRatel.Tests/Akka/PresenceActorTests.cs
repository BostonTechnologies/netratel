using Akka.Actor;
using FluentAssertions;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Presence;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class PresenceActorTests : IAsyncLifetime
{
    private ActorSystem _system = null!;

    public ValueTask InitializeAsync()
    {
        _system = ActorSystem.Create($"presence-tests-{Guid.NewGuid():N}");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _system.Terminate();
    }

    [Fact]
    public async Task ClientActor_RecordsOnlyShadowPresence()
    {
        var client = new ClientKey(17, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var actor = _system.ActorOf(ClientActor.Props(client, CreateOptions()));
        var now = DateTimeOffset.UtcNow;

        var started = await actor.Ask<GatewayPresenceSessionStarted>(
            new StartGatewayPresenceSession(
                client,
                connectionId,
                Guid.NewGuid(),
                "1.0",
                "1.2.3",
                ["presence", "shell:pwsh"],
                new string('a', 64),
                now));
        var heartbeat = await actor.Ask<PresenceMessageResult>(
            new RecordGatewayHeartbeat(
                client,
                connectionId,
                started.ConnectionEpoch,
                Guid.NewGuid(),
                1,
                now.AddSeconds(1)));
        var snapshot = await actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(client));

        started.ConnectionEpoch.Should().Be(1);
        heartbeat.Disposition.Should().Be(PresenceMessageDisposition.Accepted);
        snapshot.Status.Should().Be(ShadowPresenceStatus.Online);
        snapshot.LastAcceptedSequence.Should().Be(1);
        snapshot.Source.Should().Be("unavailable");
        snapshot.IsAuthoritative.Should().BeFalse();
        snapshot.AgentVersion.Should().Be("1.2.3");
        snapshot.Capabilities.Should().Equal("presence", "shell:pwsh");
        snapshot.LegacySpacetimeIdentity.Should().Be(new string('a', 64));
    }

    [Fact]
    public async Task ClientActor_ClaimsAuthorityWhenPresenceAuthorityIsEnabled()
    {
        var client = new ClientKey(18, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var options = CreateOptions();
        options.PresenceAuthorityEnabled = true;
        var actor = _system.ActorOf(ClientActor.Props(client, options));

        await actor.Ask<GatewayPresenceSessionStarted>(new StartGatewayPresenceSession(
            client,
            connectionId,
            Guid.NewGuid(),
            "1.0",
            "cutover-test",
            ["presence"],
            null,
            DateTimeOffset.UtcNow));
        var snapshot = await actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(client));

        snapshot.Source.Should().Be("akka");
        snapshot.IsAuthoritative.Should().BeTrue();
    }

    [Fact]
    public void MessageExtractor_UsesTheStableTenantAndAgentKey()
    {
        var client = new ClientKey(17, Guid.Parse("8ba2dd35-2e0f-4772-836d-b9463f1eac2d"));
        var message = new GetClientPresence(client);

        var entityId = new ClientPresenceMessageExtractor().EntityId(message);

        entityId.Should().Be("17:8ba2dd352e0f4772836db9463f1eac2d");
    }

    [Fact]
    public async Task NewSession_FencesThePreviousConnectionEpoch()
    {
        var client = new ClientKey(23, Guid.NewGuid());
        var actor = _system.ActorOf(ClientActor.Props(client, CreateOptions()));
        var firstConnection = Guid.NewGuid();
        var secondConnection = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var first = await actor.Ask<GatewayPresenceSessionStarted>(new StartGatewayPresenceSession(
            client,
            firstConnection,
            Guid.NewGuid(),
            "1.0",
            "1.2.3",
            ["presence"],
            null,
            now));
        var second = await actor.Ask<GatewayPresenceSessionStarted>(new StartGatewayPresenceSession(
            client,
            secondConnection,
            Guid.NewGuid(),
            "1.0",
            "1.2.3",
            ["presence"],
            null,
            now.AddSeconds(1)));
        var staleHeartbeat = await actor.Ask<PresenceMessageResult>(new RecordGatewayHeartbeat(
            client,
            firstConnection,
            first.ConnectionEpoch,
            Guid.NewGuid(),
            1,
            now.AddSeconds(2)));

        second.ConnectionEpoch.Should().Be(first.ConnectionEpoch + 1);
        staleHeartbeat.Disposition.Should().Be(PresenceMessageDisposition.StaleConnectionEpoch);
    }

    [Fact]
    public async Task HeartbeatSequence_IsIdempotentAndMonotonic()
    {
        var client = new ClientKey(42, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var actor = _system.ActorOf(ClientActor.Props(client, CreateOptions()));
        var now = DateTimeOffset.UtcNow;
        var session = await actor.Ask<GatewayPresenceSessionStarted>(new StartGatewayPresenceSession(
            client,
            connectionId,
            Guid.NewGuid(),
            "1.0",
            "1.2.3",
            ["presence"],
            null,
            now));

        Task<PresenceMessageResult> SendAsync(ulong sequence) => actor.Ask<PresenceMessageResult>(
            new RecordGatewayHeartbeat(
                client,
                connectionId,
                session.ConnectionEpoch,
                Guid.NewGuid(),
                sequence,
                now.AddSeconds(1)));

        (await SendAsync(2)).Disposition.Should().Be(PresenceMessageDisposition.Accepted);
        (await SendAsync(2)).Disposition.Should().Be(PresenceMessageDisposition.Duplicate);
        (await SendAsync(1)).Disposition.Should().Be(PresenceMessageDisposition.StaleSequence);
    }

    [Fact]
    public async Task Disconnect_MarksOnlyTheShadowSnapshotOffline()
    {
        var client = new ClientKey(9, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var actor = _system.ActorOf(ClientActor.Props(client, CreateOptions()));
        var now = DateTimeOffset.UtcNow;
        var session = await actor.Ask<GatewayPresenceSessionStarted>(new StartGatewayPresenceSession(
            client,
            connectionId,
            Guid.NewGuid(),
            "1.0",
            "1.2.3",
            ["presence"],
            null,
            now));

        var ended = await actor.Ask<PresenceMessageResult>(new EndGatewayPresenceSession(
            client,
            connectionId,
            session.ConnectionEpoch,
            "test",
            now.AddSeconds(1)));
        var snapshot = await actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(client));

        ended.Disposition.Should().Be(PresenceMessageDisposition.Accepted);
        snapshot.Status.Should().Be(ShadowPresenceStatus.Offline);
        snapshot.IsAuthoritative.Should().BeFalse();
    }

    [Fact]
    public async Task MissedHeartbeatDeadline_MarksTheShadowSnapshotOffline()
    {
        var client = new ClientKey(11, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var options = CreateOptions();
        options.HeartbeatIntervalSeconds = 1;
        options.MissedHeartbeatLimit = 1;
        options.HeartbeatGraceSeconds = 0;
        var offlineTransition = new TaskCompletionSource<ShadowPresenceChanged>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = _system.ActorOf(global::Akka.Actor.Props.Create(
            () => new ShadowPresenceObserver(offlineTransition)));
        _system.EventStream.Subscribe(observer, typeof(ShadowPresenceChanged));
        var actor = _system.ActorOf(ClientActor.Props(client, options));

        await actor.Ask<GatewayPresenceSessionStarted>(new StartGatewayPresenceSession(
            client,
            connectionId,
            Guid.NewGuid(),
            "1.0",
            "1.2.3",
            ["presence"],
            null,
            DateTimeOffset.UtcNow));

        var transition = await offlineTransition.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var snapshot = await actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(client));

        transition.Reason.Should().Be("heartbeat-expired");
        snapshot.Status.Should().Be(ShadowPresenceStatus.Offline);
        snapshot.IsAuthoritative.Should().BeFalse();
    }

    [Fact]
    public async Task PresenceReadModel_TracksGatewaySnapshotsWithoutCreatingAdditionalClientActors()
    {
        var client = new ClientKey(27, Guid.NewGuid());
        var readModel = _system.ActorOf(PresenceReadModelActor.Props());
        var actor = _system.ActorOf(ClientActor.Props(client, CreateOptions(), readModel));
        var now = DateTimeOffset.UtcNow;

        await actor.Ask<GatewayPresenceSessionStarted>(new StartGatewayPresenceSession(
            client,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "1.0",
            "0.4.94",
            ["presence"],
            null,
            now));

        var projection = await readModel.Ask<ClientPresenceReadModelSnapshot>(new GetClientPresenceReadModel());

        projection.Revision.Should().Be(1);
        projection.Items.Should().ContainSingle(snapshot =>
            snapshot.Client == client &&
            snapshot.Status == ShadowPresenceStatus.Online &&
            snapshot.AgentVersion == "0.4.94");
    }

    private static NetRatelAkkaMigrationOptions CreateOptions() => new()
    {
        Enabled = true,
        PresenceEnabled = true,
        GatewayEnabled = true,
        HeartbeatIntervalSeconds = 15,
        MissedHeartbeatLimit = 3,
        HeartbeatGraceSeconds = 5
    };

    private sealed class ShadowPresenceObserver : ReceiveActor
    {
        public ShadowPresenceObserver(TaskCompletionSource<ShadowPresenceChanged> offlineTransition)
        {
            Receive<ShadowPresenceChanged>(transition =>
            {
                if (transition.Status == ShadowPresenceStatus.Offline)
                {
                    offlineTransition.TrySetResult(transition);
                }
            });
        }
    }
}
