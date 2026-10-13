using System.Net;
using System.Text.Json;

namespace NetRatel.Infrastructure.Clients;

/// <summary>Allowlisted, last-known identity reported by the enrolled client. Directory Name is an operator label.</summary>
public sealed record ClientDirectoryIdentity(string? HostName, string? OperatingSystem, string? Architecture,
    string? PrimaryAddress, IReadOnlyList<string> Addresses)
{
    public static ClientDirectoryIdentity Parse(string? deviceInfoJson)
    {
        if (string.IsNullOrWhiteSpace(deviceInfoJson)) return new(null, null, null, null, []);
        try
        {
            using var document = JsonDocument.Parse(deviceInfoJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, null, null, null, []);
            var addresses = new List<string>();
            void Add(string? value)
            {
                if (value is { Length: <= 64 } && IPAddress.TryParse(value, out var address) &&
                    !addresses.Contains(address.ToString(), StringComparer.OrdinalIgnoreCase)) addresses.Add(address.ToString());
            }
            Add(Read(root, "reportedAddress")); Add(Read(root, "ipAddress"));
            foreach (var name in new[] { "ipAddresses", "addresses" })
                if (root.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array)
                    foreach (var value in values.EnumerateArray()) if (value.ValueKind == JsonValueKind.String) Add(value.GetString());
            return new(Read(root, "hostName"), Read(root, "os"), Read(root, "architecture"), addresses.FirstOrDefault(), addresses);
        }
        catch (JsonException) { return new(null, null, null, null, []); }
    }

    private static string? Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
