namespace NetRatel.Web.Configuration;

/// <summary>Deployment-owned destinations. These generated defaults are fixed until restart.</summary>
public sealed class WebGatewayOptions
{
    public const string SectionName = "WebGateway";
    public string Topology { get; set; } = "Compose";
    public WebGatewayMcpOptions Mcp { get; set; } = new();
    public Uri ApiBaseUri { get; private set; } = null!;
    public Uri? PublicOrigin { get; private set; }

    public static WebGatewayOptions Resolve(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<WebGatewayOptions>() ?? new();
        if (options.Topology is not ("Compose" or "LocalProcess" or "SplitHost"))
            throw new InvalidOperationException("WebGateway:Topology must be Compose, LocalProcess or SplitHost.");
        options.ApiBaseUri = ResolveApiBaseUri(configuration);
        var origin = !string.IsNullOrWhiteSpace(configuration["NETRATEL_PUBLIC_ORIGIN"])
            ? configuration["NETRATEL_PUBLIC_ORIGIN"] : configuration["PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(origin))
        {
            options.PublicOrigin = ValidateBackendUri(origin, "NETRATEL_PUBLIC_ORIGIN/PublicBaseUrl");
            if (options.PublicOrigin.Scheme != Uri.UriSchemeHttps || options.PublicOrigin.AbsolutePath != "/")
                throw new InvalidOperationException("The configured public origin must be an HTTPS origin without a path.");
        }
        if (options.Mcp.AuthenticationMode is not ("LocalCredential" or "Oidc"))
            throw new InvalidOperationException("WebGateway:Mcp:AuthenticationMode must be LocalCredential or Oidc.");
        if (options.Mcp.ActivityTimeout <= TimeSpan.Zero || options.Mcp.ActivityTimeout > TimeSpan.FromDays(1))
            throw new InvalidOperationException("WebGateway:Mcp:ActivityTimeout must be positive and at most one day.");
        if (options.Mcp.Enabled)
            options.Mcp.BackendUri = ValidateBackendUri(options.Mcp.BackendUrl, "WebGateway:Mcp:BackendUrl");
        return options;
    }

    public static Uri ResolveApiBaseUri(IConfiguration configuration) =>
        ValidateBackendUri(configuration["ApiBaseUrl"] ?? "http://api:9222/", "ApiBaseUrl");

    internal static Uri ValidateBackendUri(string? value, string key)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"{key} must be an absolute HTTP or HTTPS URL without credentials, query or fragment.");
        return uri;
    }
}

public sealed class WebGatewayMcpOptions
{
    public bool Enabled { get; set; }
    public string? BackendUrl { get; set; }
    public string AuthenticationMode { get; set; } = "LocalCredential";
    public TimeSpan ActivityTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public Uri? BackendUri { get; internal set; }
}
