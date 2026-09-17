namespace NetRatel.Infrastructure.Persistence;

public sealed class ClientWindowsSessionSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int TenantId { get; set; }
    public string ClientIdentity { get; set; } = string.Empty;
    public int WindowsSessionId { get; set; }
    public string State { get; set; } = "unknown";
    public string? Username { get; set; }
    public string? Domain { get; set; }
    public string? DisplayLabel { get; set; }
    public string? UserSidHash { get; set; }
    public bool IsConsoleSession { get; set; }
    public bool IsActive { get; set; }
    public bool IsConnected { get; set; }
    public bool IsLocked { get; set; }
    public bool IsWinlogon { get; set; }
    public bool IsAssistable { get; set; }
    public string? SessionType { get; set; }
    public string? Provider { get; set; }
    public bool HelperConnected { get; set; }
    public bool HelperVersionMatches { get; set; }
    public bool HelperLaunchable { get; set; }
    public bool HelperRepairable { get; set; }
    public int? HelperPid { get; set; }
    public string? HelperVersion { get; set; }
    public ulong InventorySequence { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string Source { get; set; } = "unknown";
    public bool Stale { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
