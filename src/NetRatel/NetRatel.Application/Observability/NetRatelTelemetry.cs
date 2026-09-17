using System.Diagnostics;
using System.Diagnostics.Metrics;
using NetRatel.Application.Events;
using NetRatel.Application.Notifications;

namespace NetRatel.Application.Observability;

public static class NetRatelTelemetry
{
    public const string ActivitySourceName = "NetRatel";
    public const string MeterName = "NetRatel";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> OutboxEventsRecorded = Meter.CreateCounter<long>(
        "netratel_outbox_events_recorded_total",
        description: "Outbox events recorded by severity, source, event type, and status.");

    private static readonly Counter<long> OutboxEventsPublished = Meter.CreateCounter<long>(
        "netratel_outbox_events_published_total",
        description: "Outbox events published by severity, source, event type, and status.");

    private static readonly Counter<long> OutboxEventsFailed = Meter.CreateCounter<long>(
        "netratel_outbox_events_failed_total",
        description: "Outbox publish failures by severity, source, event type, and status.");

    private static readonly Counter<long> NotificationStreamConnections = Meter.CreateCounter<long>(
        "netratel_notification_stream_connections_total",
        description: "Notification SSE stream connections.");

    private static readonly Counter<long> NotificationStreamDisconnects = Meter.CreateCounter<long>(
        "netratel_notification_stream_disconnects_total",
        description: "Notification SSE stream disconnects.");

    private static readonly Counter<long> NotificationStreamEventsSent = Meter.CreateCounter<long>(
        "netratel_notification_stream_events_sent_total",
        description: "Notification SSE events sent to connected clients.");

    private static readonly Counter<long> NotificationStreamErrors = Meter.CreateCounter<long>(
        "netratel_notification_stream_errors_total",
        description: "Notification SSE stream errors.");

    public static Activity? StartActivity(string name)
        => ActivitySource.StartActivity(name);

    public static void RecordOutboxEventRecorded(DomainEvent domainEvent)
    {
        OutboxEventsRecorded.Add(1, OutboxTags(
            domainEvent.Severity,
            domainEvent.Source,
            domainEvent.EventType,
            "Pending"));
    }

    public static void RecordOutboxEventPublished(OutboxEnvelope envelope)
    {
        OutboxEventsPublished.Add(1, OutboxTags(
            envelope.Severity,
            envelope.Source,
            envelope.Type,
            "Published"));
    }

    public static void RecordOutboxEventFailed(OutboxEnvelope envelope, string status)
    {
        OutboxEventsFailed.Add(1, OutboxTags(
            envelope.Severity,
            envelope.Source,
            envelope.Type,
            status));
    }

    public static void RecordNotificationStreamConnected(string? tenantId, string? eventType)
    {
        NotificationStreamConnections.Add(1, StreamTags(tenantId, eventType));
    }

    public static void RecordNotificationStreamDisconnected(string? tenantId, string? eventType)
    {
        NotificationStreamDisconnects.Add(1, StreamTags(tenantId, eventType));
    }

    public static void RecordNotificationStreamEventSent(string? tenantId, string? eventType)
    {
        NotificationStreamEventsSent.Add(1, StreamTags(tenantId, eventType));
    }

    public static void RecordNotificationStreamError(string? tenantId, string? eventType)
    {
        NotificationStreamErrors.Add(1, StreamTags(tenantId, eventType));
    }

    private static KeyValuePair<string, object?>[] OutboxTags(
        string? severity,
        string? source,
        string? eventType,
        string? status)
        =>
        [
            new("severity", NormalizeTagValue(severity, "Info")),
            new("source", NormalizeTagValue(source, "unknown")),
            new("event_type", NormalizeTagValue(eventType, "unknown")),
            new("status", NormalizeTagValue(status, "unknown"))
        ];

    private static KeyValuePair<string, object?>[] StreamTags(string? tenantId, string? eventType)
        =>
        [
            new("tenant_scope", string.IsNullOrWhiteSpace(tenantId) ? "all" : "tenant"),
            new("event_type", NormalizeTagValue(eventType, "all"))
        ];

    private static string NormalizeTagValue(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
