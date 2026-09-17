using Akka.Actor;
using FluentAssertions;
using NetRatel.Akka.Terminals;
using NetRatel.Application.Terminals;
using NetRatel.Shared.Contracts.Terminals;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class TerminalSessionActorTests
{
    [Fact]
    public async Task ValidLifecycle_TracksBoundedMetadataAndScalarDiagnostics()
    {
        var system = ActorSystem.Create($"terminal-shadow-valid-{Guid.NewGuid():N}");
        try
        {
            var key = Session(tenantId: 7, clientId: "client-a", sessionId: "session-a");
            var actor = system.ActorOf(ClientTerminalRouterActor.Props());

            await RecordAsync(actor, Lifecycle(key, TerminalShadowEventKind.SessionRequested));
            await RecordAsync(actor, Lifecycle(key, TerminalShadowEventKind.SessionOpened));
            await RecordAsync(actor, Stream(key, TerminalShadowEventKind.InputObserved, TerminalShadowStreamType.StandardInput, 1, 4));
            await RecordAsync(actor, Stream(key, TerminalShadowEventKind.OutputObserved, TerminalShadowStreamType.StandardOutput, 1, 10));
            await RecordAsync(actor, Stream(key, TerminalShadowEventKind.OutputObserved, TerminalShadowStreamType.StandardError, 2, 5));
            await RecordAsync(actor, Resize(key, sequence: 1, columns: 120, rows: 32));
            await RecordAsync(actor, Lifecycle(
                key,
                TerminalShadowEventKind.SessionCloseRequested,
                TerminalShadowCloseKind.OperatorRequested));
            await RecordAsync(actor, Lifecycle(
                key,
                TerminalShadowEventKind.SessionClosed,
                TerminalShadowCloseKind.RemoteClosed));

            var state = await actor.Ask<TerminalShadowState>(new GetTerminalShadowState(key));
            var route = await actor.Ask<TerminalShadowRouteStatus>(new ProbeTerminalShadowRoute());

            state.Status.Should().Be(TerminalShadowSessionStatus.Closed);
            state.Sequences.Should().BeEquivalentTo(new[]
            {
                new TerminalShadowStreamSequence(TerminalShadowSequenceStream.Input, 1),
                new TerminalShadowStreamSequence(TerminalShadowSequenceStream.Output, 2),
                new TerminalShadowStreamSequence(TerminalShadowSequenceStream.Resize, 1)
            });
            state.InputFrames.Should().Be(1);
            state.OutputFrames.Should().Be(2);
            state.InputBytes.Should().Be(4);
            state.OutputBytes.Should().Be(15);
            state.RecentMetadata.Should().OnlyContain(static entry => entry.PayloadLength >= 0);
            route.ActiveTerminalSessions.Should().Be(0);
            route.ClosedTerminalSessions.Should().Be(1);
            route.LifecycleCounts.Should().Be(new TerminalShadowLifecycleCounts(1, 1, 1, 1, 0));
            route.Authority.Should().Be("unavailable");
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task InvalidTransitions_AreRejectedAndCounted()
    {
        var system = ActorSystem.Create($"terminal-shadow-transitions-{Guid.NewGuid():N}");
        try
        {
            var key = Session(7, "client-a", "session-a");
            var actor = system.ActorOf(ClientTerminalRouterActor.Props());

            await RecordAsync(actor, Lifecycle(key, TerminalShadowEventKind.SessionRequested));
            var beforeOpen = await RecordAsync(
                actor,
                Stream(key, TerminalShadowEventKind.OutputObserved, TerminalShadowStreamType.StandardOutput, 1, 3));
            await RecordAsync(actor, Lifecycle(key, TerminalShadowEventKind.SessionOpened));
            await RecordAsync(actor, Lifecycle(key, TerminalShadowEventKind.SessionClosed));
            var afterClose = await RecordAsync(
                actor,
                Stream(key, TerminalShadowEventKind.InputObserved, TerminalShadowStreamType.StandardInput, 1, 2));
            var reopened = await RecordAsync(actor, Lifecycle(key, TerminalShadowEventKind.SessionOpened));
            var route = await actor.Ask<TerminalShadowRouteStatus>(new ProbeTerminalShadowRoute());

            beforeOpen.Disposition.Should().Be(TerminalShadowMessageDisposition.InvalidTransition);
            afterClose.Disposition.Should().Be(TerminalShadowMessageDisposition.InvalidTransition);
            reopened.Disposition.Should().Be(TerminalShadowMessageDisposition.InvalidTransition);
            route.InvalidTransitions.Should().Be(3);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task SequenceFences_IsolateInputOutputAndResizeAndRejectStaleConflicts()
    {
        var system = ActorSystem.Create($"terminal-shadow-sequences-{Guid.NewGuid():N}");
        try
        {
            var key = Session(7, "client-a", "session-a");
            var actor = system.ActorOf(ClientTerminalRouterActor.Props());
            await OpenAsync(actor, key);

            var output = Stream(key, TerminalShadowEventKind.OutputObserved, TerminalShadowStreamType.StandardOutput, 2, 8);
            var accepted = await RecordAsync(actor, output);
            var duplicate = await RecordAsync(actor, output);
            var conflict = await RecordAsync(
                actor,
                Stream(key, TerminalShadowEventKind.OutputObserved, TerminalShadowStreamType.StandardError, 2, 8));
            var stale = await RecordAsync(
                actor,
                Stream(key, TerminalShadowEventKind.OutputObserved, TerminalShadowStreamType.StandardOutput, 1, 8));
            var input = await RecordAsync(
                actor,
                Stream(key, TerminalShadowEventKind.InputObserved, TerminalShadowStreamType.StandardInput, 1, 2));
            var resize = await RecordAsync(actor, Resize(key, 1, 100, 30));
            var gap = await RecordAsync(
                actor,
                Stream(key, TerminalShadowEventKind.OutputObserved, TerminalShadowStreamType.StandardOutput, 4, 9));
            var state = await actor.Ask<TerminalShadowState>(new GetTerminalShadowState(key));

            accepted.Disposition.Should().Be(TerminalShadowMessageDisposition.Accepted);
            duplicate.Disposition.Should().Be(TerminalShadowMessageDisposition.Duplicate);
            conflict.Disposition.Should().Be(TerminalShadowMessageDisposition.SequenceConflict);
            stale.Disposition.Should().Be(TerminalShadowMessageDisposition.StaleSequence);
            input.Disposition.Should().Be(TerminalShadowMessageDisposition.Accepted);
            resize.Disposition.Should().Be(TerminalShadowMessageDisposition.Accepted);
            gap.Disposition.Should().Be(TerminalShadowMessageDisposition.Accepted);
            state.MaximumObservedSequence.Should().Be(4);
            state.RejectedStaleEvents.Should().Be(1);
            state.SequenceConflicts.Should().Be(1);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task TenantClientSessionAndTransportKeys_AreIsolated()
    {
        var system = ActorSystem.Create($"terminal-shadow-isolation-{Guid.NewGuid():N}");
        try
        {
            var actor = system.ActorOf(ClientTerminalRouterActor.Props());
            var sessions = new[]
            {
                Session(7, "client-a", "shared-session", TerminalTransportKind.ApiWebSocket),
                Session(8, "client-a", "shared-session", TerminalTransportKind.ApiWebSocket),
                Session(7, "client-b", "shared-session", TerminalTransportKind.ApiWebSocket),
                Session(7, "client-a", "shared-session", TerminalTransportKind.AkkaGateway),
                Session(7, "client-a", "other-session", TerminalTransportKind.ApiWebSocket)
            };

            foreach (var session in sessions)
            {
                await OpenAsync(actor, session);
                await RecordAsync(
                    actor,
                    Stream(session, TerminalShadowEventKind.InputObserved, TerminalShadowStreamType.StandardInput, 1, session.TenantId));
            }

            foreach (var session in sessions)
            {
                var state = await actor.Ask<TerminalShadowState>(new GetTerminalShadowState(session));
                state.Session.Should().Be(session);
                state.InputBytes.Should().Be((ulong)session.TenantId);
                state.Sequences.Should().ContainSingle(sequence =>
                    sequence.Stream == TerminalShadowSequenceStream.Input &&
                    sequence.LastAcceptedSequence == 1);
            }

            var route = await actor.Ask<TerminalShadowRouteStatus>(new ProbeTerminalShadowRoute());
            route.ActiveTerminalSessions.Should().Be(sessions.Length);
            route.SessionActorCount.Should().Be(sessions.Length);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task RetiredTransport_IsRejectedWithoutCreatingAnUnresponsiveChild()
    {
        var system = ActorSystem.Create($"terminal-shadow-retired-transport-{Guid.NewGuid():N}");
        try
        {
            var actor = system.ActorOf(ClientTerminalRouterActor.Props());
            var retiredSession = Session(
                7,
                "client-a",
                "legacy-session",
                TerminalTransportKind.Spacetime);

            var result = await actor.Ask<TerminalShadowMessageResult>(
                new RecordTerminalShadowEvent(
                    Lifecycle(retiredSession, TerminalShadowEventKind.SessionRequested)));
            var route = await actor.Ask<TerminalShadowRouteStatus>(new ProbeTerminalShadowRoute());

            result.Disposition.Should().Be(TerminalShadowMessageDisposition.InvalidObservation);
            result.Session.Should().Be(retiredSession);
            route.SessionActorCount.Should().Be(0);
            route.InvalidObservations.Should().Be(1);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task RetentionBounds_EvictOldestMetadataAndNeverRetainOversizeFrames()
    {
        var system = ActorSystem.Create($"terminal-shadow-bounds-{Guid.NewGuid():N}");
        try
        {
            var key = Session(7, "client-a", "session-a");
            var retention = new TerminalShadowRetentionPolicy(
                MaxRetainedMetadata: 3,
                MaxRetainedByteCount: 10,
                MaxObservedFrameBytes: 8);
            var actor = system.ActorOf(TerminalSessionActor.Props(key, retention));
            await RecordAsync(actor, Lifecycle(key, TerminalShadowEventKind.SessionRequested));
            await RecordAsync(actor, Lifecycle(key, TerminalShadowEventKind.SessionOpened));
            await RecordAsync(actor, Stream(key, TerminalShadowEventKind.InputObserved, TerminalShadowStreamType.StandardInput, 1, 4));
            await RecordAsync(actor, Stream(key, TerminalShadowEventKind.OutputObserved, TerminalShadowStreamType.StandardOutput, 1, 6));
            var oversize = await RecordAsync(
                actor,
                Stream(key, TerminalShadowEventKind.OutputObserved, TerminalShadowStreamType.StandardOutput, 2, 9));
            var state = await actor.Ask<TerminalShadowState>(new GetTerminalShadowState(key));

            oversize.Disposition.Should().Be(TerminalShadowMessageDisposition.Accepted);
            state.RecentMetadata.Should().HaveCountLessThanOrEqualTo(3);
            state.RetainedByteCount.Should().BeLessThanOrEqualTo(10);
            state.RecentMetadata.Should().NotContain(entry => entry.Sequence == 2);
            state.DroppedMetadataEvents.Should().BeGreaterThan(0);
            state.OversizeFrames.Should().Be(1);
            state.MaximumObservedSequence.Should().Be(2);
            state.CoalescedMetadataEvents.Should().Be(0);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task InvalidOrAuthoritativeObservations_DoNotMutateState()
    {
        var system = ActorSystem.Create($"terminal-shadow-invalid-{Guid.NewGuid():N}");
        try
        {
            var key = Session(7, "client-a", "session-a");
            var actor = system.ActorOf(TerminalSessionActor.Props(key));
            var authoritative = Lifecycle(key, TerminalShadowEventKind.SessionRequested) with
            {
                IsAuthoritative = true
            };
            var zeroSequence = Stream(
                key,
                TerminalShadowEventKind.InputObserved,
                TerminalShadowStreamType.StandardInput,
                0,
                1);

            var authoritativeResult = await RecordAsync(actor, authoritative);
            var zeroSequenceResult = await RecordAsync(actor, zeroSequence);
            var state = await actor.Ask<TerminalShadowState>(new GetTerminalShadowState(key));

            authoritativeResult.Disposition.Should().Be(TerminalShadowMessageDisposition.InvalidObservation);
            zeroSequenceResult.Disposition.Should().Be(TerminalShadowMessageDisposition.InvalidObservation);
            state.Status.Should().BeNull();
            state.RecentMetadata.Should().BeEmpty();
            state.InvalidObservations.Should().Be(2);
            state.IsAuthoritative.Should().BeFalse();
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task OpenAsync(IActorRef actor, TerminalShadowSessionKey session)
    {
        await RecordAsync(actor, Lifecycle(session, TerminalShadowEventKind.SessionRequested));
        await RecordAsync(actor, Lifecycle(session, TerminalShadowEventKind.SessionOpened));
    }

    private static async Task<TerminalShadowMessageResult> RecordAsync(
        IActorRef actor,
        TerminalShadowEvent observation) =>
        await actor.Ask<TerminalShadowMessageResult>(new RecordTerminalShadowEvent(observation));

    private static TerminalShadowSessionKey Session(
        int tenantId,
        string clientId,
        string sessionId,
        TerminalTransportKind transport = TerminalTransportKind.ApiWebSocket) =>
        new(tenantId, clientId, sessionId, transport);

    private static TerminalShadowEvent Lifecycle(
        TerminalShadowSessionKey session,
        TerminalShadowEventKind kind,
        TerminalShadowCloseKind closeKind = TerminalShadowCloseKind.None) =>
        new(
            session,
            kind,
            TerminalShadowStreamType.Lifecycle,
            DateTimeOffset.UtcNow,
            CloseKind: closeKind);

    private static TerminalShadowEvent Stream(
        TerminalShadowSessionKey session,
        TerminalShadowEventKind kind,
        TerminalShadowStreamType stream,
        ulong sequence,
        int payloadLength) =>
        new(
            session,
            kind,
            stream,
            DateTimeOffset.UtcNow,
            sequence,
            payloadLength);

    private static TerminalShadowEvent Resize(
        TerminalShadowSessionKey session,
        ulong sequence,
        int columns,
        int rows) =>
        new(
            session,
            TerminalShadowEventKind.ResizeObserved,
            TerminalShadowStreamType.Resize,
            DateTimeOffset.UtcNow,
            sequence,
            PayloadLength: 0,
            Columns: columns,
            Rows: rows);
}
