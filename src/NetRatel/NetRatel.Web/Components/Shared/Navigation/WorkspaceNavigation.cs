using Microsoft.AspNetCore.WebUtilities;

namespace NetRatel.Web.Components.Shared.Navigation;

public enum WorkspaceArea { Monitoring, AiOps }

public sealed class WorkspaceNavigation
{
    private readonly Dictionary<(string Route, int Tenant), string> _destinations = [];

    public static string? TabFor(string path) => path.Trim('/').ToLowerInvariant() switch
    {
        "monitoring" => "/monitoring",
        "flows" or "flows/connectors" or "flows/rateldesk-connectors" => "/flows",
        "ai/chat" => "/ai/chat",
        "ai/ops" => "/ai/ops",
        "ai/prompts" => "/ai/prompts",
        _ => null
    };

    public void Remember(Uri location, int authorizedTenant)
    {
        if (authorizedTenant <= 0 || TabFor(location.AbsolutePath) is not { } tab ||
            !location.AbsolutePath.TrimEnd('/').Equals(tab, StringComparison.OrdinalIgnoreCase)) return;
        var query = QueryHelpers.ParseQuery(location.Query);
        if (query.TryGetValue("tenantId", out var linkedTenant))
        {
            if (!int.TryParse(linkedTenant, out var tenant) || tenant != authorizedTenant) return;
            _destinations[(tab, tenant)] = location.PathAndQuery;
        }
        else
        {
            _destinations[(tab, authorizedTenant)] = WithTenant(location.PathAndQuery, authorizedTenant);
        }
    }

    public string Destination(string route, int tenant) => _destinations.GetValueOrDefault((route, tenant)) ?? WithTenant(route, tenant);

    public static string WithTenant(string route, int tenant) => tenant > 0
        ? QueryHelpers.AddQueryString(route, "tenantId", tenant.ToString(System.Globalization.CultureInfo.InvariantCulture))
        : route;
}
