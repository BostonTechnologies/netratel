namespace NetRatel.Application.Commands;

public enum CommandPersistenceWriteDisposition
{
    Stored = 0,
    Duplicate = 1
}

public sealed record CommandPersistenceWriteResult(
    CommandKey Command,
    CommandPersistenceWriteDisposition Disposition);

/// <summary>A bounded replay; an incomplete page cannot establish a final lifecycle outcome.</summary>
public sealed record CommandPersistenceReplayPage(IReadOnlyList<CommandLifecycleEvent> Events, bool IsComplete);

public sealed record CommandOutboxIntent(
    CommandKey Command,
    Guid ClientId,
    string CorrelationId,
    DateTimeOffset RequestTimestamp,
    DateTimeOffset LastObservedTimestamp,
    CommandLifecycleStatus CurrentStatus,
    ulong LastAcceptedVersion,
    ulong LastAcceptedSequence,
    int ObservedDispatchCount,
    string Mode,
    bool IsAuthoritative);

public sealed record CommandPersistenceDiagnostics(
    long InboxDepth,
    long OutboxDepth,
    ulong ReplayCount,
    ulong DuplicateDetectionCount,
    ulong RecoverySuccessCount,
    DateTimeOffset? LastReplayAtUtc,
    string Mode,
    string Authority);

/// <summary>
/// Stores shadow command observations. Implementations must not dispatch or execute commands.
/// </summary>
public interface ICommandPersistenceStore
{
    Task<CommandPersistenceWriteResult> RecordAsync(
        CommandLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CommandLifecycleEvent>> ReplayAsync(
        CommandKey command,
        CancellationToken cancellationToken);

    /// <summary>Reads at most maximumEvents, reporting whether further history exists.</summary>
    Task<CommandPersistenceReplayPage> ReplayBoundedAsync(
        CommandKey command,
        int maximumEvents,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This command persistence implementation does not support bounded replay.");

    Task<IReadOnlyList<CommandOutboxIntent>> ReadPendingOutboxAsync(
        int maxCount,
        CancellationToken cancellationToken);

    Task<CommandPersistenceDiagnostics> GetDiagnosticsAsync(
        CancellationToken cancellationToken);

    void RecordRecoverySucceeded();
}

public static class CommandLifecycleRules
{
    public static bool IsValidTransition(
        CommandLifecycleStatus? current,
        CommandLifecycleStatus next) =>
        (current, next) switch
        {
            (null, CommandLifecycleStatus.Created) => true,
            (CommandLifecycleStatus.Created, CommandLifecycleStatus.Dispatched) => true,
            (CommandLifecycleStatus.Dispatched, CommandLifecycleStatus.Accepted) => true,
            (CommandLifecycleStatus.Accepted, CommandLifecycleStatus.Started) => true,
            // Admission can be cancelled while queued, or fail to enter the client queue.
            (CommandLifecycleStatus.Accepted, CommandLifecycleStatus.Cancelled) => true,
            (CommandLifecycleStatus.Accepted, CommandLifecycleStatus.Failed) => true,
            (CommandLifecycleStatus.Started, CommandLifecycleStatus.Completed) => true,
            (CommandLifecycleStatus.Started, CommandLifecycleStatus.Failed) => true,
            (CommandLifecycleStatus.Started, CommandLifecycleStatus.Cancelled) => true,
            _ => false
        };

    public static bool IsTerminal(CommandLifecycleStatus status) =>
        status is CommandLifecycleStatus.Completed or
            CommandLifecycleStatus.Failed or
            CommandLifecycleStatus.Cancelled;
}
