using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

        if (domainEvent.EventType == NetRatelEventTypes.System.UnhandledException &&
            domainEvent.Payload is ExceptionPayload { Occurrence: { } occurrence } exception)
        {
            await RecordExceptionOccurrenceAsync(domainEvent, exception, occurrence, ct);
            NetRatelTelemetry.RecordOutboxEventRecorded(domainEvent);
            return;
        }

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

    private async Task RecordExceptionOccurrenceAsync(DomainEvent domainEvent, ExceptionPayload exception,
        ExceptionOccurrencePayload occurrence, CancellationToken ct)
    {
        // The middleware opts in only agent-update attempt requests. Attempt identity
        // bounds the occurrence lifetime; retries retain one durable notification across
        // API nodes, restarts and time boundaries. Distinct attempts/faults remain separate.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"exception:{occurrence.ErrorIdentity}"));
        var id = new Guid(hash.AsSpan(0, 16));
        var payload = JsonSerializer.Serialize(exception, JsonOptions);
        if (_db.Database.IsNpgsql())
        {
            // Keep delivery/read state intact. Atomic upsert counts concurrent retries and
            // updates only the aggregate evidence on the existing notification.
            await _db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "OutboxMessages" ("Id", "OccurredUtc", "Type", "Source", "CorrelationId", "TenantId",
                    "EntityId", "Severity", "Message", "PayloadJson", "Status", "Attempts", "NextAttemptUtc")
                VALUES ({{id}}, {{domainEvent.OccurredUtc}}, {{domainEvent.EventType}}, {{domainEvent.Source}},
                    {{domainEvent.CorrelationId}}, {{domainEvent.TenantId}}, {{domainEvent.EntityId}}, {{domainEvent.Severity}},
                    {{domainEvent.Message}}, {{payload}}, {{OutboxStatuses.Pending}}, 0, {{domainEvent.OccurredUtc}})
                ON CONFLICT ("Id") DO UPDATE SET
                    "PayloadJson" = jsonb_set("OutboxMessages"."PayloadJson"::jsonb, '{occurrence}',
                        ("OutboxMessages"."PayloadJson"::jsonb -> 'occurrence') ||
                        jsonb_build_object('count', ("OutboxMessages"."PayloadJson"::jsonb #>> '{occurrence,count}')::bigint + 1) ||
                        CASE WHEN (EXCLUDED."PayloadJson"::jsonb #>> '{occurrence,firstSeenUtc}')::timestamptz <
                            ("OutboxMessages"."PayloadJson"::jsonb #>> '{occurrence,firstSeenUtc}')::timestamptz
                        THEN jsonb_build_object('firstSeenUtc', EXCLUDED."PayloadJson"::jsonb #> '{occurrence,firstSeenUtc}',
                            'firstCorrelationId', EXCLUDED."PayloadJson"::jsonb #> '{occurrence,firstCorrelationId}',
                            'firstTraceId', EXCLUDED."PayloadJson"::jsonb #> '{occurrence,firstTraceId}')
                        ELSE '{}'::jsonb END ||
                        CASE WHEN (EXCLUDED."PayloadJson"::jsonb #>> '{occurrence,lastSeenUtc}')::timestamptz >=
                            ("OutboxMessages"."PayloadJson"::jsonb #>> '{occurrence,lastSeenUtc}')::timestamptz
                        THEN jsonb_build_object('lastSeenUtc', EXCLUDED."PayloadJson"::jsonb #> '{occurrence,lastSeenUtc}',
                            'lastCorrelationId', EXCLUDED."PayloadJson"::jsonb #> '{occurrence,lastCorrelationId}',
                            'lastTraceId', EXCLUDED."PayloadJson"::jsonb #> '{occurrence,lastTraceId}')
                        ELSE '{}'::jsonb END)::text,
                    "Message" = EXCLUDED."Message" || ' Occurrences: ' ||
                        (("OutboxMessages"."PayloadJson"::jsonb #>> '{occurrence,count}')::bigint + 1)::text
                """, ct);
            return;
        }

        var existing = await _db.OutboxMessages.FindAsync([id], ct);
        if (existing is not null)
        {
            var previous = JsonSerializer.Deserialize<ExceptionPayload>(existing.PayloadJson, JsonOptions)!.Occurrence!;
            var updated = previous with
            {
                Count = previous.Count + 1,
                FirstSeenUtc = occurrence.FirstSeenUtc < previous.FirstSeenUtc ? occurrence.FirstSeenUtc : previous.FirstSeenUtc,
                FirstCorrelationId = occurrence.FirstSeenUtc < previous.FirstSeenUtc ? occurrence.FirstCorrelationId : previous.FirstCorrelationId,
                FirstTraceId = occurrence.FirstSeenUtc < previous.FirstSeenUtc ? occurrence.FirstTraceId : previous.FirstTraceId,
                LastSeenUtc = occurrence.LastSeenUtc >= previous.LastSeenUtc ? occurrence.LastSeenUtc : previous.LastSeenUtc,
                LastCorrelationId = occurrence.LastSeenUtc >= previous.LastSeenUtc ? occurrence.LastCorrelationId : previous.LastCorrelationId,
                LastTraceId = occurrence.LastSeenUtc >= previous.LastSeenUtc ? occurrence.LastTraceId : previous.LastTraceId
            };
            existing.PayloadJson = JsonSerializer.Serialize(exception with { Occurrence = updated }, JsonOptions);
            existing.Message = $"{domainEvent.Message} Occurrences: {updated.Count}";
        }
        else
        {
            _db.OutboxMessages.Add(new OutboxMessage
            {
                Id = id, OccurredUtc = domainEvent.OccurredUtc, Type = domainEvent.EventType,
                Source = domainEvent.Source, CorrelationId = domainEvent.CorrelationId, TenantId = domainEvent.TenantId,
                EntityId = domainEvent.EntityId, Severity = domainEvent.Severity, Message = domainEvent.Message,
                PayloadJson = payload, Status = OutboxStatuses.Pending, NextAttemptUtc = domainEvent.OccurredUtc
            });
        }
        await _db.SaveChangesAsync(ct);
    }
}
