namespace NetRatel.Infrastructure.Persistence;

public sealed class Tenant
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Location { get; set; }
    public List<string> Domains { get; set; } = new();
    public string? ContactPerson { get; set; }
    public string? ContactEmail { get; set; }
    public bool AutoUpdate { get; set; }
    public string AutoUpdateChannel { get; set; } = "stable";
    public string? AutoUpdateTargetVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public long Version { get; set; } = 1;
}
