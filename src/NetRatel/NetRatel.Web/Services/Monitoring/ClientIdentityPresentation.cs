using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Web.Services.Monitoring;

public static class ClientIdentityPresentation
{
    public static string Name(MonitoringClientIdentityDto? identity) =>
        Text(identity?.DisplayName) ?? Text(identity?.Hostname) ??
        (identity?.Deleted == true ? "Deleted client" : "Client metadata unavailable");

    public static string Context(MonitoringClientIdentityDto? identity, string? tenant = null)
    {
        var parts = new List<string>();
        var hostname = Text(identity?.Hostname);
        if (hostname is not null && !string.Equals(hostname, Name(identity), StringComparison.OrdinalIgnoreCase)) parts.Add(hostname);
        if (Text(identity?.ReportedAddress) is { } address) parts.Add(address);
        if (Text(tenant) is { } tenantName) parts.Add(tenantName);
        if (identity?.Deleted == true) parts.Add("Deleted client");
        return string.Join(" · ", parts);
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
