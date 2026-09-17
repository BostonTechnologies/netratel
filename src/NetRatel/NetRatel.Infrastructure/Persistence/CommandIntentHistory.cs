using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Commands;

namespace NetRatel.Infrastructure.Persistence;

internal sealed class CommandIntentHistory(OrchestratorDbContext db)
{
    private readonly OrchestratorDbContext _db = db;

    internal async Task AppendAsync(
        CommandLifecycleEvent lifecycleEvent,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        var previous = await _db.CommandIntentEvents
            .AsNoTracking()
            .Where(item => item.TenantId == lifecycleEvent.Client.TenantId &&
                           item.CommandId == lifecycleEvent.CommandId)
            .OrderByDescending(item => item.Version)
            .ThenByDescending(item => item.Sequence)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        ValidateNext(previous, lifecycleEvent);
        _db.CommandIntentEvents.Add(new CommandIntentEventRecord
        {
            Id = Guid.NewGuid(),
            TenantId = lifecycleEvent.Client.TenantId,
            ClientId = lifecycleEvent.Client.AgentId,
            CommandId = lifecycleEvent.CommandId,
            CorrelationId = lifecycleEvent.CorrelationId,
            RequestTimestamp = lifecycleEvent.RequestTimestamp,
            StatusTimestamp = lifecycleEvent.StatusTimestamp,
            Version = lifecycleEvent.Version,
            Sequence = lifecycleEvent.Sequence,
            Status = lifecycleEvent.Status,
            Source = lifecycleEvent.Source,
            IsAuthoritative = lifecycleEvent.IsAuthoritative,
            RecordedAtUtc = recordedAtUtc
        });
    }

    internal async Task<IReadOnlyList<CommandLifecycleEvent>> ReplayAsync(
        CommandKey command,
        CancellationToken cancellationToken)
    {
        var records = await _db.CommandIntentEvents
            .AsNoTracking()
            .Where(item => item.TenantId == command.TenantId && item.CommandId == command.CommandId)
            .OrderBy(item => item.Version)
            .ThenBy(item => item.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return records.Select(ToLifecycleEvent).ToArray();
    }

    internal async Task<CommandPersistenceReplayPage> ReplayBoundedAsync(
        CommandKey command, int maximumEvents, CancellationToken cancellationToken)
    {
        var records = await _db.CommandIntentEvents.AsNoTracking()
            .Where(item => item.TenantId == command.TenantId && item.CommandId == command.CommandId)
            .OrderBy(item => item.Version).ThenBy(item => item.Sequence)
            .Take(maximumEvents + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (records.Any(record => record.Version < 0 || record.Version > ulong.MaxValue ||
            decimal.Truncate(record.Version) != record.Version || record.Sequence < 0 ||
            record.Sequence > ulong.MaxValue || decimal.Truncate(record.Sequence) != record.Sequence))
            return new([], false);
        return new(records.Take(maximumEvents).Select(ToLifecycleEvent).ToArray(), records.Count <= maximumEvents);
    }

    private static void ValidateNext(
        CommandIntentEventRecord? previous,
        CommandLifecycleEvent next)
    {
        if (previous is not null &&
            (previous.ClientId != next.Client.AgentId ||
             !string.Equals(previous.CorrelationId, next.CorrelationId, StringComparison.Ordinal) ||
             !HaveSamePersistedTimestamp(previous.RequestTimestamp, next.RequestTimestamp)))
        {
            throw new InvalidOperationException("Command intent identity cannot change during a lifecycle.");
        }

        if (previous is not null &&
            (next.Version <= previous.Version || next.Sequence <= previous.Sequence))
        {
            throw new InvalidOperationException("Command intent version and sequence must advance monotonically.");
        }

        if (!CommandLifecycleRules.IsValidTransition(previous?.Status, next.Status))
        {
            throw new InvalidOperationException(
                $"Cannot persist command transition '{previous?.Status.ToString() ?? "None"}' to '{next.Status}'.");
        }
    }

    private static CommandLifecycleEvent ToLifecycleEvent(CommandIntentEventRecord record) =>
        new(
            new(record.TenantId, record.ClientId),
            record.CommandId,
            record.CorrelationId,
            record.RequestTimestamp,
            record.StatusTimestamp,
            checked((ulong)record.Version),
            checked((ulong)record.Sequence),
            record.Status,
            record.Source,
            record.IsAuthoritative);

    private static bool HaveSamePersistedTimestamp(DateTimeOffset left, DateTimeOffset right) =>
        left.UtcDateTime.Ticks / TimeSpan.TicksPerMicrosecond ==
        right.UtcDateTime.Ticks / TimeSpan.TicksPerMicrosecond;
}
