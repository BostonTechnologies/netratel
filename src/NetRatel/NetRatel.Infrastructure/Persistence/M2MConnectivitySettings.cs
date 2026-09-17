namespace NetRatel.Infrastructure.Persistence;

public class M2MConnectivitySettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = false;
    public string? RemoteBaseUrl { get; set; }
    public string? RemoteAudience { get; set; }
    public string? RemoteSystemName { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
