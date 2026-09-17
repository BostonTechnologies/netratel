using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Commands;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Stores command intent and observed delivery state for replay. It records the
/// authority selected by the dispatcher but never dispatches commands itself.
/// </summary>
public sealed class CommandOutbox(OrchestratorDbContext db)
{
    private readonly OrchestratorDbContext _db = db;

    internal async Task TrackAsync(
        CommandLifecycleEvent lifecycleEvent,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        var record = await _db.CommandOutbox
            .SingleOrDefaultAsync(
                item => item.TenantId == lifecycleEvent.Client.TenantId &&
                        item.CommandId == lifecycleEvent.CommandId,
                cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            if (lifecycleEvent.Status != CommandLifecycleStatus.Created)
            {
                throw new InvalidOperationException("A command outbox intent must begin with Created.");
            }

            record = new CommandOutboxRecord
            {
                Id = Guid.NewGuid(),
                TenantId = lifecycleEvent.Client.TenantId,
                ClientId = lifecycleEvent.Client.AgentId,
                CommandId = lifecycleEvent.CommandId,
                CorrelationId = lifecycleEvent.CorrelationId,
                RequestTimestamp = lifecycleEvent.RequestTimestamp,
                CreatedAtUtc = recordedAtUtc,
                Mode = lifecycleEvent.IsAuthoritative ? "authority" : "shadow-only",
                IsAuthoritative = lifecycleEvent.IsAuthoritative
            };
            _db.CommandOutbox.Add(record);
        }

        if (lifecycleEvent.Status == CommandLifecycleStatus.Dispatched &&
            record.ObservedDispatchCount < int.MaxValue)
        {
            record.ObservedDispatchCount++;
        }

        record.CurrentStatus = lifecycleEvent.Status;
        record.LastAcceptedVersion = lifecycleEvent.Version;
        record.LastAcceptedSequence = lifecycleEvent.Sequence;
        record.LastObservedAtUtc = lifecycleEvent.StatusTimestamp;
        record.TerminalAtUtc = CommandLifecycleRules.IsTerminal(lifecycleEvent.Status)
            ? lifecycleEvent.StatusTimestamp
            : null;
    }

    internal async Task<IReadOnlyList<CommandOutboxIntent>> ReadPendingAsync(
        int maxCount,
        CancellationToken cancellationToken)
    {
        var records = await _db.CommandOutbox
            .AsNoTracking()
            .Where(item => item.TerminalAtUtc == null)
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.TenantId)
            .ThenBy(item => item.CommandId)
            .Take(maxCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return records.Select(ToIntent).ToArray();
    }

    internal Task<long> CountPendingAsync(CancellationToken cancellationToken) =>
        _db.CommandOutbox.LongCountAsync(item => item.TerminalAtUtc == null, cancellationToken);

    private static CommandOutboxIntent ToIntent(CommandOutboxRecord record) =>
        new(
            new(record.TenantId, record.CommandId),
            record.ClientId,
            record.CorrelationId,
            record.RequestTimestamp,
            record.LastObservedAtUtc,
            record.CurrentStatus,
            checked((ulong)record.LastAcceptedVersion),
            checked((ulong)record.LastAcceptedSequence),
            record.ObservedDispatchCount,
            record.Mode,
            record.IsAuthoritative);
}
