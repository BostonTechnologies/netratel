namespace NetRatel.Infrastructure.Persistence;

public sealed class ClientWindowsSessionInventoryRefresh
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RefreshRequestId { get; set; } = string.Empty;
    public int TenantId { get; set; }
    public string ClientIdentity { get; set; } = string.Empty;
    public string RequesterIdentity { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public bool Completed { get; set; }
    public ulong? InventorySequence { get; set; }
    public DateTime? ObservedAtUtc { get; set; }
    public string Source { get; set; } = "api_refresh";
    public string Status { get; set; } = "requested";
    public string? Error { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
