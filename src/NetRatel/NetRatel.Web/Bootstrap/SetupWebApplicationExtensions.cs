namespace NetRatel.Web.Bootstrap;

/// <summary>
/// Keeps the browser entry point useful during bootstrap without constructing the OIDC-dependent
/// Web application. It intentionally exposes status only; setup completion belongs to P05 once
/// local identity and provider initialization are available.
/// </summary>
public static class SetupWebApplicationExtensions
{
    private const string SetupApiClientName = "bootstrap-status";

    public static bool RequiresSetupShell(IConfiguration configuration)
    {
        var oidc = configuration.GetSection("Authentication:Oidc");
        if (!oidc.Exists())
        {
            oidc = configuration.GetSection("Authentication:Azure");
        }

        if (!oidc.Exists())
        {
            oidc = configuration.GetSection("AzureAd");
        }

        var secret = configuration["OIDC_CLIENT_SECRET"] ?? configuration["AZURE_CLIENT_SECRET"] ?? oidc["ClientSecret"];
        return !Uri.TryCreate(oidc["Authority"], UriKind.Absolute, out var authority) ||
               authority.Scheme is not ("https" or "http") ||
               string.IsNullOrWhiteSpace(oidc["ClientId"]) ||
               string.IsNullOrWhiteSpace(secret);
    }

    public static void AddSetupShell(this IServiceCollection services, IConfiguration configuration)
    {
        var apiBaseUrl = configuration["ApiBaseUrl"] ?? "http://api:9222";
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var apiBaseUri) || apiBaseUri.Scheme is not ("https" or "http"))
        {
            throw new InvalidOperationException("ApiBaseUrl must be an absolute HTTP or HTTPS URI for the bootstrap setup shell.");
        }

        services.AddHttpClient(SetupApiClientName, client => client.BaseAddress = apiBaseUri);
    }

    public static void MapSetupShell(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive", lifecycle = "setup-shell" })).AllowAnonymous();
        app.MapGet("/", SetupPage).AllowAnonymous();
        app.MapGet("/setup", SetupPage).AllowAnonymous();
        app.MapGet("/api/v2/setup/status", async (IHttpClientFactory clients, CancellationToken cancellationToken) =>
        {
            using var response = await clients.CreateClient(SetupApiClientName)
                .GetAsync("/api/v2/setup/status", cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
        }).AllowAnonymous();
    }

    private static IResult SetupPage() => Results.Content("""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>NetRatel setup</title><style>body{font-family:system-ui,sans-serif;max-width:42rem;margin:4rem auto;padding:0 1rem}code{background:#f3f4f6;padding:.15rem .3rem}</style></head>
        <body><main><h1>NetRatel setup</h1><p>This instance is not yet operational. Complete setup with the deployment-controlled proof; local account creation is enabled in a later setup phase.</p>
        <p id="status">Checking setup status…</p></main><script>fetch('/api/v2/setup/status').then(r=>r.json()).then(x=>document.getElementById('status').textContent='State: '+x.state).catch(()=>document.getElementById('status').textContent='Setup status is temporarily unavailable.');</script></body></html>
        """, "text/html");
}
