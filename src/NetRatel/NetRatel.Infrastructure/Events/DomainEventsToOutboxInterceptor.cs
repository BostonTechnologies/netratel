using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Events;

public sealed class DomainEventsToOutboxInterceptor : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        EnqueueOutboxMessages(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        EnqueueOutboxMessages(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void EnqueueOutboxMessages(DbContext? db)
    {
        if (db is null)
            return;

        var aggregates = db.ChangeTracker.Entries<IHasDomainEvents>()
            .Select(x => x.Entity)
            .Where(x => x.DomainEvents.Count > 0)
            .ToArray();

        if (aggregates.Length == 0)
            return;

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                db.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    Id = domainEvent.EventId,
                    OccurredUtc = domainEvent.OccurredUtc,
                    Type = domainEvent.EventType,
                    Source = domainEvent.Source,
                    CorrelationId = domainEvent.CorrelationId,
                    TenantId = domainEvent.TenantId,
                    EntityId = domainEvent.EntityId,
                    Severity = domainEvent.Severity,
                    Message = domainEvent.Message,
                    PayloadJson = JsonSerializer.Serialize(domainEvent.Payload ?? domainEvent, JsonOptions),
                    Status = OutboxStatuses.Pending,
                    Attempts = 0,
                    NextAttemptUtc = DateTimeOffset.UtcNow
                });
            }

            aggregate.ClearDomainEvents();
        }
    }
}
