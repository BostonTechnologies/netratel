namespace NetRatel.Infrastructure.Persistence;

public sealed class RemoteSupportTargetSelectionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int TenantId { get; set; }
    public string RequesterIdentity { get; set; } = string.Empty;
    public string ClientIdentity { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string TargetMode { get; set; } = "auto";
    public int? TargetWindowsSessionId { get; set; }
    public string? TargetUserSidHash { get; set; }
    public string? TargetDisplayLabel { get; set; }
    public string? SelectedProvider { get; set; }
    public ulong? InventorySequence { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public string Result { get; set; } = "requested";
    public string? FailureReason { get; set; }
}
