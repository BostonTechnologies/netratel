using System.Text.Json;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class ClientPresenceProxyRouteTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public void Client_presence_proxy_matches_the_collection_root_and_descendants()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/appsettings.json")));
        var routes = document.RootElement.GetProperty("ReverseProxy").GetProperty("Routes");

        AssertRoute(routes.GetProperty("client-presence-root-route"), "/api/v2/client-presence");
        AssertRoute(routes.GetProperty("client-presence-route"), "/api/v2/client-presence/{**catch-all}");
    }

    private static void AssertRoute(JsonElement route, string expectedPath)
    {
        Assert.Equal("apiCluster", route.GetProperty("ClusterId").GetString());
        Assert.Equal(expectedPath, route.GetProperty("Match").GetProperty("Path").GetString());

        var hasForwardedPrefixTransform = route.GetProperty("Transforms").EnumerateArray().Any(transform =>
            transform.TryGetProperty("RequestHeader", out var header) &&
            header.GetString() == "X-Forwarded-Prefix" &&
            transform.TryGetProperty("Set", out var value) &&
            value.GetString() == "/api");
        Assert.True(hasForwardedPrefixTransform, "The route must set X-Forwarded-Prefix to /api.");
    }
}
