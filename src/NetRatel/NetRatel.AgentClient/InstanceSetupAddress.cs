namespace NetRatel.AgentClient;

/// <summary>The single user-input boundary for the common Web/API/MCP authority.</summary>
public sealed record InstanceSetupAddress(string Origin, string McpResource)
{
    public static InstanceSetupAddress Parse(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) ||
            value.Contains('$') || value.Contains('\'') || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new AgentClientValidationException("Enter an HTTPS instance Web URL without credentials, query or fragment.");
        // Compare the escaped input path as well: URI dot-segment normalization must
        // not quietly turn an unsupported prefix into an approved credential origin.
        var authorityEnd = value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3);
        var inputPath = authorityEnd < 0 ? "/" : value[authorityEnd..];
        if (inputPath is not ("/" or "/mcp" or "/mcp/"))
            throw new AgentClientValidationException("Use the instance Web URL or its /mcp address. Configure split hosts or an application prefix in Advanced.");
        var rawAuthority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..];
        if (authorityEnd >= 0) rawAuthority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..authorityEnd];
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (uri.IsDefaultPort && rawAuthority.EndsWith(":443", StringComparison.Ordinal)) origin += ":443";
        return new(origin, origin + "/mcp");
    }
}
