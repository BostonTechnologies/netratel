namespace NetRatel.AgentClient;

/// <summary>The single user-input boundary for the common Web/API/MCP deployment address.</summary>
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
            throw new AgentClientValidationException("Enter an HTTPS NetRatel Web URL without credentials, query or fragment.");
        // Check the supplied path before URI dot-segment normalization. Preserve
        // deployment prefixes while rejecting encoded separators and traversal.
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = value.IndexOf('/', authorityStart);
        var inputPath = authorityEnd < 0 ? "" : value[authorityEnd..].TrimEnd('/');
        foreach (var segment in inputPath.Split('/').Skip(1))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded.Length == 0 || decoded is "." or ".." || decoded.Any(char.IsControl) || decoded.IndexOfAny(['/', '\\', '?', '#', '%']) >= 0)
                throw new AgentClientValidationException("Use the NetRatel Web URL or its /mcp endpoint with a normal deployment base path; traversal and encoded separators are unsupported.");
        }
        var basePath = uri.AbsolutePath.TrimEnd('/');
        if (basePath.EndsWith("/mcp", StringComparison.Ordinal)) basePath = basePath[..^4];
        var rawAuthority = authorityEnd < 0 ? value[authorityStart..] : value[authorityStart..authorityEnd];
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (uri.IsDefaultPort && rawAuthority.EndsWith(":443", StringComparison.Ordinal)) origin += ":443";
        origin += basePath;
        return new(origin, origin + "/mcp");
    }
}
