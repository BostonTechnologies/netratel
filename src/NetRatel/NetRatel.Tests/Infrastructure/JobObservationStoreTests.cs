using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Tests.Akka;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class JobObservationStoreTests
{
    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    [Fact]
    public async Task PersistedObservationSchemaAndKindOrdinalsRemainCompatible()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var entity = db.Model.FindEntityType(typeof(JobShadowObservationRecord));

        entity.Should().NotBeNull();
        entity!.GetTableName().Should().Be("JobShadowObservations");
        entity.GetIndexes().Select(index => index.GetDatabaseName())
            .Should().Contain("UX_JobShadowObservation_Source");
        ((short)JobObservationKind.Run).Should().Be(0);
        ((short)JobObservationKind.Step).Should().Be(1);
    }

    [Fact]
    public async Task AppendOnlyHistory_ReplaysInSourceOrderAndCorrelatesCommandIntent()
    {
        await using var provider = CreateProvider();
        var commandId = $"task-{Guid.NewGuid():N}";
        await PersistCompletedCommandAsync(provider, commandId);
        var store = provider.GetRequiredService<IJobObservationStore>();
        var run = JobCoordinatorActorTests.Run(20, 8001, 7, "client-a", JobRunState.Running);
        var step = JobCoordinatorActorTests.Step(
            21,
            8001,
            7,
            "client-a",
            JobStepRunState.Succeeded,
            commandId);

        var first = await store.RecordAsync(run, CancellationToken.None);
        var second = await store.RecordAsync(step, CancellationToken.None);
        var replay = await store.ReplayAsync(8001, CancellationToken.None);

        first.Disposition.Should().Be(JobObservationWriteDisposition.Stored);
        second.Disposition.Should().Be(JobObservationWriteDisposition.Stored);
        second.CommandCorrelationStatus.Should().Be(JobCommandCorrelationStatus.Matched);
        second.CorrelatedCommandStatus.Should().Be(CommandLifecycleStatus.Completed);
        replay.Select(item => item.Observation.SourceEventId).Should().Equal(20, 21);
        replay[1].CommandCorrelationStatus.Should().Be(JobCommandCorrelationStatus.Matched);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var records = await db.JobShadowObservations.AsNoTracking().ToListAsync();
        records.Should().HaveCount(2);
        records.Should().OnlyContain(item => !item.IsAuthoritative);
        records.Should().OnlyContain(item => item.SourceSystem == "akka-job-shadow-event");
        records.OrderBy(item => item.SourceEventId)
            .Select(item => (short)item.Kind)
            .Should().Equal(0, 1);
    }

    [Fact]
    public async Task DuplicateSourceEvent_IsIdempotent()
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IJobObservationStore>();
        var observation = JobCoordinatorActorTests.Run(
            30,
            9001,
            7,
            "client-a",
            JobRunState.Pending);

        var first = await store.RecordAsync(observation, CancellationToken.None);
        var duplicate = await store.RecordAsync(observation, CancellationToken.None);
        var diagnostics = await store.GetDiagnosticsAsync(CancellationToken.None);

        first.Disposition.Should().Be(JobObservationWriteDisposition.Stored);
        duplicate.Disposition.Should().Be(JobObservationWriteDisposition.Duplicate);
        diagnostics.ObservationCount.Should().Be(1);
        diagnostics.DuplicateDetectionCount.Should().Be(1);
    }

    [Fact]
    public async Task MissingCommandCorrelation_IsPersistedAsDiagnosticOnly()
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IJobObservationStore>();
        var observation = JobCoordinatorActorTests.Step(
            40,
            10001,
            7,
            "client-a",
            JobStepRunState.Running,
            "not-observed-by-command-lifecycle");

        var result = await store.RecordAsync(observation, CancellationToken.None);
        var diagnostics = await store.GetDiagnosticsAsync(CancellationToken.None);

        result.Disposition.Should().Be(JobObservationWriteDisposition.Stored);
        result.CommandCorrelationStatus.Should().Be(JobCommandCorrelationStatus.Missing);
        result.CorrelatedCommandStatus.Should().BeNull();
        diagnostics.MissingCommandCorrelationCount.Should().Be(1);
        diagnostics.Authority.Should().Be("akka");
    }

    [Fact]
    public async Task AuthoritativeObservation_IsStoredAndReportedAsAkkaAuthority()
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IJobObservationStore>();
        var now = DateTimeOffset.UtcNow;
        var observation = new JobRunObservation(
            1,
            11001,
            11002,
            7,
            "client-a",
            "akka:job-authority",
            JobRunState.Pending,
            0,
            now,
            null,
            null,
            now,
            "akka-job-authority:11001",
            IsAuthoritative: true);

        var result = await store.RecordAsync(observation, CancellationToken.None);
        var diagnostics = await store.GetDiagnosticsAsync(CancellationToken.None);

        result.Disposition.Should().Be(JobObservationWriteDisposition.Stored);
        diagnostics.Mode.Should().Be("akka");
        diagnostics.Authority.Should().Be("akka");
    }

    private static async Task PersistCompletedCommandAsync(ServiceProvider provider, string commandId)
    {
        var store = provider.GetRequiredService<ICommandPersistenceStore>();
        var client = new ClientKey(7, Guid.NewGuid());
        var requestedAt = DateTimeOffset.UtcNow;
        var statuses = new[]
        {
            CommandLifecycleStatus.Created,
            CommandLifecycleStatus.Dispatched,
            CommandLifecycleStatus.Accepted,
            CommandLifecycleStatus.Started,
            CommandLifecycleStatus.Completed
        };

        for (var index = 0; index < statuses.Length; index++)
        {
            await store.RecordAsync(
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

    private static ServiceProvider CreateProvider()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<OrchestratorDbContext>(options =>
            options.UseInMemoryDatabase(
                databaseName,
                DatabaseRoot));
        services.AddNetRatelCommandPersistence();
        services.AddNetRatelJobObservationPersistence();
        return services.BuildServiceProvider();
    }
}
