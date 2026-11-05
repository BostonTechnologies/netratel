using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class JobRunOwnedBoundaryPostgresTests
{
    [Fact]
    public async Task A_native_queued_rejection_holds_the_tenant_fence_before_terminal_capture_and_rolls_back_without_transport()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct, retainDefinitionSnapshot: true);
        var sawEarlyTenantFence = false;
        var sawTerminalFactInOwnerTransaction = false;
        h.Gate.ObserveSaved = db =>
        {
            if (db.OutboxMessages.Local.Any(row => row.Type == FlowExecutionEventLimits.OutboxType && row.Source == "job-terminal-ready"))
            {
                Assert.NotNull(db.Database.CurrentTransaction);
                sawTerminalFactInOwnerTransaction = true;
            }
        };
        h.Primary.OwnerDecoration = owner => new RejectQueuedAcknowledgement(owner, async token =>
        {
            await using var contender = h.Services.CreateAsyncScope();
            var db = contender.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var acquired = await db.Database.SqlQuery<bool>(
                $"SELECT pg_try_advisory_xact_lock({733450001}, {7}) AS \"Value\"").SingleAsync(token);
            acquired.Should().BeFalse("native admission must hold the tenant fence before a queued rejection can append its terminal fact");
            sawEarlyTenantFence = true;
        });
        var rejected = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => h.StartAsync(ct));
        rejected.Message.Should().Contain("lifecycle transition was rejected");
        sawEarlyTenantFence.Should().BeTrue();
        sawTerminalFactInOwnerTransaction.Should().BeTrue();
        h.Gateway.Dispatches.Should().Be(0);
        await using (var verify = h.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            Assert.Empty(await db.JobRuns.ToArrayAsync(ct));
            Assert.Empty(await db.JobTaskActivities.ToArrayAsync(ct));
            Assert.Empty(await db.OutboxMessages.Where(row => row.Type == FlowExecutionEventLimits.OutboxType).ToArrayAsync(ct));
        }
        h.Primary.OwnerDecoration = null;
        h.Gate.ObserveSaved = null;
        var accepted = await h.StartAsync(ct);
        accepted.Status.Should().Be(JobRunState.Running);
        h.Gateway.Dispatches.Should().Be(1);
    }

    // Preserve the real actor's state/persistence transaction, then simulate a rejected acknowledgement.
    // The native failure path must stage a terminal fact while its admitted definition snapshot is locked.
    private sealed class RejectQueuedAcknowledgement(IJobRunOwner inner, Func<CancellationToken, Task> observe) : IJobRunOwner
    {
        private bool rejected;
        public Task ReloadAsync(IJobObservationStore persistence, CancellationToken ct) => inner.ReloadAsync(persistence, ct);
        public async Task<JobMessageResult> RecordAsync(IJobObservation observation, IJobObservationStore persistence, CancellationToken ct)
        {
            var result = await inner.RecordAsync(observation, persistence, ct);
            if (rejected || observation is not JobRunObservation { Status: JobRunState.Pending }) return result;
            rejected = true;
            result.Disposition.Should().Be(JobMessageDisposition.Accepted);
            await observe(ct);
            return result with { Disposition = JobMessageDisposition.InvalidTransition };
        }
    }
}
