namespace NetRatel.Mcp.Core;

/// <summary>
/// Immutable, validated identity of the API a single MCP host may call.
/// It deliberately contains no credential material.
/// </summary>
public sealed record NetRatelMcpTarget
{
    public NetRatelMcpTarget(string instance, Uri apiBaseUri, Uri resourceUri, string catalogRevision)
    {
        if (string.IsNullOrWhiteSpace(instance))
        {
            throw new ArgumentException("MCP target instance is required.", nameof(instance));
        }

        ArgumentNullException.ThrowIfNull(apiBaseUri);
        ArgumentNullException.ThrowIfNull(resourceUri);
        if (!apiBaseUri.IsAbsoluteUri || (apiBaseUri.Scheme != Uri.UriSchemeHttp && apiBaseUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(apiBaseUri.Query) || !string.IsNullOrEmpty(apiBaseUri.Fragment))
        {
            throw new ArgumentException("MCP target API base URI must be an absolute HTTP(S) URI without query or fragment.", nameof(apiBaseUri));
        }

        if (!resourceUri.IsAbsoluteUri || string.IsNullOrWhiteSpace(resourceUri.Scheme))
        {
            throw new ArgumentException("MCP target resource URI must be absolute.", nameof(resourceUri));
        }

        if (string.IsNullOrWhiteSpace(catalogRevision))
        {
            throw new ArgumentException("MCP catalog revision is required.", nameof(catalogRevision));
        }

        Instance = instance.Trim();
        ApiBaseUri = new Uri(apiBaseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        ResourceUri = resourceUri;
        CatalogRevision = catalogRevision.Trim();
    }

    public string Instance { get; }
    public Uri ApiBaseUri { get; }
    public Uri ResourceUri { get; }
    public string CatalogRevision { get; }
}
