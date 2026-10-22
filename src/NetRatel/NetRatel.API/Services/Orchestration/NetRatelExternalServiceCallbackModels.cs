namespace NetRatel.API.Services.Orchestration;

public sealed class NetRatelExternalServiceCallbackRequest
{
    public string RequestTaskId { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public string ExecutionId { get; set; } = string.Empty;
    public string? NetRatelRequestId { get; set; }
    public string? NetRatelRunId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorJson { get; set; }
    public string? WorklogSummary { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public interface INetRatelExternalServiceCallbackClient
{
    Task SendStatusAsync(NetRatelExternalServiceCallbackRequest request, string correlationId, CancellationToken ct = default);
    async Task<bool> TrySendStatusAsync(NetRatelExternalServiceCallbackRequest request, string correlationId, CancellationToken ct = default)
    {
        await SendStatusAsync(request, correlationId, ct);
        return true;
    }

    async Task<OrchestrationCallbackResult> SendWithDispositionAsync(NetRatelExternalServiceCallbackRequest request,
        string correlationId, CancellationToken ct = default)
        => await TrySendStatusAsync(request, correlationId, ct)
            ? new(OrchestrationCallbackDisposition.Delivered)
            : new(OrchestrationCallbackDisposition.Retryable);
}

public enum OrchestrationCallbackDisposition { Delivered, Retryable, Rejected }

/// <summary>Safe delivery metadata only; payloads and credentials remain outside durable delivery state.</summary>
public sealed record OrchestrationCallbackResult(OrchestrationCallbackDisposition Disposition,
    int? StatusCode = null, string? FailureCode = null)
{
    internal static bool IsTerminalRejection(int status)
        => status is >= 400 and < 500 and not (408 or 425 or 429);
}
