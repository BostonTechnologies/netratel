namespace NetRatel.Infrastructure.Persistence;

public enum ClientOffboardingState : short
{
    Requested = 0,
    Accepted = 1,
    Completed = 2,
    Failed = 3
}

/// <summary>One durable uninstall request. A launch acknowledgement never completes this operation.</summary>
public sealed class ClientOffboardingOperation
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid ConnectionId { get; set; }
    public long ConnectionEpoch { get; set; }
    public ClientOffboardingState State { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string ExpectedKeyFingerprint { get; set; } = string.Empty;
    public string CompletionTokenHash { get; set; } = string.Empty;
    public string? ProtectedCompletionToken { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset StartDeadlineUtc { get; set; }
    public DateTimeOffset ReceiptExpiresAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string? FailureCode { get; set; }
    public string? ReceiptHash { get; set; }
    public bool ServiceRemoved { get; set; }
    public bool FilesRemoved { get; set; }
}
