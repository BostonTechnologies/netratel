using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

/// <summary>
/// Runs recurring jobs through the same PostgreSQL/Akka authority as the
/// interactive job-run endpoints. Hangfire is only the scheduler.
/// </summary>
public sealed class HangfireJobDispatcher(IAkkaJobAuthorityService authority)
{
    public Task RunScheduledJobAsync(ulong jobId, CancellationToken cancellationToken = default)
        => authority.StartAsync(
            jobId,
            new RunJobRequest(
                StartedBy: "hangfire:schedule",
                InputsJson: null,
                ClientIdentityOverride: null),
            cancellationToken);
}
