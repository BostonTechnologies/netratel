using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.API.Gateway;
using NetRatel.Application.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class JobRuntimeHealthCheckTests
{
    [Fact]
    public async Task HealthyEmptyLedger_ReportsNormalAkkaRuntimeAndChecksRequiredDependencies()
    {
        var healthCheck = new JobRuntimeHealthCheck(
            new EmptyJobRouter(),
            new EmptyJobObservationStore());

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["observationCount"].Should().Be(0L);
        result.Data["mode"].Should().Be("akka");
        result.Data["jobAuthority"].Should().Be("akka");
        result.Data.Keys.Should().NotContain(key => key.Contains("ingress", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class EmptyJobRouter : IJobRuntimeRouter
    {
        public Task<JobMessageResult> RecordAsync(
            RecordJobObservation message,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobRunView> GetStateAsync(
            ulong jobRunId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobRuntimeStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new JobRuntimeStatus(
                ActiveJobs: 0,
                CompletedJobs: 0,
                FailedJobs: 0,
                ActiveJobSteps: 0,
                AcceptedEvents: 0,
                InvalidTransitions: 0,
                DuplicateEvents: 0,
                StaleEvents: 0,
                MissingCommandCorrelations: 0,
                StartedAtUtc: DateTimeOffset.UtcNow,
                Mode: "akka",
                Authority: "akka"));
    }

    private sealed class EmptyJobObservationStore : IJobObservationStore
    {
        public Task<JobObservationWriteResult> RecordAsync(
            IJobObservation observation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PersistedJobObservation>> ReplayAsync(
            ulong jobRunId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobObservationDiagnostics> GetDiagnosticsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new JobObservationDiagnostics(
                ObservationCount: 0,
                ReplayCount: 0,
                DuplicateDetectionCount: 0,
                MissingCommandCorrelationCount: 0,
                RecoverySuccessCount: 0,
                LastPersistedAtUtc: null,
                LastReplayAtUtc: null,
                Mode: "akka",
                Authority: "akka"));

        public void RecordRecoverySucceeded()
        {
        }
    }
}
