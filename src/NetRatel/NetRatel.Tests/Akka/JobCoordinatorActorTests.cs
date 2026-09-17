using Akka.Actor;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Akka.Jobs;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class JobCoordinatorActorTests
{
    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    [Fact]
    public async Task ValidLifecycle_TracksStepsAndCorrelatesCompletedCommandIntent()
    {
        await using var provider = CreateProvider();
        var commandId = $"task-{Guid.NewGuid():N}";
        await PersistCommandLifecycleAsync(provider, commandId, CommandLifecycleStatus.Completed);
        var actorSystem = ActorSystem.Create($"job-shadow-valid-{Guid.NewGuid():N}");

        try
        {
            var actor = actorSystem.ActorOf(JobCoordinatorActor.Props(
                provider.GetRequiredService<IJobShadowPersistenceStore>()));
            var observations = CreateSuccessfulLifecycle(1001, 7, "client-a", commandId);

            foreach (var observation in observations)
            {
                var result = await actor.Ask<JobShadowMessageResult>(
                    new RecordJobShadowObservation(observation));
                result.Disposition.Should().Be(JobShadowMessageDisposition.Accepted);
            }

            var state = await actor.Ask<JobRunShadowState>(new GetJobShadowState(1001));
            var status = await actor.Ask<JobShadowRouteStatus>(new ProbeJobShadowRoute());

            state.Status.Should().Be(JobRunState.Succeeded);
            state.TenantId.Should().Be(7);
            state.ClientIdentity.Should().Be("client-a");
            state.Steps.Should().ContainSingle();
            state.Steps[0].Status.Should().Be(JobStepRunState.Succeeded);
            state.Steps[0].TaskRequestId.Should().Be(commandId);
            state.Steps[0].CommandCorrelationStatus.Should().Be(JobCommandCorrelationStatus.Matched);
            state.Steps[0].CorrelatedCommandStatus.Should().Be(CommandLifecycleStatus.Completed);
            status.ActiveShadowJobs.Should().Be(0);
            status.CompletedShadowJobs.Should().Be(1);
            status.ActiveJobSteps.Should().Be(0);
            status.AcceptedEvents.Should().Be((ulong)observations.Length);
            status.Authority.Should().Be("unavailable");
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task TerminalToRunning_IsRejectedAndCountedAsInvalid()
    {
        await using var provider = CreateProvider();
        var actorSystem = ActorSystem.Create($"job-shadow-invalid-{Guid.NewGuid():N}");

        try
        {
            var actor = actorSystem.ActorOf(JobCoordinatorActor.Props(
                provider.GetRequiredService<IJobShadowPersistenceStore>()));
            await RecordAsync(actor, Run(1, 2001, 7, "client-a", JobRunState.Pending));
            await RecordAsync(actor, Run(2, 2001, 7, "client-a", JobRunState.Running));
            await RecordAsync(actor, Run(3, 2001, 7, "client-a", JobRunState.Succeeded));

            var invalid = await RecordAsync(
                actor,
                Run(4, 2001, 7, "client-a", JobRunState.Running));
            var state = await actor.Ask<JobRunShadowState>(new GetJobShadowState(2001));
            var status = await actor.Ask<JobShadowRouteStatus>(new ProbeJobShadowRoute());

            invalid.Disposition.Should().Be(JobShadowMessageDisposition.InvalidTransition);
            state.Status.Should().Be(JobRunState.Succeeded);
            state.LastAcceptedSourceEventId.Should().Be(3);
            status.InvalidTransitions.Should().Be(1);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task DuplicateAndStaleObservations_DoNotExtendHistory()
    {
        await using var provider = CreateProvider();
        var actorSystem = ActorSystem.Create($"job-shadow-order-{Guid.NewGuid():N}");

        try
        {
            var actor = actorSystem.ActorOf(JobCoordinatorActor.Props(
                provider.GetRequiredService<IJobShadowPersistenceStore>()));
            var pending = Run(10, 3001, 7, "client-a", JobRunState.Pending);
            await RecordAsync(actor, pending);
            var duplicate = await RecordAsync(actor, pending);
            var running = await RecordAsync(
                actor,
                Run(12, 3001, 7, "client-a", JobRunState.Running));
            var stale = await RecordAsync(
                actor,
                Run(11, 3001, 7, "client-a", JobRunState.Running));
            var state = await actor.Ask<JobRunShadowState>(new GetJobShadowState(3001));
            var status = await actor.Ask<JobShadowRouteStatus>(new ProbeJobShadowRoute());

            duplicate.Disposition.Should().Be(JobShadowMessageDisposition.Duplicate);
            running.Disposition.Should().Be(JobShadowMessageDisposition.Accepted);
            stale.Disposition.Should().Be(JobShadowMessageDisposition.StaleEvent);
            state.RecentHistory.Should().HaveCount(2);
            status.DuplicateEvents.Should().Be(1);
            status.StaleEvents.Should().Be(1);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task TenantAndClientIdentity_CannotChangeWithinAJobRun()
    {
        await using var provider = CreateProvider();
        var actorSystem = ActorSystem.Create($"job-shadow-isolation-{Guid.NewGuid():N}");

        try
        {
            var actor = actorSystem.ActorOf(JobCoordinatorActor.Props(
                provider.GetRequiredService<IJobShadowPersistenceStore>()));
            await RecordAsync(actor, Run(1, 4001, 7, "client-a", JobRunState.Pending));

            var tenantMismatch = await RecordAsync(
                actor,
                Run(2, 4001, 8, "client-a", JobRunState.Running));
            var clientMismatch = await RecordAsync(
                actor,
                Run(3, 4001, 7, "client-b", JobRunState.Running));
            var otherRun = await RecordAsync(
                actor,
                Run(4, 4002, 8, "client-b", JobRunState.Running));
            var originalState = await actor.Ask<JobRunShadowState>(new GetJobShadowState(4001));
            var otherState = await actor.Ask<JobRunShadowState>(new GetJobShadowState(4002));

            tenantMismatch.Disposition.Should().Be(JobShadowMessageDisposition.IdentityMismatch);
            clientMismatch.Disposition.Should().Be(JobShadowMessageDisposition.IdentityMismatch);
            otherRun.Disposition.Should().Be(JobShadowMessageDisposition.Accepted);
            originalState.Status.Should().Be(JobRunState.Pending);
            otherState.TenantId.Should().Be(8);
            otherState.ClientIdentity.Should().Be("client-b");
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task MissingCommandIntent_IsAcceptedAsDiagnosticState()
    {
        await using var provider = CreateProvider();
        var actorSystem = ActorSystem.Create($"job-shadow-missing-command-{Guid.NewGuid():N}");

        try
        {
            var actor = actorSystem.ActorOf(JobCoordinatorActor.Props(
                provider.GetRequiredService<IJobShadowPersistenceStore>()));
            await RecordAsync(actor, Run(1, 5001, 7, "client-a", JobRunState.Running));
            var result = await RecordAsync(
                actor,
                Step(2, 5001, 7, "client-a", JobStepRunState.Running, "missing-command"));
            var state = await actor.Ask<JobRunShadowState>(new GetJobShadowState(5001));
            var status = await actor.Ask<JobShadowRouteStatus>(new ProbeJobShadowRoute());

            result.Disposition.Should().Be(JobShadowMessageDisposition.Accepted);
            result.CommandCorrelationStatus.Should().Be(JobCommandCorrelationStatus.Missing);
            state.Steps[0].CommandCorrelationStatus.Should().Be(JobCommandCorrelationStatus.Missing);
            status.MissingCommandCorrelations.Should().Be(1);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task ReplayFailure_ReturnsPersistenceUnavailableWithoutLeavingTheCallerWaiting()
    {
        var actorSystem = ActorSystem.Create($"job-shadow-replay-failure-{Guid.NewGuid():N}");

        try
        {
            var actor = actorSystem.ActorOf(JobCoordinatorActor.Props(new ReplayFailureStore()));

            var result = await actor.Ask<JobShadowMessageResult>(
                new RecordJobShadowObservation(Run(1, 5002, 7, "client-a", JobRunState.Running)),
                TimeSpan.FromSeconds(2));

            result.Disposition.Should().Be(JobShadowMessageDisposition.PersistenceUnavailable);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task SkippedStep_CanLeadToCancelledRunButCannotRestart()
    {
        await using var provider = CreateProvider();
        var actorSystem = ActorSystem.Create($"job-shadow-cancelled-{Guid.NewGuid():N}");

        try
        {
            var actor = actorSystem.ActorOf(JobCoordinatorActor.Props(
                provider.GetRequiredService<IJobShadowPersistenceStore>()));
            await RecordAsync(actor, Run(1, 5501, 7, "client-a", JobRunState.Pending));
            await RecordAsync(
                actor,
                Step(2, 5501, 7, "client-a", JobStepRunState.Pending, null));
            var skipped = await RecordAsync(
                actor,
                Step(3, 5501, 7, "client-a", JobStepRunState.Skipped, null));
            var cancelled = await RecordAsync(
                actor,
                Run(4, 5501, 7, "client-a", JobRunState.Cancelled));
            var invalidRestart = await RecordAsync(
                actor,
                Step(5, 5501, 7, "client-a", JobStepRunState.Running, null));
            var state = await actor.Ask<JobRunShadowState>(new GetJobShadowState(5501));

            skipped.Disposition.Should().Be(JobShadowMessageDisposition.Accepted);
            cancelled.Disposition.Should().Be(JobShadowMessageDisposition.Accepted);
            invalidRestart.Disposition.Should().Be(JobShadowMessageDisposition.InvalidTransition);
            state.Status.Should().Be(JobRunState.Cancelled);
            state.Steps[0].Status.Should().Be(JobStepRunState.Skipped);
        }
        finally
        {
            await actorSystem.Terminate();
        }
    }

    [Fact]
    public async Task RestartedActorSystem_ReconstructsRunAndStepStateFromHistory()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        await using var provider = CreateProvider(databaseName, DatabaseRoot);
        var store = provider.GetRequiredService<IJobShadowPersistenceStore>();
        var lifecycle = CreateSuccessfulLifecycle(6001, 7, "client-a", null);
        var firstSystem = ActorSystem.Create($"job-shadow-first-{Guid.NewGuid():N}");
        var firstActor = firstSystem.ActorOf(JobCoordinatorActor.Props(store));

        foreach (var observation in lifecycle)
        {
            await RecordAsync(firstActor, observation);
        }

        await firstSystem.Terminate();
        var recoveredSystem = ActorSystem.Create($"job-shadow-recovered-{Guid.NewGuid():N}");
        try
        {
            var recoveredActor = recoveredSystem.ActorOf(JobCoordinatorActor.Props(store));
            var state = await recoveredActor.Ask<JobRunShadowState>(new GetJobShadowState(6001));
            var diagnostics = await store.GetDiagnosticsAsync(CancellationToken.None);

            state.Status.Should().Be(JobRunState.Succeeded);
            state.Steps.Should().ContainSingle(step => step.Status == JobStepRunState.Succeeded);
            state.RecentHistory.Should().HaveCount(lifecycle.Length);
            diagnostics.ReplayCount.Should().BeGreaterThanOrEqualTo(2);
            diagnostics.RecoverySuccessCount.Should().BeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await recoveredSystem.Terminate();
        }
    }

    internal static IJobShadowObservation[] CreateSuccessfulLifecycle(
        ulong jobRunId,
        int tenantId,
        string clientIdentity,
        string? taskRequestId)
    {
        var timestamp = DateTimeOffset.UtcNow;
        return
        [
            Run(1, jobRunId, tenantId, clientIdentity, JobRunState.Pending, timestamp),
            Step(2, jobRunId, tenantId, clientIdentity, JobStepRunState.Pending, null, timestamp),
            Run(3, jobRunId, tenantId, clientIdentity, JobRunState.Running, timestamp),
            Step(4, jobRunId, tenantId, clientIdentity, JobStepRunState.Running, taskRequestId, timestamp),
            Step(5, jobRunId, tenantId, clientIdentity, JobStepRunState.Succeeded, taskRequestId, timestamp),
            Run(6, jobRunId, tenantId, clientIdentity, JobRunState.Succeeded, timestamp)
        ];
    }

    internal static JobRunShadowObservation Run(
        long sourceEventId,
        ulong jobRunId,
        int? tenantId,
        string clientIdentity,
        JobRunState status,
        DateTimeOffset? timestamp = null)
    {
        var observedAt = timestamp ?? DateTimeOffset.UtcNow;
        return new(
            sourceEventId,
            jobRunId,
            JobId: 99,
            tenantId,
            clientIdentity,
            StartedBy: "test",
            status,
            CurrentStepOrdinal: status == JobRunState.Pending ? 0 : 1,
            CreatedAtUtc: observedAt,
            StartedAtUtc: status == JobRunState.Pending ? null : observedAt,
            CompletedAtUtc: status is JobRunState.Succeeded or JobRunState.Failed ? observedAt : null,
            Timestamp: observedAt);
    }

    internal static JobStepShadowObservation Step(
        long sourceEventId,
        ulong jobRunId,
        int? tenantId,
        string clientIdentity,
        JobStepRunState status,
        string? taskRequestId,
        DateTimeOffset? timestamp = null)
    {
        var observedAt = timestamp ?? DateTimeOffset.UtcNow;
        return new(
            sourceEventId,
            jobRunId,
            JobId: 99,
            tenantId,
            clientIdentity,
            JobStepRunId: 7001,
            JobStepId: 71,
            status,
            Ordinal: 1,
            taskRequestId,
            StartedAtUtc: status == JobStepRunState.Pending ? null : observedAt,
            CompletedAtUtc: status is JobStepRunState.Succeeded or JobStepRunState.Failed ? observedAt : null,
            Timestamp: observedAt);
    }

    private static async Task<JobShadowMessageResult> RecordAsync(
        IActorRef actor,
        IJobShadowObservation observation) =>
        await actor.Ask<JobShadowMessageResult>(new RecordJobShadowObservation(observation));

    private static async Task PersistCommandLifecycleAsync(
        ServiceProvider provider,
        string commandId,
        CommandLifecycleStatus finalStatus)
    {
        var commandStore = provider.GetRequiredService<ICommandPersistenceStore>();
        var statuses = new[]
        {
            CommandLifecycleStatus.Created,
            CommandLifecycleStatus.Dispatched,
            CommandLifecycleStatus.Accepted,
            CommandLifecycleStatus.Started,
            finalStatus
        };
        var requestedAt = DateTimeOffset.UtcNow;
        var client = new ClientKey(7, Guid.NewGuid());
        for (var index = 0; index < statuses.Length; index++)
        {
            await commandStore.RecordAsync(
                new CommandLifecycleEvent(
                    client,
                    commandId,
                    "command-correlation",
                    requestedAt,
                    requestedAt.AddSeconds(index + 1),
                    checked((ulong)index + 1),
                    checked((ulong)index + 1),
                    statuses[index]),
                CancellationToken.None);
        }
    }

    private static ServiceProvider CreateProvider(
        string? databaseName = null,
        InMemoryDatabaseRoot? databaseRoot = null)
    {
        var sharedDatabaseName = databaseName ?? Guid.NewGuid().ToString("N");
        var sharedDatabaseRoot = databaseRoot ?? DatabaseRoot;
        var services = new ServiceCollection();
        services.AddDbContext<OrchestratorDbContext>(options =>
            options.UseInMemoryDatabase(
                sharedDatabaseName,
                sharedDatabaseRoot));
        services.AddNetRatelCommandPersistence();
        services.AddNetRatelJobShadowPersistence();
        return services.BuildServiceProvider();
    }

    private sealed class ReplayFailureStore : IJobShadowPersistenceStore
    {
        public Task<JobShadowPersistenceWriteResult> RecordAsync(
            IJobShadowObservation observation,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("RecordAsync must not run after replay fails.");

        public Task<IReadOnlyList<PersistedJobShadowObservation>> ReplayAsync(
            ulong jobRunId,
            CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<PersistedJobShadowObservation>>(
                new InvalidOperationException("Simulated replay failure."));

        public Task<JobShadowPersistenceDiagnostics> GetDiagnosticsAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void RecordRecoverySucceeded() =>
            throw new NotSupportedException();
    }
}
