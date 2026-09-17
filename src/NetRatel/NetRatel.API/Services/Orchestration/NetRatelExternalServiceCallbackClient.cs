using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using NetRatel.Application.Events;

namespace NetRatel.API.Services.Orchestration;

public sealed class NetRatelExternalServiceCallbackClient(
    HttpClient httpClient,
    INetRatelSystemTokenService tokenService,
    IOptions<NetRatelExternalServiceCallbackOptions> options,
    IEventRecorder events,
    ILogger<NetRatelExternalServiceCallbackClient> logger) : INetRatelExternalServiceCallbackClient
{
    private const string CallbackRejectedEventType = "DomainEvent.Orchestration.ExternalServiceCallbackRejected";
    private const string CallbackFailedEventType = "DomainEvent.Orchestration.ExternalServiceCallbackFailed";

    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    };

    private readonly HttpClient _httpClient = httpClient;
    private readonly INetRatelSystemTokenService _tokenService = tokenService;
    private readonly NetRatelExternalServiceCallbackOptions _options = options.Value;
    private readonly IEventRecorder _events = events;
    private readonly ILogger<NetRatelExternalServiceCallbackClient> _logger = logger;

    public async Task SendStatusAsync(NetRatelExternalServiceCallbackRequest request, string correlationId, CancellationToken ct = default)
    {
        var resolved = await ResolveCallbackTargetAsync(ct);

        if (string.IsNullOrWhiteSpace(resolved.Audience))
        {
            _logger.LogWarning("Skipping NetRatel -> ExternalService callback because Orchestration:ExternalService:Audience is not configured.");
            return;
        }

        if (resolved.CallbackUri is null)
        {
            _logger.LogWarning("Skipping NetRatel -> ExternalService callback because Orchestration:ExternalService:BaseUrl is not configured.");
            return;
        }

        var token = await _tokenService.GetTokenAsync(resolved.Audience, ct);
        for (var attempt = 0; attempt < RetryDelays.Length + 1; attempt++)
        {
            try
            {
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, resolved.CallbackUri)
                {
                    Content = JsonContent.Create(request)
                };
                requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                requestMessage.Headers.Remove("X-Correlation-Id");
                requestMessage.Headers.Add("X-Correlation-Id", correlationId);

                using var response = await _httpClient.SendAsync(requestMessage, ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                var statusCode = (int)response.StatusCode;
                if (response.StatusCode is HttpStatusCode.BadRequest
                    or HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden)
                {
                    var responseBody = await SafeReadBodyAsync(response, ct);
                    _logger.LogWarning(
                        "NetRatel callback rejected by ExternalService. Status={StatusCode} taskId={RequestTaskId} netratelRequestId={NetRatelRequestId} executionId={ExecutionId} body={ResponseBody}",
                        statusCode,
                        request.RequestTaskId,
                        request.NetRatelRequestId ?? request.RequestId,
                        request.ExecutionId,
                        responseBody);
                    await RecordAsync(
                        CallbackRejectedEventType,
                        "Warning",
                        $"ExternalService rejected NetRatel callback with status {statusCode}.",
                        request,
                        correlationId,
                        statusCode,
                        responseBody,
                        ct);
                    return;
                }

                if (statusCode < 500 || attempt >= RetryDelays.Length)
                {
                    var responseBody = await SafeReadBodyAsync(response, ct);
                    _logger.LogWarning(
                        "NetRatel callback failed without retry. Status={StatusCode} taskId={RequestTaskId} netratelRequestId={NetRatelRequestId} executionId={ExecutionId} body={ResponseBody}",
                        statusCode,
                        request.RequestTaskId,
                        request.NetRatelRequestId ?? request.RequestId,
                        request.ExecutionId,
                        responseBody);
                    await RecordAsync(
                        CallbackFailedEventType,
                        "Error",
                        $"ExternalService callback failed with status {statusCode}.",
                        request,
                        correlationId,
                        statusCode,
                        responseBody,
                        ct);
                    return;
                }
            }
            catch (HttpRequestException ex) when (attempt < RetryDelays.Length)
            {
                _logger.LogWarning(
                    ex,
                    "Transient network error while sending NetRatel callback. Retry={RetryAttempt}",
                    attempt + 1);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "NetRatel callback failed after retries exhausted. taskId={RequestTaskId} netratelRequestId={NetRatelRequestId} executionId={ExecutionId}",
                    request.RequestTaskId,
                    request.NetRatelRequestId ?? request.RequestId,
                    request.ExecutionId);
                await RecordAsync(
                    CallbackFailedEventType,
                    "Error",
                    $"ExternalService callback failed after retries exhausted: {ex.Message}",
                    request,
                    correlationId,
                    null,
                    null,
                    ct);
                return;
            }

            if (attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }
    }

    private async Task<(Uri? CallbackUri, string? Audience)> ResolveCallbackTargetAsync(CancellationToken ct)
    {
        var baseUrl = _options.BaseUrl;
        var audience = _options.Audience;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (null, audience);
        }

        return (new Uri(new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute), "api/v1/orchestration/netratel/callback"), audience);
    }

    private async Task RecordAsync(
        string eventType,
        string severity,
        string message,
        NetRatelExternalServiceCallbackRequest request,
        string correlationId,
        int? statusCode,
        string? responseBody,
        CancellationToken ct)
    {
        await _events.RecordAsync(new DomainEvent
        {
            EventType = eventType,
            Source = "Orchestration",
            CorrelationId = correlationId,
            EntityId = request.NetRatelRunId ?? request.ExecutionId,
            Severity = severity,
            Message = message,
            Payload = new
            {
                request.RequestTaskId,
                request.RequestId,
                request.NetRatelRequestId,
                request.NetRatelRunId,
                request.ExecutionId,
                request.Status,
                request.Message,
                request.ResultJson,
                request.ErrorJson,
                request.WorklogSummary,
                request.StartedAtUtc,
                request.CompletedAtUtc,
                StatusCode = statusCode,
                ResponseBody = responseBody
            }
        }, ct);
    }

    private static async Task<string?> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        return body.Length <= 1024 ? body : body[..1024];
    }
}
