using Akka.Actor;
using FluentAssertions;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Presence;
using NetRatel.Application.Presence;
using NetRatel.Tests.API;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class PresenceProvisionalAdmissionTests : IAsyncLifetime
{
    private ActorSystem _system = null!;
    public ValueTask InitializeAsync()
    {
        _system = ActorSystem.Create($"presence-provisional-{Guid.NewGuid():N}");
        return ValueTask.CompletedTask;
    }
    public async ValueTask DisposeAsync() => await _system.Terminate();

    [Fact]
    public async Task ProvisionalHello_DoesNotPublishOnlineUntilTheFirstValidHeartbeat()
    {
        var clock = new RenewalManualTimeProvider();
        var key = new ClientKey(91, Guid.NewGuid());
        var actor = CreateActor(key, clock);
        var admission = Start(key, Guid.NewGuid(), clock.GetUtcNow());
        var started = await actor.Ask<GatewayPresenceSessionStarted>(admission);
        started.Disposition.Should().Be(PresenceMessageDisposition.Accepted);
        var beforeHeartbeat = await SnapshotAsync(actor, key);
        beforeHeartbeat.Status.Should().Be(ClientPresenceStatus.Unknown);
        beforeHeartbeat.ConnectionId.Should().BeNull();
        beforeHeartbeat.AuthenticationExpiresAtUtc.Should().BeNull();
        (await actor.Ask<PresenceMessageResult>(Heartbeat(started, clock.GetUtcNow(), 0)))
            .Disposition.Should().Be(PresenceMessageDisposition.StaleSequence);
        (await SnapshotAsync(actor, key)).Status.Should().Be(ClientPresenceStatus.Unknown);
        (await actor.Ask<PresenceMessageResult>(Heartbeat(started, clock.GetUtcNow(), 1)))
            .Disposition.Should().Be(PresenceMessageDisposition.Accepted);
        var committed = await SnapshotAsync(actor, key);
        committed.Status.Should().Be(ClientPresenceStatus.Online);
        committed.ConnectionId.Should().Be(started.ConnectionId);
        committed.ConnectionEpoch.Should().Be(started.ConnectionEpoch);
        committed.LastAcceptedSequence.Should().Be(1);
        committed.AuthenticationExpiresAtUtc.Should().Be(admission.AuthenticationExpiresAtUtc);
    }

    [Fact]
    public async Task CancelBeforeDelayedStart_AfterSuccessorCommitCannotDisplaceTheSuccessor()
    {
        var clock = new RenewalManualTimeProvider();
        var key = new ClientKey(91, Guid.NewGuid());
        var actor = CreateActor(key, clock);
        var abandoned = Start(key, Guid.NewGuid(), clock.GetUtcNow());
        await CancelAsync(actor, key, abandoned.ConnectionId, clock.GetUtcNow());
        var successor = await StartAndCommitAsync(actor, Start(key, Guid.NewGuid(), clock.GetUtcNow()), clock.GetUtcNow());
        var delayed = await actor.Ask<GatewayPresenceSessionStarted>(abandoned);
        delayed.Disposition.Should().Be(PresenceMessageDisposition.AdmissionCancelled);
        (await actor.Ask<PresenceMessageResult>(Heartbeat(delayed, clock.GetUtcNow(), 1)))
            .Disposition.Should().NotBe(PresenceMessageDisposition.Accepted);
        await AssertCurrentAsync(actor, key, successor);
    }

    [Fact]
    public async Task OlderPendingHeartbeat_AfterNewerCommitCannotStealAuthority()
    {
        var clock = new RenewalManualTimeProvider();
        var key = new ClientKey(91, Guid.NewGuid());
        var actor = CreateActor(key, clock);
        var olderStart = Start(key, Guid.NewGuid(), clock.GetUtcNow());
        var older = await actor.Ask<GatewayPresenceSessionStarted>(olderStart);
        var successor = await StartAndCommitAsync(actor, Start(key, Guid.NewGuid(), clock.GetUtcNow()), clock.GetUtcNow());
        (await actor.Ask<PresenceMessageResult>(Heartbeat(older, clock.GetUtcNow(), 1)))
            .Disposition.Should().Be(PresenceMessageDisposition.StaleConnectionEpoch);
        (await actor.Ask<GatewayPresenceSessionStarted>(olderStart))
            .Disposition.Should().Be(PresenceMessageDisposition.AdmissionCancelled);
        await AssertCurrentAsync(actor, key, successor);
    }

    [Fact]
    public async Task AdmissionDeadline_RejectsExpiredCandidateAndIndefinitelyLateReplay()
    {
        var clock = new RenewalManualTimeProvider();
        var key = new ClientKey(91, Guid.NewGuid());
        var actor = CreateActor(key, clock);
        var admission = Start(key, Guid.NewGuid(), clock.GetUtcNow());
        var candidate = await actor.Ask<GatewayPresenceSessionStarted>(admission);
        clock.Advance(TimeSpan.FromSeconds(30));
        (await actor.Ask<PresenceMessageResult>(Heartbeat(candidate, clock.GetUtcNow(), 1)))
            .Disposition.Should().NotBe(PresenceMessageDisposition.Accepted);
        (await actor.Ask<GatewayPresenceSessionStarted>(admission))
            .Disposition.Should().Be(PresenceMessageDisposition.AdmissionExpired);
        (await SnapshotAsync(actor, key)).Status.Should().Be(ClientPresenceStatus.Unknown);
        var successor = await StartAndCommitAsync(actor, Start(key, Guid.NewGuid(), clock.GetUtcNow()), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(5));
        (await actor.Ask<GatewayPresenceSessionStarted>(admission))
            .Disposition.Should().Be(PresenceMessageDisposition.AdmissionExpired);
        (await SnapshotAsync(actor, key)).ConnectionId.Should().Be(successor.ConnectionId);
    }

    [Fact]
    public async Task PendingCapacity_IsBoundedAndAnExactCancelReleasesOneSlot()
    {
        var clock = new RenewalManualTimeProvider();
        var key = new ClientKey(91, Guid.NewGuid());
        var actor = CreateActor(key, clock);
        GatewayPresenceSessionStarted? first = null;
        for (var index = 0; index < 16; index++)
        {
            var candidate = await actor.Ask<GatewayPresenceSessionStarted>(Start(key, Guid.NewGuid(), clock.GetUtcNow()));
            candidate.Disposition.Should().Be(PresenceMessageDisposition.Accepted);
            first ??= candidate;
        }
        (await actor.Ask<GatewayPresenceSessionStarted>(Start(key, Guid.NewGuid(), clock.GetUtcNow())))
            .Disposition.Should().Be(PresenceMessageDisposition.AdmissionCapacityExceeded);
        await CancelAsync(actor, key, first!.ConnectionId, clock.GetUtcNow());
        (await actor.Ask<GatewayPresenceSessionStarted>(Start(key, Guid.NewGuid(), clock.GetUtcNow())))
            .Disposition.Should().Be(PresenceMessageDisposition.Accepted);
        (await SnapshotAsync(actor, key)).Status.Should().Be(ClientPresenceStatus.Unknown);
    }

    [Fact]
    public async Task CancellationOverflow_FailsClosedUntilUnrecordedCancellationDeadlineExpires()
    {
        var clock = new RenewalManualTimeProvider();
        var key = new ClientKey(91, Guid.NewGuid());
        var actor = CreateActor(key, clock);
        for (var index = 0; index < 64; index++)
            await CancelAsync(actor, key, Guid.NewGuid(), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(1));
        var overflowId = Guid.NewGuid();
        await CancelAsync(actor, key, overflowId, clock.GetUtcNow());
        var overflowStart = Start(key, overflowId, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(29));
        (await actor.Ask<GatewayPresenceSessionStarted>(overflowStart))
            .Disposition.Should().Be(PresenceMessageDisposition.AdmissionCapacityExceeded);
        clock.Advance(TimeSpan.FromSeconds(1));
        (await actor.Ask<GatewayPresenceSessionStarted>(overflowStart))
            .Disposition.Should().Be(PresenceMessageDisposition.AdmissionExpired);
        (await actor.Ask<GatewayPresenceSessionStarted>(Start(key, Guid.NewGuid(), clock.GetUtcNow())))
            .Disposition.Should().Be(PresenceMessageDisposition.Accepted);
    }

    [Fact]
    public async Task LostReplyCancelWithUnknownEpoch_RetiresOnlyTheExactConnection()
    {
        var clock = new RenewalManualTimeProvider();
        var key = new ClientKey(91, Guid.NewGuid());
        var actor = CreateActor(key, clock);
        var predecessor = await StartAndCommitAsync(actor, Start(key, Guid.NewGuid(), clock.GetUtcNow()), clock.GetUtcNow());
        var successor = await StartAndCommitAsync(actor, Start(key, Guid.NewGuid(), clock.GetUtcNow()), clock.GetUtcNow());
        await CancelAsync(actor, key, predecessor.ConnectionId, clock.GetUtcNow());
        await AssertCurrentAsync(actor, key, successor);
        await CancelAsync(actor, key, successor.ConnectionId, clock.GetUtcNow());
        (await SnapshotAsync(actor, key)).Status.Should().Be(ClientPresenceStatus.Offline);
    }

    [Fact]
    public async Task MalformedFirstHeartbeatRenewal_DoesNotCommitOrDisplaceCurrentAuthority()
    {
        var clock = new RenewalManualTimeProvider();
        var key = new ClientKey(91, Guid.NewGuid());
        var actor = CreateActor(key, clock);
        var current = await StartAndCommitAsync(actor, Start(key, Guid.NewGuid(), clock.GetUtcNow()), clock.GetUtcNow());
        var candidateStart = Start(key, Guid.NewGuid(), clock.GetUtcNow());
        var candidate = await actor.Ask<GatewayPresenceSessionStarted>(candidateStart);
        var malformed = Heartbeat(candidate, clock.GetUtcNow(), 1) with
        {
            RenewedAuthenticationExpiresAtUtc = candidateStart.AuthenticationExpiresAtUtc
        };
        (await actor.Ask<PresenceMessageResult>(malformed))
            .Disposition.Should().Be(PresenceMessageDisposition.InvalidAuthenticationRenewal);
        await AssertCurrentAsync(actor, key, current);
        (await actor.Ask<PresenceMessageResult>(Heartbeat(candidate, clock.GetUtcNow(), 1)))
            .Disposition.Should().Be(PresenceMessageDisposition.Accepted);
        await AssertCurrentAsync(actor, key, candidate);
    }

    private IActorRef CreateActor(ClientKey key, TimeProvider clock) => _system.ActorOf(
        PresenceActor.Props(key, new NetRatelAkkaOptions(), ActorRefs.Nobody, clock));
    private static StartGatewayPresenceSession Start(ClientKey key, Guid connectionId, DateTimeOffset now) =>
        new(key, connectionId, Guid.NewGuid(), "1.0", "provisional-test", ["presence"], null, now,
            AuthenticationExpiresAtUtc: now.AddMinutes(10), AdmissionExpiresAtUtc: now.AddSeconds(30), ProvisionalAdmission: true);
    private static RecordGatewayHeartbeat Heartbeat(GatewayPresenceSessionStarted candidate, DateTimeOffset now, ulong sequence) =>
        new(candidate.Client, candidate.ConnectionId, candidate.ConnectionEpoch, Guid.NewGuid(), sequence, now);
    private static Task<PresenceMessageResult> CancelAsync(IActorRef actor, ClientKey key, Guid connectionId, DateTimeOffset now) =>
        actor.Ask<PresenceMessageResult>(new EndGatewayPresenceSession(key, connectionId, 0, "abandoned", now, CancelPendingAdmission: true));
    private static Task<ClientPresenceSnapshot> SnapshotAsync(IActorRef actor, ClientKey key) => actor.Ask<ClientPresenceSnapshot>(new GetClientPresence(key));
    private static async Task<GatewayPresenceSessionStarted> StartAndCommitAsync(IActorRef actor, StartGatewayPresenceSession admission, DateTimeOffset now)
    {
        var candidate = await actor.Ask<GatewayPresenceSessionStarted>(admission);
        candidate.Disposition.Should().Be(PresenceMessageDisposition.Accepted);
        (await actor.Ask<PresenceMessageResult>(Heartbeat(candidate, now, 1))).Disposition.Should().Be(PresenceMessageDisposition.Accepted);
        return candidate;
    }
    private static async Task AssertCurrentAsync(IActorRef actor, ClientKey key, GatewayPresenceSessionStarted expected)
    {
        var snapshot = await SnapshotAsync(actor, key);
        snapshot.Status.Should().Be(ClientPresenceStatus.Online);
        snapshot.ConnectionId.Should().Be(expected.ConnectionId);
        snapshot.ConnectionEpoch.Should().Be(expected.ConnectionEpoch);
        snapshot.LastAcceptedSequence.Should().Be(1);
    }
}
