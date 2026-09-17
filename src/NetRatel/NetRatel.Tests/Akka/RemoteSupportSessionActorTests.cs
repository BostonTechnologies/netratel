using Akka.Actor;
using FluentAssertions;
using FluentAssertions.Execution;
using System.Threading.Channels;
using System.Text;
using System.Text.Json;
using NetRatel.Akka.RemoteSupport;
using NetRatel.Application.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class RemoteSupportSessionActorTests
{
    [Theory]
    [InlineData("stale")]
    [InlineData("duplicate")]
    [InlineData("current")]
    public async Task CloseRevisionFence_OnlyAnAppliedCloseCancelsThePendingTargetTransition(string attempt)
    {
        var store = new InMemoryLifecycleStore();
        var opened = await store.OpenAsync(OpenCommand(), Guid.NewGuid(), CancellationToken.None);
        var system = ActorSystem.Create($"remote-support-close-fence-{Guid.NewGuid():N}");
        try
        {
            var session = opened.Snapshot.Session;
            var authority = system.ActorOf(RemoteSupportSessionActor.Props(session, store));
            await authority.Ask<RemoteSupportLifecycleTransitionResult>(new PrepareRemoteSupportMedia(
                session, new("operator-a"), Prepared(session, opened.Snapshot.Target), 1, Guid.NewGuid(), DateTimeOffset.UtcNow));
            var edge = system.ActorOf(Props.Create(() => new RecordingAgentEdge()));
            await authority.Ask<RemoteSupportAgentEdgeRegistration>(new RegisterRemoteSupportAgentEdge(
                session, session.AgentId, Guid.NewGuid(), 1, Guid.NewGuid(), 1, edge));
            var transitionRequestId = Guid.NewGuid();
            var transition = await authority.Ask<RemoteSupportTransitionDecision>(new StartRemoteSupportTargetTransition(
                session, transitionRequestId, 3, 1, RemoteSupportTransitionReason.ConsoleLoginDetected, true, DateTimeOffset.UtcNow));
            transition.Accepted.Should().BeTrue();
            var before = await authority.Ask<RemoteSupportSessionSnapshot>(new GetRemoteSupportSession(new("operator-a")));
            var effectsBefore = await edge.Ask<RemoteSupportAgentRouteEnvelope[]>(ReadRecordedEnvelopes.Instance);

            var close = await authority.Ask<RemoteSupportLifecycleTransitionResult>(new ControlRemoteSupportSession(
                Close(session, "operator-a", attempt == "duplicate" ? transitionRequestId : Guid.NewGuid(),
                    before.LifecycleRevision - (attempt == "current" ? 0 : 1))));
            var effectsAfter = await edge.Ask<RemoteSupportAgentRouteEnvelope[]>(ReadRecordedEnvelopes.Instance);
            var inventory = new RemoteSupportTargetInventorySnapshot(
                RemoteSupportV2ContractVersions.Current, session.TenantId, session.AgentId, 2,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
                [new(7, "active", "sid-seven", false, true, false, false, true, true, "1.0.0")]);
            var continued = await authority.Ask<RemoteSupportTransitionDecision>(new ObserveRemoteSupportTransitionInventory(
                session, Guid.NewGuid(), transition.Fence!.TransitionId, 3, inventory, DateTimeOffset.UtcNow));

            using var assertions = new AssertionScope();
            close.Disposition.Should().Be(attempt switch
            {
                "stale" => RemoteSupportLifecycleTransitionDisposition.StaleRevision,
                "duplicate" => RemoteSupportLifecycleTransitionDisposition.Duplicate,
                _ => RemoteSupportLifecycleTransitionDisposition.Applied
            });
            if (attempt == "current")
            {
                close.Snapshot!.State.Should().Be(RemoteSupportV2SessionStates.Completed);
                effectsAfter.Should().HaveCount(effectsBefore.Length + 1);
                JsonSerializer.Deserialize(effectsAfter[^1].Payload, RemoteSupportV2JsonContext.Default.RemoteSupportTransitionEffect)!
                    .Kind.Should().Be(RemoteSupportTransitionEffectKinds.Cancel);
                continued.Accepted.Should().BeFalse("a completed session cannot continue its transition");
            }
            else
            {
                close.Snapshot.Should().Be(before);
                effectsAfter.Should().Equal(effectsBefore, "an unapplied close must not cancel pending agent work");
                continued.Accepted.Should().BeTrue("an unapplied close must not close the transition coordinator");
            }
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("duplicate")]
    [InlineData("current")]
    public async Task PreparingTarget_OnlyAFreshRevisionCanReplaceThePreparedHelperRoute(string attempt)
    {
        var store = new InMemoryLifecycleStore();
        var opened = await store.OpenAsync(OpenCommand(), Guid.NewGuid(), CancellationToken.None);
        var system = ActorSystem.Create($"remote-support-preparation-fence-{Guid.NewGuid():N}");
        try
        {
            var session = opened.Snapshot.Session;
            var authority = system.ActorOf(RemoteSupportSessionActor.Props(session, store));
            var original = Prepared(session, opened.Snapshot.Target);
            var initialRequestId = Guid.NewGuid();
            var initial = await authority.Ask<RemoteSupportLifecycleTransitionResult>(new PrepareRemoteSupportMedia(
                session, new("operator-a"), original, 1, initialRequestId, DateTimeOffset.UtcNow));
            initial.Snapshot!.State.Should().Be(RemoteSupportV2SessionStates.PreparingTarget);
            var replacement = Prepared(session, opened.Snapshot.Target);
            var repeated = await authority.Ask<RemoteSupportLifecycleTransitionResult>(new PrepareRemoteSupportMedia(
                session, new("operator-a"), replacement, attempt == "current" ? initial.Snapshot.LifecycleRevision : 1,
                attempt == "duplicate" ? initialRequestId : Guid.NewGuid(), DateTimeOffset.UtcNow));

            var edge = system.ActorOf(Props.Create(() => new RecordingAgentEdge()));
            await authority.Ask<RemoteSupportAgentEdgeRegistration>(new RegisterRemoteSupportAgentEdge(
                session, session.AgentId, Guid.NewGuid(), 1, Guid.NewGuid(), 1, edge));
            var ready = await authority.Ask<RemoteSupportSessionSnapshot>(new GetRemoteSupportSession(new("operator-a")));
            var control = await authority.Ask<RemoteSupportLifecycleTransitionResult>(new ControlRemoteSupportSession(
                new(RemoteSupportV2ContractVersions.Current, session, new("operator-a"), Guid.NewGuid(),
                    RemoteSupportV2ControlTypes.RequestSas, ready.LifecycleRevision, DateTimeOffset.UtcNow)));
            control.Disposition.Should().Be(RemoteSupportLifecycleTransitionDisposition.Applied);
            var envelopes = await edge.Ask<RemoteSupportAgentRouteEnvelope[]>(ReadRecordedEnvelopes.Instance);
            var dispatched = JsonSerializer.Deserialize(envelopes.Single(envelope => envelope.Kind == "v2_control").Payload,
                RemoteSupportV2JsonContext.Default.RemoteSupportV2PrivilegedControl)!;

            using var assertions = new AssertionScope();
            repeated.Disposition.Should().Be(attempt switch
            {
                "stale" => RemoteSupportLifecycleTransitionDisposition.StaleRevision,
                "duplicate" => RemoteSupportLifecycleTransitionDisposition.Duplicate,
                _ => RemoteSupportLifecycleTransitionDisposition.Applied
            });
            repeated.Snapshot!.LifecycleRevision.Should().Be(initial.Snapshot.LifecycleRevision + (attempt == "current" ? 1 : 0));
            dispatched.HelperRouteId.Should().Be((attempt == "current" ? replacement : original).HelperRoute!.HelperRouteId,
                "stale or duplicate preparation must not replace the helper used by subsequent control");
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task OperatorBoundControl_IsFencedAndPersistsAnAppendOnlyAuditEvent()
    {
        var store = new InMemoryLifecycleStore();
        var command = OpenCommand();
        var opened = await store.OpenAsync(command, Guid.NewGuid(), CancellationToken.None);
        var system = ActorSystem.Create($"remote-support-{Guid.NewGuid():N}");
        try
        {
            var actor = system.ActorOf(RemoteSupportSessionActor.Props(opened.Snapshot.Session, store));
            var denied = await actor.Ask<RemoteSupportLifecycleTransitionResult>(
                new ControlRemoteSupportSession(Close(opened.Snapshot.Session, "another-operator", Guid.NewGuid(), 1)));
            var accepted = await actor.Ask<RemoteSupportLifecycleTransitionResult>(
                new ControlRemoteSupportSession(Close(opened.Snapshot.Session, "operator-a", Guid.NewGuid(), 1)));
            var stale = await actor.Ask<RemoteSupportLifecycleTransitionResult>(
                new ControlRemoteSupportSession(Close(opened.Snapshot.Session, "operator-a", Guid.NewGuid(), 1)));

            denied.Disposition.Should().Be(RemoteSupportLifecycleTransitionDisposition.Rejected);
            accepted.Disposition.Should().Be(RemoteSupportLifecycleTransitionDisposition.Applied);
            accepted.Snapshot!.State.Should().Be(RemoteSupportV2SessionStates.Completed);
            accepted.Snapshot.LifecycleRevision.Should().Be(2);
            stale.Disposition.Should().Be(RemoteSupportLifecycleTransitionDisposition.Rejected);
            (await store.ReadAuditAsync(opened.Snapshot.Session, 0, CancellationToken.None))
                .Select(audit => audit.EventType)
                .Should().Equal(RemoteSupportV2AuditEventTypes.SessionRequested, RemoteSupportV2AuditEventTypes.SessionClosed);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task RestartedActor_RehydratesActiveSessionAsRenegotiationRequired()
    {
        var store = new InMemoryLifecycleStore();
        var command = OpenCommand() with
        {
            Target = new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.InteractiveUser, 4, "sid-four", 1)
        };
        var opened = await store.OpenAsync(command, Guid.NewGuid(), CancellationToken.None);
        var firstSystem = ActorSystem.Create($"remote-support-first-{Guid.NewGuid():N}");
        var first = firstSystem.ActorOf(RemoteSupportSessionActor.Props(opened.Snapshot.Session, store));
        var prepared = await first.Ask<RemoteSupportLifecycleTransitionResult>(
            new AdvanceRemoteSupportSession(new(
                RemoteSupportV2ContractVersions.Current,
                opened.Snapshot.Session,
                Guid.NewGuid(),
                RemoteSupportV2SessionStates.PreparingTarget,
                1,
                DateTimeOffset.UtcNow)));
        prepared.Disposition.Should().Be(RemoteSupportLifecycleTransitionDisposition.Applied);
        await firstSystem.Terminate();

        var recoveredSystem = ActorSystem.Create($"remote-support-recovered-{Guid.NewGuid():N}");
        try
        {
            var recovered = recoveredSystem.ActorOf(RemoteSupportSessionActor.Props(opened.Snapshot.Session, store));
            var snapshot = await recovered.Ask<RemoteSupportSessionSnapshot?>(new GetRemoteSupportSession(new("operator-a")));

            snapshot.Should().NotBeNull();
            snapshot!.State.Should().Be(RemoteSupportV2SessionStates.RenegotiationRequired);
            snapshot.LifecycleRevision.Should().Be(3);
            (await store.ReadAuditAsync(opened.Snapshot.Session, 0, CancellationToken.None))
                .Should().Contain(audit => audit.FailureCode == "renegotiation_required_after_recovery");
        }
        finally
        {
            await recoveredSystem.Terminate();
        }
    }

    [Fact]
    public async Task AgentEdge_IsEphemeralFencedAndRoutesOnlyTheCurrentGeneration()
    {
        var store = new InMemoryLifecycleStore();
        var command = OpenCommand();
        var opened = await store.OpenAsync(command, Guid.NewGuid(), CancellationToken.None);
        var system = ActorSystem.Create($"remote-support-agent-edge-{Guid.NewGuid():N}");
        try
        {
            var authority = system.ActorOf(RemoteSupportSessionActor.Props(opened.Snapshot.Session, store));
            var outbound = Channel.CreateBounded<RemoteSupportAgentRouteEnvelope>(1);
            var edge = system.ActorOf(RemoteSupportAgentEdgeActor.Props(outbound));
            var routeId = Guid.NewGuid();
            var connectionId = Guid.NewGuid();
            var registration = await authority.Ask<RemoteSupportAgentEdgeRegistration>(new RegisterRemoteSupportAgentEdge(
                opened.Snapshot.Session,
                opened.Snapshot.Session.AgentId,
                connectionId,
                4,
                routeId,
                1,
                edge));

            registration.Accepted.Should().BeTrue();
            (await authority.Ask<RemoteSupportAgentEdgeRegistration>(new RegisterRemoteSupportAgentEdge(
                opened.Snapshot.Session,
                opened.Snapshot.Session.AgentId,
                connectionId,
                4,
                routeId,
                1,
                edge))).Accepted.Should().BeTrue();
            (await outbound.Reader.ReadAsync()).Kind.Should().Be("renegotiation_required");
            var envelope = new RemoteSupportAgentRouteEnvelope(
                opened.Snapshot.Session,
                routeId,
                1,
                "renegotiation_required",
                []);
            authority.Tell(new RouteRemoteSupportAgentEnvelope(envelope));

            (await outbound.Reader.ReadAsync()).Should().Be(envelope);

            authority.Tell(new RouteRemoteSupportAgentEnvelope(envelope with { RouteGeneration = 0 }));
            outbound.Reader.TryRead(out _).Should().BeFalse();
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task PreparedInteractiveTarget_GatesOfferAndRequiresAllCurrentPeerReadinessProofs()
    {
        var store = new InMemoryLifecycleStore();
        var command = OpenCommand() with
        {
            Target = new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.InteractiveUser, 4, "sid-four", 1)
        };
        var opened = await store.OpenAsync(command, Guid.NewGuid(), CancellationToken.None);
        var system = ActorSystem.Create($"remote-support-media-{Guid.NewGuid():N}");
        try
        {
            var authority = system.ActorOf(RemoteSupportSessionActor.Props(opened.Snapshot.Session, store));
            var rejected = await authority.Ask<RemoteSupportNegotiationIngressResult>(new RemoteSupportNegotiationIngress(
                Envelope(opened.Snapshot.Session, 1, RemoteSupportV2NegotiationDirections.Browser, 1, RemoteSupportV2NegotiationSignalTypes.Offer, "{}"),
                new("operator-a")));
            rejected.Accepted.Should().BeFalse();
            rejected.FailureCode.Should().Be("ready_for_offer_required");

            var prepared = await authority.Ask<RemoteSupportLifecycleTransitionResult>(new PrepareRemoteSupportMedia(
                opened.Snapshot.Session,
                new("operator-a"),
                Prepared(opened.Snapshot.Session, opened.Snapshot.Target),
                1,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow));
            prepared.Snapshot!.State.Should().Be(RemoteSupportV2SessionStates.PreparingTarget);

            var outbound = Channel.CreateBounded<RemoteSupportAgentRouteEnvelope>(4);
            var edge = system.ActorOf(RemoteSupportAgentEdgeActor.Props(outbound));
            var routeId = Guid.NewGuid();
            (await authority.Ask<RemoteSupportAgentEdgeRegistration>(new RegisterRemoteSupportAgentEdge(
                opened.Snapshot.Session, opened.Snapshot.Session.AgentId, Guid.NewGuid(), 3, routeId, 1, edge))).Accepted.Should().BeTrue();
            await outbound.Reader.ReadAsync();
            (await authority.Ask<RemoteSupportSessionSnapshot?>(new GetRemoteSupportSession(new("operator-a"))))!.State
                .Should().Be(RemoteSupportV2SessionStates.ReadyForOffer);

            (await authority.Ask<RemoteSupportNegotiationIngressResult>(new RemoteSupportNegotiationIngress(
                Envelope(opened.Snapshot.Session, 1, RemoteSupportV2NegotiationDirections.Browser, 1, RemoteSupportV2NegotiationSignalTypes.Offer, "{\"type\":\"offer\"}"),
                new("operator-a")))).Accepted.Should().BeTrue();
            (await outbound.Reader.ReadAsync()).Negotiation!.SignalType.Should().Be(RemoteSupportV2NegotiationSignalTypes.Offer);

            (await authority.Ask<RemoteSupportNegotiationIngressResult>(new RemoteSupportNegotiationIngress(
                Envelope(opened.Snapshot.Session, 1, RemoteSupportV2NegotiationDirections.Agent, 1, RemoteSupportV2NegotiationSignalTypes.Answer, "{\"type\":\"answer\"}"),
                AgentEdgeRouteId: routeId, AgentRouteGeneration: 1))).Accepted.Should().BeTrue();
            foreach (var status in new[]
                     {
                         (RemoteSupportV2NegotiationDirections.Browser, "first_frame", (Guid?)null),
                         (RemoteSupportV2NegotiationDirections.Browser, "data_channel_open", (Guid?)null),
                         (RemoteSupportV2NegotiationDirections.Agent, "input_ready", routeId)
                     })
            {
                var ingress = new RemoteSupportNegotiationIngress(
                    Envelope(opened.Snapshot.Session, 1, status.Item1,
                        status.Item1 == RemoteSupportV2NegotiationDirections.Browser
                            ? status.Item2 == "first_frame" ? 2 : 3
                            : 2,
                        RemoteSupportV2NegotiationSignalTypes.Status,
                        $"{{\"code\":\"{status.Item2}\"}}"),
                    status.Item1 == RemoteSupportV2NegotiationDirections.Browser ? new("operator-a") : null,
                    status.Item3,
                    status.Item3 is null ? null : 1);
                (await authority.Ask<RemoteSupportNegotiationIngressResult>(ingress)).Accepted.Should().BeTrue();
            }

            var connected = await authority.Ask<RemoteSupportSessionSnapshot?>(new GetRemoteSupportSession(new("operator-a")));
            connected!.State.Should().Be(RemoteSupportV2SessionStates.Connected);

            authority.Tell(new UnregisterRemoteSupportAgentEdge(opened.Snapshot.Session, routeId, 3));
            var recoveredRouteId = Guid.NewGuid();
            (await authority.Ask<RemoteSupportAgentEdgeRegistration>(new RegisterRemoteSupportAgentEdge(
                opened.Snapshot.Session, opened.Snapshot.Session.AgentId, Guid.NewGuid(), 3, recoveredRouteId, 1, edge))).Accepted.Should().BeTrue();
            var recovery = await outbound.Reader.ReadAsync();
            recovery.Negotiation!.Generation.Should().Be(2);
            recovery.Negotiation.SignalType.Should().Be(RemoteSupportV2NegotiationSignalTypes.Status);
            (await authority.Ask<RemoteSupportSessionSnapshot?>(new GetRemoteSupportSession(new("operator-a"))))!.State
                .Should().Be(RemoteSupportV2SessionStates.ReadyForOffer);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task PreparedConsoleLoginTarget_UsesTheSameReadyForOfferAndGenerationFences()
    {
        var store = new InMemoryLifecycleStore();
        var command = OpenCommand() with { Target = new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.ConsoleLogin) };
        var opened = await store.OpenAsync(command, Guid.NewGuid(), CancellationToken.None);
        var system = ActorSystem.Create($"remote-support-console-{Guid.NewGuid():N}");
        try
        {
            var authority = system.ActorOf(RemoteSupportSessionActor.Props(opened.Snapshot.Session, store));
            var prepared = await authority.Ask<RemoteSupportLifecycleTransitionResult>(new PrepareRemoteSupportMedia(
                opened.Snapshot.Session, new("operator-a"), Prepared(opened.Snapshot.Session, opened.Snapshot.Target),
                1, Guid.NewGuid(), DateTimeOffset.UtcNow));

            prepared.Disposition.Should().Be(RemoteSupportLifecycleTransitionDisposition.Applied);
            prepared.Snapshot!.State.Should().Be(RemoteSupportV2SessionStates.PreparingTarget);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task ConsoleSasControl_IsOperatorBoundDeduplicatedAndRoutedOutsideTheDataChannel()
    {
        var store = new InMemoryLifecycleStore();
        var opened = await store.OpenAsync(OpenCommand(), Guid.NewGuid(), CancellationToken.None);
        var system = ActorSystem.Create($"remote-support-sas-{Guid.NewGuid():N}");
        try
        {
            var authority = system.ActorOf(RemoteSupportSessionActor.Props(opened.Snapshot.Session, store));
            await authority.Ask<RemoteSupportLifecycleTransitionResult>(new PrepareRemoteSupportMedia(
                opened.Snapshot.Session, new("operator-a"), Prepared(opened.Snapshot.Session, opened.Snapshot.Target),
                1, Guid.NewGuid(), DateTimeOffset.UtcNow));
            var outbound = Channel.CreateBounded<RemoteSupportAgentRouteEnvelope>(4);
            var edge = system.ActorOf(RemoteSupportAgentEdgeActor.Props(outbound));
            var routeId = Guid.NewGuid();
            await authority.Ask<RemoteSupportAgentEdgeRegistration>(new RegisterRemoteSupportAgentEdge(
                opened.Snapshot.Session, opened.Snapshot.Session.AgentId, Guid.NewGuid(), 1, routeId, 1, edge));
            await outbound.Reader.ReadAsync();
            var snapshot = await authority.Ask<RemoteSupportSessionSnapshot?>(new GetRemoteSupportSession(new("operator-a")));
            var commandId = Guid.NewGuid();

            var dispatched = await authority.Ask<RemoteSupportLifecycleTransitionResult>(new ControlRemoteSupportSession(
                new(RemoteSupportV2ContractVersions.Current, opened.Snapshot.Session, new("operator-a"), commandId,
                    RemoteSupportV2ControlTypes.RequestSas, snapshot!.LifecycleRevision, DateTimeOffset.UtcNow)));
            var envelope = await outbound.Reader.ReadAsync();
            var duplicate = await authority.Ask<RemoteSupportLifecycleTransitionResult>(new ControlRemoteSupportSession(
                new(RemoteSupportV2ContractVersions.Current, opened.Snapshot.Session, new("operator-a"), commandId,
                    RemoteSupportV2ControlTypes.RequestSas, dispatched.Snapshot!.LifecycleRevision, DateTimeOffset.UtcNow)));

            dispatched.Disposition.Should().Be(RemoteSupportLifecycleTransitionDisposition.Applied);
            envelope.Kind.Should().Be("v2_control");
            envelope.Negotiation.Should().BeNull();
            duplicate.Disposition.Should().Be(RemoteSupportLifecycleTransitionDisposition.Duplicate);
            outbound.Reader.TryRead(out _).Should().BeFalse();
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task ConsoleToUserTransition_SuspendsOldGenerationAndRequiresFreshReplacementProof()
    {
        var store = new InMemoryLifecycleStore();
        var opened = await store.OpenAsync(OpenCommand(), Guid.NewGuid(), CancellationToken.None);
        var system = ActorSystem.Create($"remote-support-transition-{Guid.NewGuid():N}");
        try
        {
            var authority = system.ActorOf(RemoteSupportSessionActor.Props(opened.Snapshot.Session, store));
            await authority.Ask<RemoteSupportLifecycleTransitionResult>(new PrepareRemoteSupportMedia(
                opened.Snapshot.Session, new("operator-a"), Prepared(opened.Snapshot.Session, opened.Snapshot.Target),
                1, Guid.NewGuid(), DateTimeOffset.UtcNow));
            var outbound = Channel.CreateBounded<RemoteSupportAgentRouteEnvelope>(4);
            var edge = system.ActorOf(RemoteSupportAgentEdgeActor.Props(outbound));
            var routeId = Guid.NewGuid();
            await authority.Ask<RemoteSupportAgentEdgeRegistration>(new RegisterRemoteSupportAgentEdge(
                opened.Snapshot.Session, opened.Snapshot.Session.AgentId, Guid.NewGuid(), 1, routeId, 1, edge));
            (await outbound.Reader.ReadAsync()).Kind.Should().Be("renegotiation_required");
            var start = await authority.Ask<RemoteSupportTransitionDecision>(new StartRemoteSupportTargetTransition(
                opened.Snapshot.Session, Guid.NewGuid(), 3, 1, RemoteSupportTransitionReason.ConsoleLoginDetected, true, DateTimeOffset.UtcNow));

            start.Accepted.Should().BeTrue();
            var reacquire = await outbound.Reader.ReadAsync();
            reacquire.Kind.Should().Be("v2_transition_effect");
            JsonSerializer.Deserialize(reacquire.Payload, RemoteSupportV2JsonContext.Default.RemoteSupportTransitionEffect)!.Kind
                .Should().Be(RemoteSupportTransitionEffectKinds.ReacquireInventory);
            (await authority.Ask<RemoteSupportNegotiationIngressResult>(new RemoteSupportNegotiationIngress(
                Envelope(opened.Snapshot.Session, 1, RemoteSupportV2NegotiationDirections.Browser, 1, RemoteSupportV2NegotiationSignalTypes.Offer, "{}"), new("operator-a")))).FailureCode
                .Should().Be("negotiation_generation_stale");
            var inventory = new RemoteSupportTargetInventorySnapshot(
                RemoteSupportV2ContractVersions.Current, opened.Snapshot.Session.TenantId, opened.Snapshot.Session.AgentId,
                2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
                [new RemoteSupportTargetInventoryEntry(7, "active", "sid-seven", false, true, false, false, true, true, "1.0.0")]);
            await authority.Ask<RemoteSupportTransitionDecision>(new ObserveRemoteSupportTransitionInventory(
                opened.Snapshot.Session, Guid.NewGuid(), start.Fence!.TransitionId, 3, inventory, DateTimeOffset.UtcNow));
            JsonSerializer.Deserialize((await outbound.Reader.ReadAsync()).Payload, RemoteSupportV2JsonContext.Default.RemoteSupportTransitionEffect)!.Kind
                .Should().Be(RemoteSupportTransitionEffectKinds.ReacquireInventory);
            var selected = await authority.Ask<RemoteSupportTransitionDecision>(new ObserveRemoteSupportTransitionInventory(
                opened.Snapshot.Session, Guid.NewGuid(), start.Fence.TransitionId, 3, inventory with { InventorySequence = 3 }, DateTimeOffset.UtcNow));
            var prepare = JsonSerializer.Deserialize(
                (await outbound.Reader.ReadAsync()).Payload,
                RemoteSupportV2JsonContext.Default.RemoteSupportTransitionEffect)!;
            prepare.Kind.Should().Be(RemoteSupportTransitionEffectKinds.PrepareReplacementTarget);
            prepare.Target.Should().Be(selected.SelectedTarget);
            var prepared = Prepared(opened.Snapshot.Session, selected.SelectedTarget!);
            var replacement = await authority.Ask<RemoteSupportTransitionDecision>(new CompleteRemoteSupportReplacementPreparation(
                opened.Snapshot.Session, Guid.NewGuid(), start.Fence.TransitionId, 3, prepared, DateTimeOffset.UtcNow));

            selected.Code.Should().Be("target_automatically_selected");
            replacement.FreshGeneration.Should().Be(2);
            (await authority.Ask<RemoteSupportLifecycleTransitionResult>(new ControlRemoteSupportSession(
                new(RemoteSupportV2ContractVersions.Current, opened.Snapshot.Session, new("operator-a"), Guid.NewGuid(),
                    RemoteSupportV2ControlTypes.RequestSas, null, DateTimeOffset.UtcNow)))).Disposition.Should().Be(RemoteSupportLifecycleTransitionDisposition.Rejected);
            (await authority.Ask<RemoteSupportNegotiationIngressResult>(new RemoteSupportNegotiationIngress(
                Envelope(opened.Snapshot.Session, 2, RemoteSupportV2NegotiationDirections.Browser, 1, RemoteSupportV2NegotiationSignalTypes.Offer, "{}"), new("operator-a")))).Accepted.Should().BeTrue();
            (await authority.Ask<RemoteSupportNegotiationIngressResult>(new RemoteSupportNegotiationIngress(
                Envelope(opened.Snapshot.Session, 2, RemoteSupportV2NegotiationDirections.Agent, 1, RemoteSupportV2NegotiationSignalTypes.Answer, "{}"),
                AgentEdgeRouteId: routeId, AgentRouteGeneration: 1))).Accepted.Should().BeTrue();
            foreach (var status in new[]
                     {
                         (RemoteSupportV2NegotiationDirections.Browser, "first_frame", (Guid?)null, 2L),
                         (RemoteSupportV2NegotiationDirections.Browser, "data_channel_open", (Guid?)null, 3L),
                         (RemoteSupportV2NegotiationDirections.Agent, "input_ready", routeId, 2L)
                     })
            {
                (await authority.Ask<RemoteSupportNegotiationIngressResult>(new RemoteSupportNegotiationIngress(
                    Envelope(opened.Snapshot.Session, 2, status.Item1, status.Item4, RemoteSupportV2NegotiationSignalTypes.Status, $"{{\"code\":\"{status.Item2}\"}}"),
                    status.Item1 == RemoteSupportV2NegotiationDirections.Browser ? new("operator-a") : null,
                    status.Item3,
                    status.Item3 is null ? null : 1))).Accepted.Should().BeTrue();
            }
            (await authority.Ask<RemoteSupportTransitionDecision>(new CompleteRemoteSupportReplacementNegotiation(
                opened.Snapshot.Session, Guid.NewGuid(), start.Fence.TransitionId, 3, 1, DateTimeOffset.UtcNow))).Accepted.Should().BeFalse();
            (await authority.Ask<RemoteSupportTransitionDecision>(new CompleteRemoteSupportReplacementNegotiation(
                opened.Snapshot.Session, Guid.NewGuid(), start.Fence.TransitionId, 3, 2, DateTimeOffset.UtcNow))).Code.Should().Be("transition_completed");
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static RemoteSupportOpenSessionCommand OpenCommand() => new(
        RemoteSupportV2ContractVersions.Current,
        8,
        Guid.NewGuid(),
        Guid.NewGuid(),
        new("operator-a"),
        new(RemoteSupportV2TargetKinds.Console),
        ["view"],
        DateTimeOffset.UtcNow);

    private static RemoteSupportControlCommand Close(
        RemoteSupportSessionKey session,
        string operatorId,
        Guid requestId,
        long revision) => new(
        RemoteSupportV2ContractVersions.Current,
        session,
        new(operatorId),
        requestId,
        RemoteSupportV2ControlTypes.Close,
        revision,
        DateTimeOffset.UtcNow);

    private static RemoteSupportPreparedTargetResult Prepared(RemoteSupportSessionKey session, RemoteSupportTargetDescriptor target) => new(
        RemoteSupportV2ContractVersions.Current,
        session.TenantId,
        session.AgentId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        target,
        target.InventorySequence ?? 1,
        true,
        true,
        "target_helper_ready",
        "ready",
        new RemoteSupportHelperRoute(
            Guid.NewGuid(),
            target.WindowsSessionId ?? 3,
            target.UserSidHash ?? "console-login",
            "1.0.0"),
        DateTimeOffset.UtcNow);

    private static RemoteSupportV2NegotiationEnvelope Envelope(
        RemoteSupportSessionKey session,
        long generation,
        string direction,
        long sequence,
        string signalType,
        string payload) => new(session, generation, direction, sequence, Guid.NewGuid(), signalType, Encoding.UTF8.GetBytes(payload));

    private sealed record ReadRecordedEnvelopes
    {
        public static readonly ReadRecordedEnvelopes Instance = new();
    }

    private sealed class RecordingAgentEdge : ReceiveActor
    {
        public RecordingAgentEdge()
        {
            var envelopes = new List<RemoteSupportAgentRouteEnvelope>();
            Receive<RemoteSupportAgentRouteEnvelope>(envelopes.Add);
            Receive<ReadRecordedEnvelopes>(_ => Sender.Tell(envelopes.ToArray()));
        }
    }

    private sealed class InMemoryLifecycleStore : IRemoteSupportLifecycleStore
    {
        private readonly Dictionary<RemoteSupportSessionKey, RemoteSupportSessionSnapshot> _sessions = [];
        private readonly Dictionary<string, RemoteSupportSessionKey> _opens = [];
        private readonly Dictionary<RemoteSupportSessionKey, List<RemoteSupportAuditEvent>> _audit = [];

        public Task<RemoteSupportLifecycleOpenResult> OpenAsync(RemoteSupportOpenSessionCommand command, Guid remoteSupportSessionId, CancellationToken cancellationToken)
        {
            var key = RemoteSupportV2ContractValidator.OpenIdempotencyKey(command);
            if (_opens.TryGetValue(key, out var existing))
            {
                var existingSnapshot = _sessions[existing];
                return Task.FromResult(new RemoteSupportLifecycleOpenResult(existingSnapshot, _audit[existing][0], true));
            }

            var session = new RemoteSupportSessionKey(command.TenantId, command.AgentId, remoteSupportSessionId);
            var snapshot = new RemoteSupportSessionSnapshot(command.ContractVersion, session, command.RequestId,
                command.InitiatingOperator, command.Target, [], RemoteSupportV2SessionStates.Requested, 1,
                command.RequestedAtUtc, command.RequestedAtUtc, command.ExpiresAtUtc);
            var audit = Audit(snapshot, 1, RemoteSupportV2AuditEventTypes.SessionRequested, "operator", command.InitiatingOperator.OperatorId, command.RequestId, "accepted", null, command.RequestedAtUtc);
            _opens.Add(key, session);
            _sessions.Add(session, snapshot);
            _audit.Add(session, [audit]);
            return Task.FromResult(new RemoteSupportLifecycleOpenResult(snapshot, audit, false));
        }

        public Task<RemoteSupportSessionSnapshot?> LoadAsync(RemoteSupportSessionKey session, CancellationToken cancellationToken) =>
            Task.FromResult(_sessions.TryGetValue(session, out var snapshot) ? snapshot : null);

        public Task<RemoteSupportLifecycleTransitionResult> TransitionAsync(RemoteSupportLifecycleTransition transition, CancellationToken cancellationToken)
        {
            if (!_sessions.TryGetValue(transition.Session, out var snapshot))
                return Task.FromResult(new RemoteSupportLifecycleTransitionResult(RemoteSupportLifecycleTransitionDisposition.Missing, null, null));
            var duplicate = _audit[transition.Session].SingleOrDefault(item => item.RequestId == transition.RequestId);
            if (duplicate is not null)
                return Task.FromResult(new RemoteSupportLifecycleTransitionResult(RemoteSupportLifecycleTransitionDisposition.Duplicate, snapshot, duplicate));
            if (transition.ExpectedLifecycleRevision is { } expected && expected != snapshot.LifecycleRevision)
                return Task.FromResult(new RemoteSupportLifecycleTransitionResult(RemoteSupportLifecycleTransitionDisposition.StaleRevision, snapshot, null));

            var updated = snapshot with
            {
                State = transition.NextState,
                LifecycleRevision = snapshot.LifecycleRevision + 1,
                UpdatedAtUtc = transition.OccurredAtUtc,
                TerminalReasonCode = transition.NextState is RemoteSupportV2SessionStates.Completed or
                    RemoteSupportV2SessionStates.Failed or RemoteSupportV2SessionStates.Expired
                    ? transition.FailureCode
                    : null
            };
            var audit = Audit(updated, _audit[transition.Session].Count + 1, transition.EventType,
                transition.ActorKind, transition.ActorId, transition.RequestId, transition.Outcome, transition.FailureCode, transition.OccurredAtUtc);
            _sessions[transition.Session] = updated;
            _audit[transition.Session].Add(audit);
            return Task.FromResult(new RemoteSupportLifecycleTransitionResult(RemoteSupportLifecycleTransitionDisposition.Applied, updated, audit));
        }

        public Task<IReadOnlyList<RemoteSupportAuditEvent>> ReadAuditAsync(RemoteSupportSessionKey session, long afterAuditSequence, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteSupportAuditEvent>>(_audit.TryGetValue(session, out var audit)
                ? audit.Where(item => item.AuditSequence > afterAuditSequence).ToArray()
                : []);

        private static RemoteSupportAuditEvent Audit(RemoteSupportSessionSnapshot snapshot, long sequence, string eventType,
            string actorKind, string actorId, Guid requestId, string outcome, string? failureCode, DateTimeOffset occurredAtUtc) => new(
                snapshot.ContractVersion, snapshot.Session, sequence, Guid.NewGuid(), eventType, actorKind, actorId,
                requestId, outcome, failureCode, occurredAtUtc);
    }
}
