using System.Text.Json;
using NetRatel.AgentClient;

namespace NetRatel.Tests.Mcp;

/// <summary>A deterministic complete deployment input for native approval tests.</summary>
internal static class HttpHostingDeploymentFixture
{
    public static HttpHostingSetupRequest WithSource(HttpHostingSetupRequest request)
    {
        var path = Path.Combine(request.OutputDirectory, ".effective-fixture.json");
        var api = new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = request.Instance == "dev" ? "Development" : "Production"
        };
        var callers = request.ExistingM2MCallerClientIds ?? ["netratel.cli", "netratel.client.artifacts"];
        for (var index = 0; index < callers.Count; index++) api["M2M__AllowedCallerClientIds__" + index] = callers[index];
        var source = JsonSerializer.Serialize(new
        {
            services = new Dictionary<string, object>
            {
                ["web"] = new { environment = new Dictionary<string, string?>() },
                ["api"] = new { environment = api },
                ["mcp-http"] = new { environment = new Dictionary<string, string?>() }
            }
        });
        ConfigurationFileStorage.WriteAtomic(path, source, replace: File.Exists(path));
        return request with { EffectiveComposeSourcePath = path };
    }
}
