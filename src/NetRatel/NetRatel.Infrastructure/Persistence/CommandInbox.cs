using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Commands;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Tracks receipt identity for command observations. It provides idempotency only;
/// it never executes or dispatches a command.
/// </summary>
public sealed class CommandInbox(OrchestratorDbContext db)
{
    private readonly OrchestratorDbContext _db = db;

    internal Task<CommandInboxReceipt?> FindAsync(
        CommandLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken) =>
        _db.CommandInboxReceipts.SingleOrDefaultAsync(
            receipt => receipt.TenantId == lifecycleEvent.Client.TenantId &&
                       receipt.CommandId == lifecycleEvent.CommandId &&
                       receipt.Version == lifecycleEvent.Version &&
                       receipt.Sequence == lifecycleEvent.Sequence,
            cancellationToken);

    internal void Receive(CommandLifecycleEvent lifecycleEvent, DateTimeOffset receivedAtUtc)
    {
        _db.CommandInboxReceipts.Add(new CommandInboxReceipt
        {
            Id = Guid.NewGuid(),
            TenantId = lifecycleEvent.Client.TenantId,
            ClientId = lifecycleEvent.Client.AgentId,
            CommandId = lifecycleEvent.CommandId,
            CorrelationId = lifecycleEvent.CorrelationId,
            Version = lifecycleEvent.Version,
            Sequence = lifecycleEvent.Sequence,
            FirstReceivedAtUtc = receivedAtUtc,
            LastReceivedAtUtc = receivedAtUtc
        });
    }

    internal static void RecordDuplicate(
        CommandInboxReceipt receipt,
        DateTimeOffset receivedAtUtc)
    {
        receipt.LastReceivedAtUtc = receivedAtUtc;
        if (receipt.DuplicateCount < long.MaxValue)
        {
            receipt.DuplicateCount++;
        }
    }

    internal Task<long> CountAsync(CancellationToken cancellationToken) =>
        _db.CommandInboxReceipts.LongCountAsync(cancellationToken);
}
