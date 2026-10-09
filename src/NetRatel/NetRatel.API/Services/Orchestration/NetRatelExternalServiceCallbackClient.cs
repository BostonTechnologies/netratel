using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;

namespace NetRatel.API.Services.Orchestration;

/// <summary>Delivers only a durable execution result belonging to one currently authorized connection.</summary>
public sealed class NetRatelExternalServiceCallbackClient(
    OrchestratorDbContext db,
    PairingBusinessProfileService profiles,
    IRequestService requests,
    IJobRunService runs,
    PairingTransport transport,
    IServicePrincipalRegistry registry,
    IEventRecorder events,
    ILogger<NetRatelExternalServiceCallbackClient> logger) : INetRatelExternalServiceCallbackClient
{
    private const string CallbackPath = "/api/v1/orchestration/provider/callback";
    private const string RejectedEvent = "DomainEvent.Orchestration.ExternalServiceCallbackRejected";
    private const string FailedEvent = "DomainEvent.Orchestration.ExternalServiceCallbackFailed";
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    public async Task SendStatusAsync(NetRatelExternalServiceCallbackRequest request, string correlationId, CancellationToken ct = default)
    {
        await TrySendStatusAsync(request, correlationId, ct);
    }

    public async Task<bool> TrySendStatusAsync(NetRatelExternalServiceCallbackRequest request, string correlationId, CancellationToken ct = default)
    {
        if (!int.TryParse(request.NetRatelRequestId, NumberStyles.None, CultureInfo.InvariantCulture, out var requestId)) return false;
        var binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId, ct);
        if (binding is null || binding.LinkId is null || binding.GrantHash is null || binding.CallbackUrl is null ||
            binding.ExecutionId != request.ExecutionId || request.NetRatelRunId != binding.ExecutionId ||
            request.RequestId != binding.RequestId.ToString(CultureInfo.InvariantCulture) || request.RequestTaskId != binding.RequestTaskId ||
            correlationId != binding.CorrelationId || !ulong.TryParse(binding.ExecutionId, NumberStyles.None, CultureInfo.InvariantCulture, out var runId)) return false;
        var stored = await requests.GetAsync(binding.RequestId, ct);
        var details = await runs.GetDetailsAsync(runId, ct);
        if (stored is null || details is null || details.Run.Status == JobRunState.Pending ||
            !OrchestrationCallbackProjection.Matches(stored, details.Run, binding) ||
            !await OrchestrationCallbackProjection.TerminalReadyAsync(db, details, ct) ||
            request.Status != OrchestrationCallbackProjection.Status(details.Run.Status)) return false;
        request = OrchestrationCallbackProjection.Build(stored, details, binding);

        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                if (!await CurrentInboundAuthorityAsync(binding, ct)) return false;
                var authorization = await profiles.GetCallbackAuthorizationAsync(binding.LinkId, binding.TenantId,
                    binding.PeerInstanceId, binding.PeerTenantId, binding.LinkRevision, binding.GrantHash, binding.CallbackUrl,
                    binding.ParentRequestId, binding.RequestTaskId, ct);
                var uri = new Uri(authorization.CallbackUrl, UriKind.Absolute);
                var status = await transport.PostBusinessStatusAsync(uri.GetLeftPart(UriPartial.Authority), CallbackPath,
                    OrchestrationCallbackProjection.ManagedWire(request), authorization.BearerToken, correlationId, ct);
                if (status is >= 200 and < 300) return true;
                if (status < 500 || attempt == RetryDelays.Length)
                {
                    await RecordAsync(status is 400 or 401 or 403 ? RejectedEvent : FailedEvent,
                        "The paired system rejected the correlated callback.", request, correlationId, status, null, ct);
                    return false;
                }
            }
            catch (PairingException error)
            {
                // Revocation and changed mapping authority stop before business transport.
                await RecordAsync(RejectedEvent, "The connection no longer authorizes this correlated callback.",
                    request, correlationId, error.StatusCode, error.Code, ct);
                return false;
            }
            catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt == RetryDelays.Length)
                {
                    await RecordAsync(FailedEvent, "The paired system could not be reached for the correlated callback.",
                        request, correlationId, null, error is OperationCanceledException ? "callback-timeout" : "callback-network-unavailable", ct);
                    return false;
                }
            }
            if (attempt < RetryDelays.Length) await Task.Delay(RetryDelays[attempt], ct);
        }
        return false;
    }

    private async Task<bool> CurrentInboundAuthorityAsync(ManagedOrchestrationRequestBinding binding, CancellationToken ct)
    {
        var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.ServicePrincipalId, ct);
        if (row is null || row.TenantId != binding.TenantId || row.Status != "active" || row.LinkId != binding.LinkId ||
            row.LinkRevision != binding.LinkRevision || row.GrantHash != binding.GrantHash ||
            row.PeerInstanceId != binding.PeerInstanceId || row.PeerTenantId != binding.PeerTenantId) return false;
        var secret = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ServicePrincipalId == row.Id && x.CredentialRevision == row.CurrentCredentialRevision, ct);
        return secret is not null && await registry.CanIssueScopesAsync(new(row, secret), [OrchestrationManagedAuthorization.InvokeScope], ct);
    }

    private async Task RecordAsync(string type, string message, NetRatelExternalServiceCallbackRequest request,
        string correlationId, int? status, string? code, CancellationToken ct)
    {
        logger.LogWarning("Correlated callback could not be delivered. LocalRequestId={RequestId} ExecutionId={ExecutionId} Status={Status} Code={Code} CorrelationId={CorrelationId}",
            request.NetRatelRequestId, request.ExecutionId, status, code, correlationId);
        await events.RecordAsync(new DomainEvent
        {
            EventType = type, Source = "Orchestration", CorrelationId = correlationId,
            EntityId = request.NetRatelRunId ?? request.ExecutionId, Severity = "Warning", Message = message,
            Payload = new
            {
                request.RequestTaskId, request.RequestId, request.NetRatelRequestId, request.NetRatelRunId,
                request.ExecutionId, request.Status, request.Message, request.ResultJson, request.ErrorJson,
                request.WorklogSummary, request.StartedAtUtc, request.CompletedAtUtc,
                StatusCode = status, FailureCode = code
            }
        }, ct);
    }
}
