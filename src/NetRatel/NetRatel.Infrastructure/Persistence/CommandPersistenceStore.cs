using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Commands;
using Npgsql;

namespace NetRatel.Infrastructure.Persistence;

public sealed class CommandPersistenceStore(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : ICommandPersistenceStore
{
    private const string InboxIdempotencyConstraint = "UX_CommandInbox_Idempotency";
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private long _duplicateDetectionCount;
    private long _replayCount;
    private long _recoverySuccessCount;
    private long _lastReplayUtcTicks;

    public async Task<CommandPersistenceWriteResult> RecordAsync(
        CommandLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken)
    {
        Validate(lifecycleEvent);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var inbox = scope.ServiceProvider.GetRequiredService<CommandInbox>();
        var outbox = scope.ServiceProvider.GetRequiredService<CommandOutbox>();
        var history = scope.ServiceProvider.GetRequiredService<CommandIntentHistory>();
        var now = _timeProvider.GetUtcNow();

        var existingReceipt = await inbox.FindAsync(lifecycleEvent, cancellationToken)
            .ConfigureAwait(false);
        if (existingReceipt is not null)
        {
            CommandInbox.RecordDuplicate(existingReceipt, now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            IncrementSaturating(ref _duplicateDetectionCount);
            return new(lifecycleEvent.Command, CommandPersistenceWriteDisposition.Duplicate);
        }

        inbox.Receive(lifecycleEvent, now);
        await history.AppendAsync(lifecycleEvent, now, cancellationToken).ConfigureAwait(false);
        await outbox.TrackAsync(lifecycleEvent, now, cancellationToken).ConfigureAwait(false);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new(lifecycleEvent.Command, CommandPersistenceWriteDisposition.Stored);
        }
        catch (DbUpdateException exception) when (IsConcurrentInboxDuplicate(exception))
        {
            IncrementSaturating(ref _duplicateDetectionCount);
            return new(lifecycleEvent.Command, CommandPersistenceWriteDisposition.Duplicate);
        }
    }

    public async Task<IReadOnlyList<CommandLifecycleEvent>> ReplayAsync(
        CommandKey command,
        CancellationToken cancellationToken)
    {
        if (!command.IsValid)
        {
            throw new ArgumentException("A valid command key is required.", nameof(command));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var history = scope.ServiceProvider.GetRequiredService<CommandIntentHistory>();
        var replay = await history.ReplayAsync(command, cancellationToken).ConfigureAwait(false);
        IncrementSaturating(ref _replayCount);
        Interlocked.Exchange(ref _lastReplayUtcTicks, _timeProvider.GetUtcNow().UtcTicks);
        return replay;
    }

    public async Task<CommandPersistenceReplayPage> ReplayBoundedAsync(
        CommandKey command, int maximumEvents, CancellationToken cancellationToken)
    {
        if (!command.IsValid)
            throw new ArgumentException("A valid command key is required.", nameof(command));
        if (maximumEvents is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(maximumEvents), "Bounded command replay accepts between 1 and 64 events.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var history = scope.ServiceProvider.GetRequiredService<CommandIntentHistory>();
        return await history.ReplayBoundedAsync(command, maximumEvents, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CommandOutboxIntent>> ReadPendingOutboxAsync(
        int maxCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCount, 1_000);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<CommandOutbox>();
        return await outbox.ReadPendingAsync(maxCount, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandPersistenceDiagnostics> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<CommandInbox>();
        var outbox = scope.ServiceProvider.GetRequiredService<CommandOutbox>();
        var inboxDepth = await inbox.CountAsync(cancellationToken).ConfigureAwait(false);
        var outboxDepth = await outbox.CountPendingAsync(cancellationToken).ConfigureAwait(false);
        var lastReplayTicks = Interlocked.Read(ref _lastReplayUtcTicks);

        return new(
            inboxDepth,
            outboxDepth,
            ReadCounter(ref _replayCount),
            ReadCounter(ref _duplicateDetectionCount),
            ReadCounter(ref _recoverySuccessCount),
            lastReplayTicks == 0 ? null : new DateTimeOffset(lastReplayTicks, TimeSpan.Zero),
            "shadow-only",
            "unavailable");
    }

    public void RecordRecoverySucceeded() =>
        IncrementSaturating(ref _recoverySuccessCount);

    private static void Validate(CommandLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        if (!lifecycleEvent.Command.IsValid || lifecycleEvent.Client.AgentId == Guid.Empty ||
            string.IsNullOrWhiteSpace(lifecycleEvent.CorrelationId))
        {
            throw new ArgumentException("A complete command lifecycle identity is required.", nameof(lifecycleEvent));
        }

    }

    private static bool IsConcurrentInboxDuplicate(DbUpdateException exception) =>
        exception.GetBaseException() is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: InboxIdempotencyConstraint
        };

    private static ulong ReadCounter(ref long counter) =>
        checked((ulong)Math.Max(0, Interlocked.Read(ref counter)));

    private static void IncrementSaturating(ref long counter)
    {
        while (true)
        {
            var current = Interlocked.Read(ref counter);
            if (current == long.MaxValue)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref counter, current + 1, current) == current)
            {
                return;
            }
        }
    }
}
