using System.Text.Json;

namespace NetRatel.Shared.Contracts.RemoteSupport;

public sealed record RemoteSupportCapabilitySet(
    bool FallbackDesktopSupported,
    bool ConsoleLoginSupported,
    bool SessionInventorySupported,
    bool ExplicitUserAssistSupported,
    bool TargetPreflightSupported,
    int ProtocolRevision,
    bool LegacyAutomaticSupported,
    string Source);

public static class RemoteSupportCapabilityResolver
{
    public static RemoteSupportCapabilitySet Resolve(string? clientInfoJson)
    {
        if (string.IsNullOrWhiteSpace(clientInfoJson))
        {
            return Empty("client_info_missing");
        }

        try
        {
            using var document = JsonDocument.Parse(clientInfoJson);
            var root = document.RootElement;
            var hasNestedCapabilities = TryGetObject(root, "capabilities", out var nested);
            var capabilities = hasNestedCapabilities
                ? nested
                : root;
            var source = hasNestedCapabilities
                ? "client_info_capabilities"
                : "client_info_root";

            var fallback = ReadBoolean(capabilities, root, "remoteDesktopAvailable") == true;
            var consoleScaffolded = ReadBoolean(capabilities, root, "remoteSupportConsoleProviderScaffolded") == true;
            var preLoginLevel = ReadString(capabilities, root, "remoteSupportPreLoginSupportLevel");
            var reportedConsole = ReadBoolean(capabilities, root, "remoteSupportConsoleLoginSupported");
            var inventory = ReadBoolean(capabilities, root, "remoteSupportWindowsSessionInventorySupported") == true;
            var explicitTargeting = ReadBoolean(capabilities, root, "remoteSupportExplicitTargetingSupported") == true;
            var reportedAssist = ReadBoolean(capabilities, root, "remoteSupportInteractiveAssistSupported");
            var preflight = ReadBoolean(capabilities, root, "remoteSupportTargetPreflightSupported") == true;
            var protocolRevision = Math.Max(1, ReadInteger(capabilities, root, "remoteSupportProtocolRevision") ?? 1);

            var console = reportedConsole == true ||
                (consoleScaffolded && string.Equals(preLoginLevel, "media_preview", StringComparison.OrdinalIgnoreCase));
            var assist = reportedAssist == true || (inventory && explicitTargeting);
            var legacy = fallback && !console && !assist;

            return new RemoteSupportCapabilitySet(
                fallback,
                console,
                inventory,
                assist,
                preflight,
                protocolRevision,
                legacy,
                source);
        }
        catch (JsonException)
        {
            return Empty("client_info_malformed");
        }
    }

    private static RemoteSupportCapabilitySet Empty(string source) =>
        new(false, false, false, false, false, 1, false, source);

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static JsonElement? Find(JsonElement primary, JsonElement fallback, string name)
    {
        var value = Find(primary, name);
        return value ?? Find(fallback, name);
    }

    private static JsonElement? Find(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static bool? ReadBoolean(JsonElement primary, JsonElement fallback, string name) =>
        Find(primary, fallback, name) is { } value
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    private static string? ReadString(JsonElement primary, JsonElement fallback, string name) =>
        Find(primary, fallback, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()
            : null;

    private static int? ReadInteger(JsonElement primary, JsonElement fallback, string name)
    {
        var value = Find(primary, fallback, name);
        if (value is null)
        {
            return null;
        }

        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.Value.ValueKind == JsonValueKind.String && int.TryParse(value.Value.GetString(), out number)
            ? number
            : null;
    }
}
