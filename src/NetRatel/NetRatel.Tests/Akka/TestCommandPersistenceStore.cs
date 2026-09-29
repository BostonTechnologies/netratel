using NetRatel.Application.Commands;

namespace NetRatel.Tests.Akka;

/// <summary>Small in-memory durable-store substitute for actor behavior tests.</summary>
internal sealed class TestCommandPersistenceStore : ICommandPersistenceStore
{
    private readonly object _gate = new();
    private readonly List<CommandLifecycleEvent> _events = [];
    private ulong _replayCount;
    private ulong _recoverySuccessCount;

    public Exception? ReplayFailure { get; init; }

    public Task<CommandPersistenceWriteResult> RecordAsync(
        CommandLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var existing = _events.FirstOrDefault(item =>
                item.Command == lifecycleEvent.Command &&
                item.Version == lifecycleEvent.Version &&
                item.Sequence == lifecycleEvent.Sequence);
            if (existing is not null)
            {
                return Task.FromResult(new CommandPersistenceWriteResult(
                    lifecycleEvent.Command,
                    CommandPersistenceWriteDisposition.Duplicate));
            }

            _events.Add(lifecycleEvent);
            return Task.FromResult(new CommandPersistenceWriteResult(
                lifecycleEvent.Command,
                CommandPersistenceWriteDisposition.Stored));
        }
    }

    public Task<IReadOnlyList<CommandLifecycleEvent>> ReplayAsync(
        CommandKey command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReplayFailure is { } replayFailure)
        {
            return Task.FromException<IReadOnlyList<CommandLifecycleEvent>>(replayFailure);
        }

        lock (_gate)
        {
            _replayCount++;
            IReadOnlyList<CommandLifecycleEvent> result = _events
                .Where(item => item.Command == command)
                .OrderBy(item => item.Sequence)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<CommandPersistenceReplayPage> ReplayBoundedAsync(
        CommandKey command,
        int maximumEvents,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        }

        lock (_gate)
        {
            var events = _events.Where(item => item.Command == command)
                .OrderBy(item => item.Sequence)
                .Take(maximumEvents + 1)
                .ToArray();
            return Task.FromResult(new CommandPersistenceReplayPage(
                events.Take(maximumEvents).ToArray(),
                events.Length <= maximumEvents));
        }
    }

    public Task<IReadOnlyList<CommandOutboxIntent>> ReadPendingOutboxAsync(
        int maxCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CommandOutboxIntent>>([]);
    }

    public Task<CommandPersistenceDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(new CommandPersistenceDiagnostics(
                InboxDepth: _events.Count,
                OutboxDepth: 0,
                ReplayCount: _replayCount,
                DuplicateDetectionCount: 0,
                RecoverySuccessCount: _recoverySuccessCount,
                LastReplayAtUtc: null,
                Mode: "test",
                Authority: "test"));
        }
    }

    public void RecordRecoverySucceeded()
    {
        lock (_gate)
        {
            _recoverySuccessCount++;
        }
    }
}
