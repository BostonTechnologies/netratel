using System.Text.Json;
using NetRatel.Application.Events;
using NetRatel.Application.Observability;
using NetRatel.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace NetRatel.Infrastructure.Events;

public sealed class OutboxEventRecorder(
    OrchestratorDbContext db,
    ILogger<OutboxEventRecorder> logger) : IEventRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OrchestratorDbContext _db = db;
    private readonly ILogger<OutboxEventRecorder> _logger = logger;

    public async Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default)
    {
        using var activity = NetRatelTelemetry.StartActivity("netratel.outbox.record");
        activity?.SetTag("event.id", domainEvent.EventId);
        activity?.SetTag("event.type", domainEvent.EventType);
        activity?.SetTag("event.source", domainEvent.Source);
        activity?.SetTag("event.severity", domainEvent.Severity);
        activity?.SetTag("tenant.id", domainEvent.TenantId);
        activity?.SetTag("entity.id", domainEvent.EntityId);
        activity?.SetTag("correlation.id", domainEvent.CorrelationId);

        _db.OutboxMessages.Add(new OutboxMessage
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

        await _db.SaveChangesAsync(ct);
        NetRatelTelemetry.RecordOutboxEventRecorded(domainEvent);

        _logger.LogInformation(
            "Recorded NetRatel outbox event {EventType} from {Source} with severity {Severity} and status {Status}",
            domainEvent.EventType,
            domainEvent.Source,
            domainEvent.Severity ?? "Info",
            OutboxStatuses.Pending);
    }
}
