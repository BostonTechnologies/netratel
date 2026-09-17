using System.Text.Json;
using NetRatel.Application.Notifications;

namespace NetRatel.API.Services.Orchestration;

public interface INetRatelExternalServiceCallbackReplayService
{
    bool CanReplay(NetRatelNotificationDto notification);
    Task ReplayAsync(NetRatelNotificationDto notification, CancellationToken ct = default);
}

public sealed class NetRatelExternalServiceCallbackReplayService(
    INetRatelExternalServiceCallbackClient callbackClient) : INetRatelExternalServiceCallbackReplayService
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

        await _callbackClient.SendStatusAsync(
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
