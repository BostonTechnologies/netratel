using System.Text.Json;

namespace NetRatel.Shared.Contracts.Monitoring;

/// <summary>Tenant-scoped directory identity. ReportedAddress is client metadata, never the proxy peer address.</summary>
public sealed record MonitoringClientIdentityDto(Guid AgentId, string DisplayName, string? Hostname = null,
    string? ReportedAddress = null, bool Deleted = false, bool MetadataMissing = false);

public static class MonitoringIdentityPresentation
{
    public static MonitoringClientIdentityDto Create(Guid agentId, string? name, string? deviceInfoJson, bool deleted = false)
    {
        string? host = null, address = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(deviceInfoJson))
            {
                using var document = JsonDocument.Parse(deviceInfoJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    host = Read(document.RootElement, "hostName");
                    address = Read(document.RootElement, "reportedAddress") ?? Read(document.RootElement, "ipAddress");
                }
            }
        }
        catch (JsonException) { return Create(agentId, name, null, deleted); }
        var display = Clean(name) ?? host ?? (deleted ? "Deleted client" : "Client details unavailable");
        return new(agentId, display, host, address, deleted, Clean(name) is null && host is null);
    }

    private static string? Read(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        ? Clean(value.GetString()) : null;
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Trim().Where(c => !char.IsControl(c)).Take(256).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }
}
