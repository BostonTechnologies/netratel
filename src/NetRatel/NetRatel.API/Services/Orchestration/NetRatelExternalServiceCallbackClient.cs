using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using NetRatel.Application.Events;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;

namespace NetRatel.API.Services.Orchestration;

public sealed class NetRatelExternalServiceCallbackClient(
    HttpClient httpClient,
    INetRatelSystemTokenService tokenService,
    IOptions<NetRatelExternalServiceCallbackOptions> options,
    IEventRecorder events,
    ILogger<NetRatelExternalServiceCallbackClient> logger,
    OrchestratorDbContext? db = null,
    ServiceLinkProfileService? profiles = null,
    IRequestService? requests = null,
    IJobRunService? runs = null,
    ServiceLinkTransport? managedTransport = null) : INetRatelExternalServiceCallbackClient
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
        await TrySendStatusAsync(request, correlationId, ct);
    }

    public async Task<bool> TrySendStatusAsync(NetRatelExternalServiceCallbackRequest request, string correlationId, CancellationToken ct = default)
    {
        ManagedOrchestrationRequestBinding? binding = null;
        if (db is not null)
        {
            if (int.TryParse(request.NetRatelRequestId, out var requestId))
                binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
            if (binding is null)
            {
                // A forged/missing local id must never move a managed execution to the legacy signer.
                if (await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().AnyAsync(x => x.ExecutionId == request.ExecutionId, ct) ||
                    int.TryParse(request.NetRatelRequestId, out var localId) && await db.Requests.AsNoTracking().AnyAsync(x => x.Id == localId && x.SourceSystem.StartsWith("service:"), ct)) return false;
            }
            else
            {
                if (profiles is null || requests is null || runs is null || binding.LinkId is null || binding.GrantHash is null || binding.CallbackUrl is null ||
                    binding.ExecutionId != request.ExecutionId || request.NetRatelRunId != binding.ExecutionId ||
                    request.RequestId != binding.RequestId.ToString(System.Globalization.CultureInfo.InvariantCulture) || request.RequestTaskId != binding.RequestTaskId || correlationId != binding.CorrelationId ||
                    !ulong.TryParse(binding.ExecutionId, out var runId)) return false;
                var stored = await requests.GetAsync(binding.RequestId, ct);
                var details = await runs.GetDetailsAsync(runId, ct);
                if (stored is null || details is null || details.Run.Status == JobRunState.Pending || !OrchestrationCallbackProjection.Matches(stored, details.Run, binding) ||
                    !await OrchestrationCallbackProjection.TerminalReadyAsync(db, details, ct) ||
                    request.Status != OrchestrationCallbackProjection.Status(details.Run.Status) ||
                    !await db.Set<ServicePrincipalRegistration>().AsNoTracking().AnyAsync(x => x.Id == binding.ServicePrincipalId &&
                        x.TenantId == binding.TenantId && x.Status == "active" && x.LinkId == binding.LinkId && x.LinkRevision == binding.LinkRevision &&
                        x.GrantHash == binding.GrantHash && x.PeerInstanceId == binding.PeerInstanceId && x.PeerTenantId == binding.PeerTenantId, ct)) return false;
                if (!await db.Set<ServiceLinkAttempt>().AsNoTracking().AnyAsync(x => x.LinkId == binding.LinkId &&
                    x.InboundPrincipalId == binding.ServicePrincipalId && x.LinkRevision == binding.LinkRevision && x.GrantHash == binding.GrantHash, ct)) return false;
                request = OrchestrationCallbackProjection.Build(stored, details, binding);
            }
        }
        var resolved = ResolveLegacyCallbackTarget();
        if (binding is null && (resolved.CallbackUri is null || string.IsNullOrWhiteSpace(resolved.Audience))) return false;
        for (var attempt = 0; attempt < RetryDelays.Length + 1; attempt++)
        {
            try
            {
                Uri callbackUri;
                string token;
                if (binding is not null)
                {
                    var authorization = await profiles!.GetCallbackAuthorizationAsync(binding.LinkId!, binding.TenantId,
                        binding.PeerInstanceId, binding.PeerTenantId, binding.LinkRevision, binding.GrantHash!, binding.CallbackUrl!,
                        binding.ParentRequestId, binding.RequestTaskId, ct);
                    callbackUri = new Uri(authorization.CallbackUrl, UriKind.Absolute);
                    token = authorization.BearerToken;
                }
                else
                {
                    callbackUri = resolved.CallbackUri!;
                    token = await _tokenService.GetTokenAsync(resolved.Audience!, ct);
                }
                if (binding is not null)
                {
                    if (managedTransport is null) return false;
                    var managedStatus = await managedTransport.PostStatusAsync(callbackUri.AbsoluteUri, OrchestrationCallbackProjection.ManagedWire(request), correlationId, token, ct);
                    if (managedStatus is >= 200 and < 300) return true;
                    if (managedStatus < 500 || attempt >= RetryDelays.Length)
                    {
                        await RecordAsync(managedStatus is 400 or 401 or 403 ? CallbackRejectedEventType : CallbackFailedEventType,
                            "Warning", $"The approved peer rejected the callback with status {managedStatus}.", request, correlationId, managedStatus, null, ct);
                        return false;
                    }
                    await Task.Delay(RetryDelays[attempt], ct);
                    continue;
                }
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, callbackUri)
                {
                    Content = JsonContent.Create(request)
                };
                requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                requestMessage.Headers.Remove("X-Correlation-Id");
                requestMessage.Headers.Add("X-Correlation-Id", correlationId);

                using var response = await _httpClient.SendAsync(requestMessage, ct);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                var statusCode = (int)response.StatusCode;
                if (response.StatusCode is HttpStatusCode.BadRequest
                    or HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden)
                {
                    var responseBody = binding is null ? await SafeReadBodyAsync(response, ct) : null;
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
                    return false;
                }

                if (statusCode < 500 || attempt >= RetryDelays.Length)
                {
                    var responseBody = binding is null ? await SafeReadBodyAsync(response, ct) : null;
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
                    return false;
                }
            }
            catch (ServiceLinkProtocolException)
            {
                // Disable, unlink, identity drift, and expired grants stop before business HTTP.
                return false;
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
                return false;
            }

            if (attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }
        return false;
    }

    private (Uri? CallbackUri, string? Audience) ResolveLegacyCallbackTarget()
    {
        var baseUrl = _options.BaseUrl;
        var audience = _options.Audience;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (null, audience);
        }

        return (new Uri(new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute), "api/v1/orchestration/provider/callback"), audience);
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
