using Akka.Actor;
using AwesomeAssertions;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Presence;
using NetRatel.Application.Presence;
using NetRatel.Tests.API;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class PresenceAuthenticationRenewalTests : IAsyncLifetime
{
    private ActorSystem _system = null!;
    public ValueTask InitializeAsync() { _system = ActorSystem.Create($"presence-auth-{Guid.NewGuid():N}"); return ValueTask.CompletedTask; }
    public async ValueTask DisposeAsync() => await _system.Terminate();

    [Fact]
    public async Task TwoRenewals_AtomicallyExtendTheSameOwner_AndFenceOldExpiryAndReplay()
    {
        var clock = new RenewalManualTimeProvider();
        var client = new ClientKey(79, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var actor = _system.ActorOf(PresenceActor.Props(client, new NetRatelAkkaOptions(), ActorRefs.Nobody, clock));
        var firstExpiry = clock.GetUtcNow().AddMinutes(2);
        var started = await actor.Ask<GatewayPresenceSessionStarted>(Start(client, connection, clock.GetUtcNow(), firstExpiry));
        var firstRenewal = Heartbeat(client, connection, started.ConnectionEpoch, 1, clock.GetUtcNow(), firstExpiry.AddMinutes(2));
        (await actor.Ask<PresenceMessageResult>(firstRenewal)).Disposition.Should().Be(PresenceMessageDisposition.Accepted);
        (await actor.Ask<PresenceMessageResult>(firstRenewal with { RenewedAuthenticationExpiresAtUtc = firstExpiry.AddHours(1) }))
            .Disposition.Should().Be(PresenceMessageDisposition.Duplicate);
        var finalExpiry = firstExpiry.AddMinutes(4);
        (await actor.Ask<PresenceMessageResult>(Heartbeat(client, connection, started.ConnectionEpoch, 2, clock.GetUtcNow(), finalExpiry)))
            .Disposition.Should().Be(PresenceMessageDisposition.Accepted);
        clock.Advance(TimeSpan.FromMinutes(2));
        actor.Tell(new PresenceActor.AuthenticationDeadlineElapsed(started.ConnectionEpoch, connection, firstExpiry));
        var snapshot = await actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(client));
        snapshot.Status.Should().Be(ClientPresenceStatus.Online);
        snapshot.ConnectionId.Should().Be(connection);
        snapshot.ConnectionEpoch.Should().Be(started.ConnectionEpoch);
        snapshot.AuthenticationExpiresAtUtc.Should().Be(finalExpiry);
        snapshot.LastAcceptedSequence.Should().Be(2);
        clock.Advance(TimeSpan.FromMinutes(4));
        actor.Tell(new PresenceActor.AuthenticationDeadlineElapsed(started.ConnectionEpoch, connection, finalExpiry));
        (await actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(client))).Status.Should().Be(ClientPresenceStatus.Offline);
        (await actor.Ask<PresenceMessageResult>(Heartbeat(client, connection, started.ConnectionEpoch, 3, clock.GetUtcNow(), finalExpiry.AddMinutes(10))))
            .Disposition.Should().Be(PresenceMessageDisposition.NoActiveSession);
    }

    [Fact]
    public async Task ExpiredInitialOrCurrentAuthority_CannotBeAdmittedOrRevivedByRenewal()
    {
        var clock = new RenewalManualTimeProvider();
        var client = new ClientKey(79, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var actor = _system.ActorOf(PresenceActor.Props(client, new NetRatelAkkaOptions(), ActorRefs.Nobody, clock));
        (await actor.Ask<GatewayPresenceSessionStarted>(Start(client, connection, clock.GetUtcNow(), clock.GetUtcNow())))
            .Disposition.Should().Be(PresenceMessageDisposition.AuthenticationExpired);
        (await actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(client))).Status.Should().Be(ClientPresenceStatus.Unknown);
        var expiry = clock.GetUtcNow().AddMinutes(2);
        var started = await actor.Ask<GatewayPresenceSessionStarted>(Start(client, connection, clock.GetUtcNow(), expiry));
        clock.Advance(TimeSpan.FromMinutes(2));
        (await actor.Ask<PresenceMessageResult>(Heartbeat(client, connection, started.ConnectionEpoch, 1, clock.GetUtcNow(), expiry.AddMinutes(10))))
            .Disposition.Should().Be(PresenceMessageDisposition.AuthenticationExpired);
        var snapshot = await actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(client));
        snapshot.Status.Should().Be(ClientPresenceStatus.Offline);
        snapshot.AuthenticationExpiresAtUtc.Should().Be(expiry);
        snapshot.LastAcceptedSequence.Should().Be(0);
    }

    [Fact]
    public async Task StaleOwnerRenewalExpiryAndCleanup_CannotChangeTheAuthorizedSuccessor()
    {
        var clock = new RenewalManualTimeProvider();
        var client = new ClientKey(79, Guid.NewGuid());
        var firstId = Guid.NewGuid();
        var successorId = Guid.NewGuid();
        var actor = _system.ActorOf(PresenceActor.Props(client, new NetRatelAkkaOptions(), ActorRefs.Nobody, clock));
        var expiry = clock.GetUtcNow().AddMinutes(2);
        var first = await actor.Ask<GatewayPresenceSessionStarted>(Start(client, firstId, clock.GetUtcNow(), expiry));
        var successor = await actor.Ask<GatewayPresenceSessionStarted>(Start(client, successorId, clock.GetUtcNow(), expiry.AddMinutes(5)));
        (await actor.Ask<PresenceMessageResult>(Heartbeat(client, firstId, first.ConnectionEpoch, 1, clock.GetUtcNow(), expiry.AddMinutes(10))))
            .Disposition.Should().Be(PresenceMessageDisposition.StaleConnectionEpoch);
        actor.Tell(new PresenceActor.AuthenticationDeadlineElapsed(first.ConnectionEpoch, firstId, expiry));
        await actor.Ask<PresenceMessageResult>(new EndGatewayPresenceSession(client, firstId, first.ConnectionEpoch, "late", clock.GetUtcNow()));
        var snapshot = await actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(client));
        snapshot.Status.Should().Be(ClientPresenceStatus.Online);
        snapshot.ConnectionId.Should().Be(successorId);
        snapshot.ConnectionEpoch.Should().Be(successor.ConnectionEpoch);
        snapshot.AuthenticationExpiresAtUtc.Should().Be(expiry.AddMinutes(5));
    }

    [Fact]
    public async Task ExpiredHeartbeatOwner_CannotBeRevivedByALateSameOwnerFrame()
    {
        var clock = new RenewalManualTimeProvider();
        var client = new ClientKey(79, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var actor = _system.ActorOf(PresenceActor.Props(client, new NetRatelAkkaOptions(), ActorRefs.Nobody, clock));
        var expiry = clock.GetUtcNow().AddMinutes(2);
        var started = await actor.Ask<GatewayPresenceSessionStarted>(Start(client, connection, clock.GetUtcNow(), expiry));
        actor.Tell(new PresenceActor.PresenceDeadlineElapsed(started.ConnectionEpoch, connection));
        (await actor.Ask<PresenceMessageResult>(Heartbeat(client, connection, started.ConnectionEpoch, 1, clock.GetUtcNow(), expiry.AddMinutes(10))))
            .Disposition.Should().Be(PresenceMessageDisposition.NoActiveSession);
        (await actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(client))).Status.Should().Be(ClientPresenceStatus.Offline);
    }

    [Fact]
    public async Task LegacyAdmission_CannotAcquireRenewableAuthorityFromAnOrdinaryHeartbeat()
    {
        var clock = new RenewalManualTimeProvider();
        var client = new ClientKey(79, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var actor = _system.ActorOf(PresenceActor.Props(client, new NetRatelAkkaOptions(), ActorRefs.Nobody, clock));
        var started = await actor.Ask<GatewayPresenceSessionStarted>(Start(client, connection, clock.GetUtcNow(), null));
        (await actor.Ask<PresenceMessageResult>(Heartbeat(client, connection, started.ConnectionEpoch, 1, clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(10))))
            .Disposition.Should().Be(PresenceMessageDisposition.InvalidAuthenticationRenewal);
        var snapshot = await actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(client));
        snapshot.AuthenticationExpiresAtUtc.Should().BeNull();
        snapshot.LastAcceptedSequence.Should().Be(0);
    }

    private static StartGatewayPresenceSession Start(ClientKey client, Guid connection, DateTimeOffset now, DateTimeOffset? expiry) =>
        new(client, connection, Guid.NewGuid(), "1.0", "renewal-test", ["presence"], null, now, expiry);
    private static RecordGatewayHeartbeat Heartbeat(ClientKey client, Guid connection, long epoch, ulong sequence, DateTimeOffset now, DateTimeOffset expiry) =>
        new(client, connection, epoch, Guid.NewGuid(), sequence, now, RenewedAuthenticationExpiresAtUtc: expiry);
}
