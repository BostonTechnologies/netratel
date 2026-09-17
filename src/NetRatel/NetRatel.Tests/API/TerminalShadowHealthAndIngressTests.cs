using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.API.Gateway;
using NetRatel.API.Services.Terminal;
using NetRatel.Application.Terminals;
using NetRatel.Shared.Contracts.Terminals;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class TerminalShadowHealthAndIngressTests
{
    [Fact]
    public async Task HealthCheck_ReportsBoundedRouteAndIngressDiagnostics()
    {
        var route = CreateRouteStatus();
        var healthCheck = new TerminalShadowHealthCheck(
            new StubRouter(route),
            new StubSink(new TerminalShadowIngressStatus(10, 2, 7, 1)));

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["activeTerminalSessions"].Should().Be(2);
        result.Data["retainedMetadataCount"].Should().Be(12);
        result.Data["ingressDropped"].Should().Be(2UL);
        result.Data["terminalAuthority"].Should().Be("spacetimedb");
        result.Data.Keys.Should().NotContain(key => key.Contains("Id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BoundedQueue_ProcessesMetadataWithoutBlockingTheProducer()
    {
        var router = new RecordingRouter();
        var queue = new TerminalShadowObservationQueue(router);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            queue.TryEnqueue(Requested()).Should().BeTrue();
            await router.Recorded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await queue.StopAsync(CancellationToken.None);

            var status = queue.GetStatus();
            status.Enqueued.Should().Be(1);
            status.Accepted.Should().Be(1);
            status.Dropped.Should().Be(0);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task FanoutFailure_DoesNotRewriteAcceptedRouterOutcome()
    {
        var fanout = new ThrowingShadowFanoutSink();
        var queue = new TerminalShadowObservationQueue(new RecordingRouter(), fanout);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            queue.TryEnqueue(Requested()).Should().BeTrue();
            await fanout.Called.Task.WaitAsync(TimeSpan.FromSeconds(3));

            queue.GetStatus().Should().Be(new TerminalShadowIngressStatus(1, 0, 1, 0));
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void BoundedQueue_DropsShadowMetadataWhenCapacityIsReached()
    {
        var queue = new TerminalShadowObservationQueue(new NeverCalledRouter());
        for (var index = 0; index < 2_048; index++)
        {
            queue.TryEnqueue(Requested($"session-{index}"))
                .Should().BeTrue();
        }

        queue.TryEnqueue(Requested("overflow")).Should().BeFalse();
        queue.GetStatus().Should().Be(new TerminalShadowIngressStatus(
            Enqueued: 2_048,
            Dropped: 1,
            Accepted: 0,
            Rejected: 0));
    }

    private static TerminalShadowRouteStatus CreateRouteStatus() =>
        new(
            ActiveTerminalSessions: 2,
            ClosedTerminalSessions: 3,
            FailedTerminalSessions: 1,
            SessionActorCount: 6,
            AcceptedEvents: 20,
            DuplicateEvents: 1,
            RejectedStaleEvents: 2,
            SequenceConflicts: 1,
            InvalidTransitions: 2,
            InvalidObservations: 1,
            IdentityConflicts: 0,
            DroppedMetadataEvents: 4,
            CoalescedMetadataEvents: 0,
            RetainedMetadataCount: 12,
            InputFrames: 4,
            OutputFrames: 8,
            ResizeEvents: 1,
            InputBytes: 32,
            OutputBytes: 128,
            MaximumObservedSequence: 8,
            LifecycleCounts: new(6, 5, 2, 3, 1),
            StartedAtUtc: DateTimeOffset.UtcNow,
            Mode: "local-shadow",
            Authority: "spacetimedb");

    private static TerminalShadowEvent Requested(string sessionId = "session-a") =>
        new(
            new TerminalShadowSessionKey(7, "client-a", sessionId, TerminalTransportKind.ApiWebSocket),
            TerminalShadowEventKind.SessionRequested,
            TerminalShadowStreamType.Lifecycle,
            DateTimeOffset.UtcNow);

    private sealed class StubSink(TerminalShadowIngressStatus status) : ITerminalShadowObservationSink
    {
        public bool TryEnqueue(TerminalShadowEvent observation) => false;

        public TerminalShadowIngressStatus GetStatus() => status;
    }

    private class StubRouter(TerminalShadowRouteStatus status) : ITerminalShadowRouter
    {
        public virtual Task<TerminalShadowMessageResult> RecordAsync(
            RecordTerminalShadowEvent message,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TerminalShadowState> GetStateAsync(
            TerminalShadowSessionKey session,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TerminalShadowRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(status);
    }

    private sealed class RecordingRouter : StubRouter
    {
        public RecordingRouter() : base(CreateRouteStatus())
        {
        }

        public TaskCompletionSource Recorded { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<TerminalShadowMessageResult> RecordAsync(
            RecordTerminalShadowEvent message,
            CancellationToken cancellationToken)
        {
            Recorded.TrySetResult();
            return Task.FromResult(new TerminalShadowMessageResult(
                message.Event.Session,
                TerminalShadowMessageDisposition.Accepted,
                TerminalShadowSessionStatus.Requested,
                MaximumObservedSequence: 0,
                RetainedMetadataCount: 1,
                RetainedByteCount: 0,
                DroppedMetadataEvents: 0,
                CoalescedMetadataEvents: 0));
        }
    }

    private sealed class NeverCalledRouter : ITerminalShadowRouter
    {
        public Task<TerminalShadowMessageResult> RecordAsync(
            RecordTerminalShadowEvent message,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The queue is deliberately not started in this capacity test.");

        public Task<TerminalShadowState> GetStateAsync(
            TerminalShadowSessionKey session,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TerminalShadowRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
