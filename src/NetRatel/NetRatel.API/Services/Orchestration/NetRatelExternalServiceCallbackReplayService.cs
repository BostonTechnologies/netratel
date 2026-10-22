using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Application.Notifications;

namespace NetRatel.API.Services.Orchestration;

public interface INetRatelExternalServiceCallbackReplayService
{
    bool CanReplay(NetRatelNotificationDto notification);
    Task ReplayAsync(NetRatelNotificationDto notification, CancellationToken ct = default);
}

public sealed class NetRatelExternalServiceCallbackReplayService(
    INetRatelExternalServiceCallbackClient callbackClient, OrchestratorDbContext db, TimeProvider clock, IEventRecorder events) : INetRatelExternalServiceCallbackReplayService
{
    private const string CallbackRejectedEventType = "DomainEvent.Orchestration.ExternalServiceCallbackRejected";
    private const string CallbackFailedEventType = "DomainEvent.Orchestration.ExternalServiceCallbackFailed";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly INetRatelExternalServiceCallbackClient _callbackClient = callbackClient;

    public bool CanReplay(NetRatelNotificationDto notification)
        => string.Equals(notification.EventType, CallbackRejectedEventType, StringComparison.Ordinal)
            || string.Equals(notification.EventType, CallbackFailedEventType, StringComparison.Ordinal);

    public async Task ReplayAsync(NetRatelNotificationDto notification, CancellationToken ct = default)
    {
        if (!CanReplay(notification))
        {
            throw new InvalidOperationException($"Notification event type '{notification.EventType}' is not replayable as a ExternalService callback.");
        }

        var payload = DeserializePayload(notification.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.RequestTaskId) || string.IsNullOrWhiteSpace(payload.ExecutionId) || string.IsNullOrWhiteSpace(payload.Status))
        {
            throw new InvalidOperationException("Notification payload does not contain enough ExternalService callback data to replay.");
        }

        if (!int.TryParse(payload.NetRatelRequestId, NumberStyles.None, CultureInfo.InvariantCulture, out var requestId))
            throw new InvalidOperationException("Notification payload does not identify a durable callback request.");
        var completed = await db.Set<OrchestrationCallbackDelivery>().AsNoTracking().AnyAsync(x =>
            x.RequestId == requestId && x.Phase == payload.Status && x.DeliveredAtUtc != null, ct);
        if (completed) return;
        var lease = await OrchestrationCallbackDeliveryState.TryLeaseAsync(db, requestId, payload.Status,
            clock.GetUtcNow(), replay: true, ct);
        if (lease is null) throw new InvalidOperationException("The callback already has an active delivery attempt.");
        await events.RecordAsync(new DomainEvent
        {
            EventType = "DomainEvent.Orchestration.ExternalServiceCallbackReplayRequested", Source = "Orchestration",
            CorrelationId = notification.CorrelationId, EntityId = payload.ExecutionId, Severity = "Information",
            Message = "An operator requested replay of the correlated callback.",
            Payload = new { NotificationId = notification.Id, RequestId = requestId, Phase = payload.Status }
        }, ct);
        var result = await _callbackClient.SendWithDispositionAsync(
            new NetRatelExternalServiceCallbackRequest
            {
                RequestTaskId = payload.RequestTaskId,
                RequestId = payload.RequestId,
                ExecutionId = payload.ExecutionId,
                NetRatelRequestId = payload.NetRatelRequestId,
                NetRatelRunId = payload.NetRatelRunId,
                Status = payload.Status,
                Message = payload.Message,
                ResultJson = payload.ResultJson,
                ErrorJson = payload.ErrorJson,
                WorklogSummary = payload.WorklogSummary,
                StartedAtUtc = payload.StartedAtUtc,
                CompletedAtUtc = payload.CompletedAtUtc
            },
            string.IsNullOrWhiteSpace(notification.CorrelationId) ? $"replay-{notification.Id:N}" : notification.CorrelationId,
            ct);
        await OrchestrationCallbackDeliveryState.CompleteAsync(db, lease.Value.Delivery, lease.Value.LeaseId,
            result, clock.GetUtcNow(), ct);
        if (result.Disposition != OrchestrationCallbackDisposition.Delivered) throw new InvalidOperationException("The correlated callback was not delivered. Check the current connection and peer availability before retrying.");
    }

    private static ReplayPayload? DeserializePayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ReplayPayload>(payloadJson, JsonOptions);
    }

    private sealed class ReplayPayload
    {
        public string? RequestTaskId { get; set; }
        public string? RequestId { get; set; }
        public string? ExecutionId { get; set; }
        public string? NetRatelRequestId { get; set; }
        public string? NetRatelRunId { get; set; }
        public string? Status { get; set; }
        public string? Message { get; set; }
        public string? ResultJson { get; set; }
        public string? ErrorJson { get; set; }
        public string? WorklogSummary { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
    }
}
