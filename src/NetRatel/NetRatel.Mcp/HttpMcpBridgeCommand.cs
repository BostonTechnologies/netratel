using System.Reflection;
using NetRatel.AgentClient;

namespace NetRatel.Mcp;

public static class HttpMcpBridgeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--version"])
        {
            await Console.Out.WriteLineAsync(typeof(HttpMcpBridge).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
            return 0;
        }
        if (args is not ["--profile", _] && args is not ["--config", _])
        {
            await Console.Error.WriteLineAsync("Usage: NetRatel.Mcp http-bridge --profile NAME | --config ABSOLUTE_PATH. Create a protected HTTP MCP profile with setup --transport http first.");
            return 2;
        }
        try
        {
            if (args[0] == "--config" && !Path.IsPathFullyQualified(args[1]))
                throw new AgentClientValidationException("The configuration path must be absolute.");
            var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.HttpMcpClient,
                ConfigurationPath: args[0] == "--config" ? args[1] : null,
                ProfileName: args[0] == "--profile" ? args[1] : null));
            if (!effective.Validate().IsValid)
                throw new AgentClientValidationException("The HTTP MCP profile is invalid.");
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            var client = new NetRatelAgentClient(effective.Configuration);
            var bridge = new HttpMcpBridge(http, new Uri(effective.Configuration.ApiBaseUrl!), client.GetAccessTokenAsync);
            using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false, true));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
            await bridge.RunAsync(input, output, Console.Error);
            return 0;
        }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync("HTTP MCP bridge could not continue. Validate the selected protected HTTP MCP profile and reconcile uncertain operations before reconnecting. No credential values or remote response bodies are displayed.");
            return 2;
        }
    }
}
