namespace NetRatel.API.Services.Orchestration;

public sealed class NetRatelExternalServiceCallbackOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string ClientId { get; set; } = "netratel.api";
}

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
}

public interface INetRatelSystemTokenService
{
    Task<string> GetTokenAsync(string audience, CancellationToken ct = default);
}
