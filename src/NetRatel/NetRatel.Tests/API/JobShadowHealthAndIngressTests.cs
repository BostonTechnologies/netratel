using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.API.Gateway;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class JobShadowHealthAndIngressTests
{
    [Fact]
    public async Task HealthCheck_ReportsRoutePersistenceAndIngressDiagnostics()
    {
        var router = new RecordingRouter();
        var persistence = new StubPersistenceStore();
        var ingress = new StubIngress();
        var healthCheck = new JobShadowHealthCheck(router, persistence, ingress);

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["activeShadowJobs"].Should().Be(2);
        result.Data["observationCount"].Should().Be(11L);
        result.Data["ingressDropped"].Should().Be(1UL);
        result.Data["jobAuthority"].Should().Be("spacetimedb");
    }

    [Fact]
    public async Task BoundedQueue_ProcessesAcceptedObservationWithoutPollingDelay()
    {
        var router = new RecordingRouter();
        var queue = new JobShadowObservationQueue(router);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            queue.TryEnqueue(CreateRunObservation()).Should().BeTrue();
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
    public async Task FanoutFailure_DoesNotRewriteAcceptedOrPersistenceOutcome()
    {
        var fanout = new ThrowingShadowFanoutSink();
        var queue = new JobShadowObservationQueue(new RecordingRouter(), fanout);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            queue.TryEnqueue(CreateRunObservation()).Should().BeTrue();
            await fanout.Called.Task.WaitAsync(TimeSpan.FromSeconds(3));

            queue.GetStatus().Should().Be(new JobShadowIngressStatus(1, 0, 1, 0, 0));
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    private static JobRunShadowObservation CreateRunObservation()
    {
        var timestamp = DateTimeOffset.UtcNow;
        return new(
            SourceEventId: 1,
            JobRunId: 1,
            JobId: 2,
            TenantId: 3,
            ClientIdentity: "client-a",
            StartedBy: "test",
            Status: JobRunState.Pending,
            CurrentStepOrdinal: 0,
            CreatedAtUtc: timestamp,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            Timestamp: timestamp);
    }

    private sealed class RecordingRouter : IJobShadowRouter
    {
        public TaskCompletionSource Recorded { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<JobShadowMessageResult> RecordAsync(
            RecordJobShadowObservation message,
            CancellationToken cancellationToken)
        {
            Recorded.TrySetResult();
            return Task.FromResult(new JobShadowMessageResult(
                message.Observation.JobRunId,
                JobShadowMessageDisposition.Accepted,
                JobRunState.Pending,
                message.Observation.SourceEventId,
                JobCommandCorrelationStatus.NotProvided,
                null));
        }

        public Task<JobRunShadowState> GetStateAsync(
            ulong jobRunId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobShadowRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new JobShadowRouteStatus(
                ActiveShadowJobs: 2,
                CompletedShadowJobs: 3,
                FailedShadowJobs: 1,
                ActiveJobSteps: 1,
                AcceptedEvents: 10,
                InvalidTransitions: 1,
                DuplicateEvents: 2,
                StaleEvents: 3,
                MissingCommandCorrelations: 4,
                StartedAtUtc: DateTimeOffset.UtcNow,
                Mode: "local-shadow",
                Authority: "spacetimedb"));
    }

    private sealed class StubPersistenceStore : IJobShadowPersistenceStore
    {
        public Task<JobShadowPersistenceWriteResult> RecordAsync(
            IJobShadowObservation observation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PersistedJobShadowObservation>> ReplayAsync(
            ulong jobRunId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobShadowPersistenceDiagnostics> GetDiagnosticsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new JobShadowPersistenceDiagnostics(
                ObservationCount: 11,
                ReplayCount: 2,
                DuplicateDetectionCount: 1,
                MissingCommandCorrelationCount: 4,
                RecoverySuccessCount: 2,
                LastPersistedAtUtc: DateTimeOffset.UtcNow,
                LastReplayAtUtc: DateTimeOffset.UtcNow,
                Mode: "shadow-only",
                Authority: "spacetimedb"));

        public void RecordRecoverySucceeded()
        {
        }
    }

    private sealed class StubIngress : IJobShadowObservationSink
    {
        public bool TryEnqueue(IJobShadowObservation observation) => false;

        public JobShadowIngressStatus GetStatus() =>
            new(Enqueued: 5, Dropped: 1, Accepted: 4, Rejected: 1, PersistenceFailures: 0);
    }
}
