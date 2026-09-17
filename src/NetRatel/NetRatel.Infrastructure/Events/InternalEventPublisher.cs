using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Events;
using NetRatel.Application.Notifications;
using NetRatel.Infrastructure.Notifications;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Events;

public sealed class InternalEventPublisher(
    OrchestratorDbContext db,
    INetRatelNotificationEventBus bus,
    NetRatelNotificationDisplaySanitizer displaySanitizer) : IEventPublisher
{
    private const string ConsumerName = "InternalNotificationBus";

    private readonly OrchestratorDbContext _db = db;
    private readonly INetRatelNotificationEventBus _bus = bus;
    private readonly NetRatelNotificationDisplaySanitizer _displaySanitizer = displaySanitizer;

    public async Task PublishAsync(OutboxEnvelope envelope, CancellationToken ct)
    {
        var exists = await _db.OutboxProcessedEvents
            .AnyAsync(x => x.EventId == envelope.Id && x.ConsumerName == ConsumerName, ct);

        if (exists)
            return;

        _db.OutboxProcessedEvents.Add(new OutboxProcessedEvent
        {
            EventId = envelope.Id,
            ConsumerName = ConsumerName,
            ProcessedUtc = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        _bus.Publish(_displaySanitizer.Sanitize(new NetRatelNotificationDto
        {
            Id = envelope.Id,
            EventType = envelope.Type,
            OccurredUtc = envelope.OccurredUtc,
            Source = envelope.Source,
            CorrelationId = envelope.CorrelationId,
            TenantId = envelope.TenantId,
            EntityId = envelope.EntityId,
            Severity = ParseSeverity(envelope.Severity),
            Message = envelope.Message,
            PayloadJson = envelope.PayloadJson,
            Status = envelope.Status,
            Attempts = envelope.Attempts,
            NextAttemptUtc = envelope.NextAttemptUtc,
            LastError = envelope.LastError
        }));
    }

    private static NetRatelNotificationSeverity ParseSeverity(string? severity)
    {
        if (string.IsNullOrWhiteSpace(severity))
            return NetRatelNotificationSeverity.Info;

        return Enum.TryParse<NetRatelNotificationSeverity>(severity, true, out var parsed)
            ? parsed
            : NetRatelNotificationSeverity.Info;
    }
}
