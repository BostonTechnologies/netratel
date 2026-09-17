using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Services.Operations;
using NetRatel.Application.Commands;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorTaskReconciliationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CommandLifecycleStatus.Completed)]
    [InlineData(CommandLifecycleStatus.Failed)]
    [InlineData(CommandLifecycleStatus.Cancelled)]
    public async Task CompleteAuthoritativeHistory_RecoversExactOutcomeWithoutInventingOutput(CommandLifecycleStatus terminal)
    {
        await using var db = CreateDb();
        var task = NewTask(1);
        db.McpOperatorTasks.Add(task);
        await db.SaveChangesAsync();
        var history = History(task, terminal);
        var replay = new ReplayStore(_ => new(history, true));
        var service = Service(db, replay);

        var result = await service.ReconcileAsync(Now);
        var second = await service.ReconcileAsync(Now);

        result.Reconciled.Should().Be(1);
        second.Scanned.Should().Be(0);
        replay.Calls.Should().Be(1);
        var saved = await db.McpOperatorTasks.AsNoTracking().SingleAsync();
        saved.State.Should().Be(terminal.ToString());
        saved.CompletedAtUtc.Should().Be(history[^1].StatusTimestamp);
        saved.ResultSummary.Should().Be(McpOperatorTaskReconciliationService.ResultUnavailable);
        saved.CancellationRequested.Should().BeTrue();
        saved.Subject.Should().Be(task.Subject);
        (await db.McpOperatorTaskAudits.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("agent")]
    [InlineData("command")]
    [InlineData("correlation")]
    [InlineData("shadow")]
    [InlineData("version")]
    [InlineData("sequence")]
    [InlineData("transition")]
    [InlineData("request_time")]
    [InlineData("status_time")]
    [InlineData("old_request")]
    [InlineData("nonterminal")]
    [InlineData("missing")]
    [InlineData("overflow")]
    public async Task IncompleteOrUntrustedHistory_CannotInferCancellation(string invalid)
    {
        await using var db = CreateDb();
        var task = NewTask(1);
        db.McpOperatorTasks.Add(task);
        await db.SaveChangesAsync();
        var history = History(task, CommandLifecycleStatus.Cancelled);
        switch (invalid)
        {
            case "tenant": history[^1] = history[^1] with { Client = new ClientKey(task.TenantId + 1, task.AgentId) }; break;
            case "agent": history[^1] = history[^1] with { Client = new ClientKey(task.TenantId, Guid.NewGuid()) }; break;
            case "command": history[^1] = history[^1] with { CommandId = Guid.NewGuid().ToString("N") }; break;
            case "correlation": history[^1] = history[^1] with { CorrelationId = "other-request" }; break;
            case "shadow": history[2] = history[2] with { IsAuthoritative = false }; break;
            case "version": history[^1] = history[^1] with { Version = 1 }; break;
            case "sequence": history[^1] = history[^1] with { Sequence = 1 }; break;
            case "transition": history[1] = history[1] with { Status = CommandLifecycleStatus.Completed }; break;
            case "request_time": history[^1] = history[^1] with { RequestTimestamp = history[0].RequestTimestamp.AddSeconds(1) }; break;
            case "status_time": history[^1] = history[^1] with { StatusTimestamp = history[0].StatusTimestamp }; break;
            case "old_request": history = history.Select(item => item with { RequestTimestamp = task.CreatedAtUtc.AddSeconds(-1) }).ToArray(); break;
            case "nonterminal": history = history[..^1]; break;
            case "missing": history = []; break;
        }
        var replay = new ReplayStore(_ => new(history, invalid != "overflow"));

        var result = await Service(db, replay).ReconcileAsync(Now);

        result.Reconciled.Should().Be(0);
        var saved = await db.McpOperatorTasks.AsNoTracking().SingleAsync();
        saved.State.Should().Be("CancelRequested");
        saved.CompletedAtUtc.Should().BeNull();
        saved.ResultSummary.Should().BeNull();
    }

    [Fact]
    public async Task CandidateCursor_VisitsLaterTasksAfterAnUnrecoverableFullPage()
    {
        await using var db = CreateDb();
        for (var id = 1; id <= McpOperatorTaskReconciliationService.MaximumCandidates + 1; id++)
            db.McpOperatorTasks.Add(NewTask(id));
        await db.SaveChangesAsync();
        var replay = new ReplayStore(_ => new([], true));
        var service = Service(db, replay);

        var first = await service.ReconcileAsync(Now);
        var second = await service.ReconcileAsync(Now, first.NextAfterTaskId);

        first.Scanned.Should().Be(McpOperatorTaskReconciliationService.MaximumCandidates);
        first.NextAfterTaskId.Should().Be(McpOperatorTaskReconciliationService.MaximumCandidates);
        second.Scanned.Should().Be(1);
        second.NextAfterTaskId.Should().Be(0);
        replay.Calls.Should().Be(McpOperatorTaskReconciliationService.MaximumCandidates + 1);
    }

    [Fact]
    public async Task RecentProjection_IsLeftForTheActiveGateway()
    {
        await using var db = CreateDb();
        var task = NewTask(1);
        task.UpdatedAtUtc = Now;
        db.McpOperatorTasks.Add(task);
        await db.SaveChangesAsync();
        var replay = new ReplayStore(_ => new(History(task, CommandLifecycleStatus.Cancelled), true));

        (await Service(db, replay).ReconcileAsync(Now)).Scanned.Should().Be(0);
        replay.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RecentTerminalEvent_IsLeftForGatewayResultPersistenceEvenWhenProjectionIsOld()
    {
        await using var db = CreateDb();
        var task = NewTask(1);
        db.McpOperatorTasks.Add(task);
        await db.SaveChangesAsync();
        var history = History(task, CommandLifecycleStatus.Completed);
        history[^1] = history[^1] with { StatusTimestamp = Now.AddSeconds(-1) };
        var replay = new ReplayStore(_ => new(history, true));

        var result = await Service(db, replay).ReconcileAsync(Now);

        result.Scanned.Should().Be(1);
        result.Reconciled.Should().Be(0);
        (await db.McpOperatorTasks.AsNoTracking().SingleAsync()).State.Should().Be("CancelRequested");
    }

    [Fact]
    public async Task ConcurrentGatewayTerminalResult_WinsWithoutRecoveryOverwritingItsPayload()
    {
        var database = Guid.NewGuid().ToString("N");
        await using var db = CreateDb(database);
        await using var gatewayDb = CreateDb(database);
        var task = NewTask(1);
        db.McpOperatorTasks.Add(task);
        await db.SaveChangesAsync();
        var replay = new ReplayStore(_ => new(History(task, CommandLifecycleStatus.Cancelled), true))
        {
            BeforeRead = token => new McpOperatorTaskStore(gatewayDb).RecordLifecycleAsync(task.CommandId,
                task.TenantId, task.AgentId, "Completed", "real-final-output", Now.AddSeconds(-1), token)
        };

        var result = await Service(db, replay).ReconcileAsync(Now);

        result.Reconciled.Should().Be(0);
        var saved = await db.McpOperatorTasks.AsNoTracking().SingleAsync();
        saved.State.Should().Be("Completed");
        saved.ResultSummary.Should().Be("real-final-output");
        replay.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CancellationDuringReplay_StopsWithoutChangingTaskState()
    {
        await using var db = CreateDb();
        var task = NewTask(1);
        db.McpOperatorTasks.Add(task);
        await db.SaveChangesAsync();
        using var stop = new CancellationTokenSource();
        var replay = new ReplayStore(_ => new(History(task, CommandLifecycleStatus.Cancelled), true))
        {
            BeforeRead = token =>
            {
                stop.Cancel();
                return Task.FromCanceled(token);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(db, replay).ReconcileAsync(Now, cancellationToken: stop.Token));
        (await db.McpOperatorTasks.AsNoTracking().SingleAsync()).State.Should().Be("CancelRequested");
    }

    private static McpOperatorTaskReconciliationService Service(OrchestratorDbContext db, ICommandPersistenceStore replay) =>
        new(db, replay, new McpOperatorTaskStore(db), NullLogger<McpOperatorTaskReconciliationService>.Instance);

    private static OrchestratorDbContext CreateDb(string? database = null) => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(database ?? Guid.NewGuid().ToString("N")).Options);

    private static McpOperatorTaskRecord NewTask(long taskId) => new()
    {
        Id = Guid.NewGuid(), TaskActivityId = taskId, TenantId = 42, AgentId = Guid.NewGuid(),
        CommandId = Guid.NewGuid().ToString("N"), CorrelationId = "corr-task-recovery", Subject = "test-operator",
        State = "CancelRequested", CancellationRequested = true, CreatedAtUtc = Now.AddMinutes(-2),
        UpdatedAtUtc = Now.AddMinutes(-1), Version = 1
    };

    private static CommandLifecycleEvent[] History(McpOperatorTaskRecord task, CommandLifecycleStatus terminal)
    {
        var requestedAt = task.CreatedAtUtc.AddSeconds(1);
        var statuses = new[] { CommandLifecycleStatus.Created, CommandLifecycleStatus.Dispatched,
            CommandLifecycleStatus.Accepted, CommandLifecycleStatus.Started, terminal };
        return statuses.Select((status, index) => new CommandLifecycleEvent(new ClientKey(task.TenantId, task.AgentId),
            task.CommandId, task.CorrelationId, requestedAt, requestedAt.AddSeconds(index),
            (ulong)index + 1, (ulong)index + 1, status, "akka", true)).ToArray();
    }

    private sealed class ReplayStore(Func<CommandKey, CommandPersistenceReplayPage> result) : ICommandPersistenceStore
    {
        public int Calls { get; private set; }
        public Func<CancellationToken, Task>? BeforeRead { get; init; }
        public async Task<CommandPersistenceReplayPage> ReplayBoundedAsync(CommandKey command, int maximumEvents, CancellationToken cancellationToken)
        {
            maximumEvents.Should().Be(McpOperatorTaskReconciliationService.MaximumHistoryEvents);
            Calls++;
            if (BeforeRead is not null) await BeforeRead(cancellationToken);
            return result(command);
        }
        public Task<CommandPersistenceWriteResult> RecordAsync(CommandLifecycleEvent lifecycleEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CommandLifecycleEvent>> ReplayAsync(CommandKey command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CommandOutboxIntent>> ReadPendingOutboxAsync(int maxCount, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CommandPersistenceDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public void RecordRecoverySucceeded() => throw new NotSupportedException();
    }
}
