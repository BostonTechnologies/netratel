namespace NetRatel.Shared.Client;

/// <summary>
/// Normalizes the public API and gateway addresses used by an agent. API routes
/// are rooted at <c>/api/v1</c> and <c>/api/v2</c>; a mistakenly supplied
/// <c>/api</c> suffix is accepted and removed, while arbitrary path bases are
/// rejected because agent requests use absolute API paths.
/// </summary>
public static class ClientEndpointAddress
{
    /// <summary>Public Web address, including a deliberately configured deployment path base.</summary>
    public static string NormalizePublicWebBase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) ||
            value.Contains('\\') || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Set a public HTTPS Web address without credentials, query or fragment.", nameof(value));
        var authority = NormalizePublicOrigin(uri.GetLeftPart(UriPartial.Authority));
        var text = value.Trim();
        var pathStart = text.IndexOf('/', text.IndexOf("://", StringComparison.Ordinal) + 3);
        var path = pathStart < 0 ? "" : text[pathStart..].TrimEnd('/');
        foreach (var segment in path.Split('/').Skip(1))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded.Length == 0 || decoded is "." or ".." || decoded.Any(char.IsControl) || decoded.IndexOfAny(['/', '\\', '?', '#', '%']) >= 0)
                throw new ArgumentException("Use a normal deployment path without traversal or encoded separators.", nameof(value));
        }
        return authority + uri.AbsolutePath.TrimEnd('/');
    }

    /// <summary>Public installation endpoints use a DNS HTTPS origin on port 443.</summary>
    public static string NormalizePublicOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 ||
            uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains('.') ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.TrimEnd('/').Length != 0)
            throw new ArgumentException("Set a public HTTPS origin without credentials, a path, a query, or a fragment.", nameof(value));
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static string NormalizeApiBase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("API base URL must be an absolute HTTP or HTTPS origin, optionally followed by /api.", nameof(value));
        }

        if ((!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("API base URL must not contain credentials, a query, or a fragment and must use HTTP or HTTPS.", nameof(value));
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length != 0 && !string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("API base URL must be an origin, optionally followed by /api. API routes are added by NetRatel.", nameof(value));
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static string NormalizeGatewayBase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Gateway endpoint must be an absolute HTTPS origin.", nameof(value));
        }

        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath.TrimEnd('/').Length != 0)
        {
            throw new ArgumentException("Gateway endpoint must be an HTTPS origin without credentials, a path, a query, or a fragment.", nameof(value));
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static Uri ComposeApiRoute(string apiBaseUrl, string apiPath)
    {
        if (!apiPath.StartsWith("/api/v1/", StringComparison.Ordinal) &&
            !apiPath.StartsWith("/api/v2/", StringComparison.Ordinal))
        {
            throw new ArgumentException("API route must be rooted at /api/v1 or /api/v2.", nameof(apiPath));
        }

        return new Uri(NormalizeApiBase(apiBaseUrl) + apiPath, UriKind.Absolute);
    }
}
