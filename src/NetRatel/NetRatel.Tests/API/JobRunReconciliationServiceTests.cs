using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class JobRunReconciliationServiceTests
{
    [Fact]
    public async Task ReconcileAsync_MarksStaleRunningRunTerminal()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new FakeJobRunService(now - TimeSpan.FromMinutes(10));
        var service = new JobRunReconciliationService(
            store,
            NullLogger<JobRunReconciliationService>.Instance);

        var count = await service.ReconcileAsync(now, TimeSpan.FromMinutes(5));

        count.Should().Be(1);
        store.Run!.Status.Should().Be(JobRunState.TimedOut);
        store.Run.Error.Should().Contain("timed out");
        store.Step!.Status.Should().Be(JobStepRunState.Failed);
        store.Step.TaskRequestId.Should().Be("req-1");
        store.Activity!.Status.Should().Be("TimedOut");
    }

    [Fact]
    public async Task ReconcileAsync_CleansNonTerminalActivity_ForAlreadyTerminalRun()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new FakeJobRunService(now - TimeSpan.FromMinutes(10));
        store.SetRunStatus(JobRunState.Failed);
        var service = new JobRunReconciliationService(
            store,
            NullLogger<JobRunReconciliationService>.Instance);

        var count = await service.ReconcileAsync(now, TimeSpan.FromMinutes(5));

        count.Should().Be(1);
        store.Run!.Status.Should().Be(JobRunState.Failed);
        store.Step!.Status.Should().Be(JobStepRunState.Running);
        store.Activity!.Status.Should().Be("TimedOut");
    }

    private sealed class FakeJobRunService : IJobRunService
    {
        public JobRunInfo? Run { get; private set; }
        public JobStepRunInfo? Step { get; private set; }
        public JobTaskActivityInfo? Activity { get; private set; }

        public FakeJobRunService(DateTimeOffset startedAt)
        {
            Run = new JobRunInfo(
                86,
                14,
                2,
                "C200145AF30C99E98DAB6FCF3F15A8DC9A7546F4719754A834013409AA170A58",
                "ui",
                JobRunState.Running,
                1,
                startedAt,
                startedAt,
                null,
                null,
                "{\"Path\":\"c:\\\\\"}",
                null);
            Step = new JobStepRunInfo(99, 86, 32, JobStepRunState.Running, 1, "req-1", null, startedAt, null);
            Activity = new JobTaskActivityInfo(116, "req-1", 86, 32, Run.ClientIdentity, 2, "exec-library-script", "Pending", null, startedAt, null);
        }

        public void SetRunStatus(JobRunState status)
            => Run = Run! with { Status = status };

        public Task<IReadOnlyList<JobRunInfo>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<JobRunInfo>>(Run is null ? [] : [Run]);

        public Task<JobRunInfo?> GetAsync(ulong runId, CancellationToken ct = default)
            => Task.FromResult(Run?.Id == runId ? Run : null);

        public Task<JobRunDetails?> GetDetailsAsync(ulong runId, CancellationToken ct = default)
            => Task.FromResult<JobRunDetails?>(Run?.Id == runId && Step is not null && Activity is not null
                ? new JobRunDetails(Run, [Step], [Activity])
                : null);

        public Task DeleteAsync(ulong runId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<JobTaskActivityInfo?> GetActivityByIdAsync(ulong activityId, CancellationToken ct = default) => Task.FromResult(Activity?.Id == activityId ? Activity : null);
        public Task<JobTaskActivityInfo?> GetActivityByRequestIdAsync(string requestId, CancellationToken ct = default) => Task.FromResult(Activity?.RequestId == requestId ? Activity : null);
        public Task<IReadOnlyList<JobTaskActivityInfo>> ListTaskActivitiesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobTaskActivityInfo>>(Activity is null ? [] : [Activity]);
        public Task<IReadOnlyList<JobTaskLogInfo>> GetLogsByRequestIdAsync(string requestId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobTaskLogInfo>>([]);

        public Task<JobRunInfo> UpsertRunAsync(UpsertJobRunCommand command, CancellationToken ct = default)
        {
            Run = Run! with
            {
                Status = command.Status,
                CompletedAtUtc = command.CompletedAtUtc,
                Error = command.Error
            };
            return Task.FromResult(Run);
        }

        public Task<JobStepRunInfo> UpsertStepRunAsync(UpsertJobStepRunCommand command, CancellationToken ct = default)
        {
            Step = Step! with
            {
                Status = command.Status,
                TaskRequestId = command.TaskRequestId,
                CompletedAtUtc = command.CompletedAtUtc,
                Error = command.Error
            };
            return Task.FromResult(Step);
        }

        public Task<JobTaskActivityInfo> CreateTaskActivityAsync(CreateJobTaskActivityCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobTaskActivityInfo> UpsertTaskActivityAsync(UpsertJobTaskActivityCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobTaskLogInfo> AppendTaskLogAsync(AppendJobTaskLogCommand command, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<JobTaskActivityInfo?> UpdateTaskActivityStatusAsync(UpdateJobTaskActivityStatusCommand command, CancellationToken ct = default)
        {
            Activity = Activity! with
            {
                Status = command.Status,
                Error = command.Error,
                CompletedAtUtc = command.CompletedAtUtc
            };
            return Task.FromResult<JobTaskActivityInfo?>(Activity);
        }
    }
}
