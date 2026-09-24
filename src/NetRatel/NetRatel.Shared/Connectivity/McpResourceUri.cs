namespace NetRatel.Shared.Connectivity;

/// <summary>Canonical public address used by the HTTP MCP gateway and its purpose-bound credentials.</summary>
public static class McpResourceUri
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.AbsolutePath.TrimEnd('/').EndsWith("/mcp", StringComparison.Ordinal))
            return false;

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    public static bool Equivalent(string? left, string? right) =>
        TryNormalize(left, out var canonicalLeft) && TryNormalize(right, out var canonicalRight) &&
        string.Equals(canonicalLeft, canonicalRight, StringComparison.Ordinal);
}
