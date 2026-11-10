using AwesomeAssertions;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class HangfireJobDispatcherTests
{
    [Fact]
    public async Task RunScheduledJobAsync_UsesAkkaAuthorityWithScheduleIdentity()
    {
        var authority = new RecordingAuthority();
        var dispatcher = new HangfireJobDispatcher(authority);
        using (JobScheduleOccurrenceContext.Enter("synthetic-background-job-42"))
            await dispatcher.RunScheduledJobAsync(42);

        authority.JobId.Should().Be(42);
        authority.Request.Should().Be(new RunJobRequest("hangfire:schedule", null, null));
        authority.OccurrenceId.Should().Be("synthetic-background-job-42");
        JobScheduleOccurrenceContext.Current.Should().BeNull();
    }

    private sealed class RecordingAuthority : IAkkaJobAuthorityService
    {
        public ulong? JobId { get; private set; }
        public RunJobRequest? Request { get; private set; }
        public string? OccurrenceId { get; private set; }
        public Task<JobRunInfo> StartScheduledAsync(ulong jobId, string occurrenceId, CancellationToken cancellationToken)
        {
            OccurrenceId = occurrenceId;
            return StartAsync(jobId, new("hangfire:schedule", null, null), cancellationToken);
        }

        public Task<JobRunInfo> StartAsync(ulong jobId, RunJobRequest request, CancellationToken cancellationToken)
        {
            JobId = jobId;
            Request = request;
            return Task.FromResult(new JobRunInfo(
                1, jobId, null, string.Empty, request.StartedBy, JobRunState.Pending, 0,
                DateTimeOffset.UtcNow, null, null, null, null, null));
        }

        public Task RecordLifecycleAsync(ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> CancelAsync(ulong jobRunId, string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
