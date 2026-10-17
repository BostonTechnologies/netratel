namespace NetRatel.AgentClient;

/// <summary>Only settings consumed by NetRatel's configuration providers have aliases.
/// Other Linux environment names retain their ordinal identity.</summary>
internal static class HttpHostingEnvironmentNames
{
    public static IEqualityComparer<string> Comparer { get; } = new SettingComparer();

    public static bool Recognized(string key)
    {
        var normalized = key.Replace(":", "__", StringComparison.Ordinal);
        return new[] { "NetRatel__", "M2M__", "M2MClients__", "WebGateway__", "HttpHostSetup__", "Authentication__", "Integration__", "NETRATEL_MCP_", "NETRATEL_AUTHENTICATION_", "NETRATEL_CLIENT_" }
            .Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            new[] { "NetRatel_KEYS_DIR", "NetRatel_HTTP_PORT", "ApiBaseUrl", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS" }
            .Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    public static string Normalize(string key) => Recognized(key) ? key.Replace(":", "__", StringComparison.Ordinal) : key;
    public static string Identity(string key) => Recognized(key) ? Normalize(key).ToLowerInvariant() : key;
    public static string Canonical(string key)
    {
        if (key.Equals("NetRatel_KEYS_DIR", StringComparison.OrdinalIgnoreCase)) return "NetRatel_KEYS_DIR";
        if (key.Equals("NetRatel_HTTP_PORT", StringComparison.OrdinalIgnoreCase)) return "NetRatel_HTTP_PORT";
        if (new[] { "NETRATEL_MCP_", "NETRATEL_AUTHENTICATION_", "NETRATEL_CLIENT_", "DOTNET_", "ASPNETCORE_" }
            .Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) && Recognized(key)) return Normalize(key).ToUpperInvariant();
        return Normalize(key);
    }

    public static AgentClientValidationException Conflict(string service, string key, string first, string second, string source, int firstIndex, int secondIndex)
        => new($"environment_alias_conflict: service {service}; normalized key {Safe(Identity(key))}; {Safe(first)} at {source} entry {firstIndex} / {Safe(second)} at {source} entry {secondIndex}. Make these entries exactly equal or remove the unintended alias.");

    private static string Safe(string key) => key.Length <= 128 && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or ':') ? key : "[redacted setting name]";
    private sealed class SettingComparer : IEqualityComparer<string>
    {
        public bool Equals(string? left, string? right) => left is null || right is null ? left == right : Identity(left) == Identity(right);
        public int GetHashCode(string key) => StringComparer.Ordinal.GetHashCode(Identity(key));
    }
}
