using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using NetRatel.AgentClient;
using NetRatel.Mcp;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Core.Prompts;
using NetRatel.Mcp.Tools;

if (NetRatel.Setup.SetupCommand.IsSetupInvocation(args))
    return await NetRatel.Setup.SetupCommand.RunAsync(args, ConfigurationSurface.StdioMcp);

try
{
var builder = Host.CreateApplicationBuilder(args);

// MCP stdio reserves stdout for protocol frames. The console logger is explicitly stderr-only.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var configurationPath = AgentClientConfigurationResolver.GetMcpConfigurationPath(Environment.GetEnvironmentVariable);
var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.StdioMcp, configurationPath));
var validation = effective.Validate();
if (!validation.IsValid) throw new AgentClientValidationException("NetRatel MCP configuration is invalid. Run NetRatel.Mcp config validate or setup explicitly.");
var configuration = effective.Configuration;
var configurationStore = new AgentClientConfigurationStore(effective.ConfigurationPath, readOnly: effective.ConfigurationPath is null);
var apiBaseUri = Uri.TryCreate(configuration.ApiBaseUrl ?? AgentClientConfiguration.DefaultApiBaseUrl, UriKind.Absolute, out var configuredApiBaseUri)
    ? configuredApiBaseUri
    : throw new AgentClientValidationException("NetRatel MCP API base URL must be absolute.");
var mcpInstance = (configuration.Instance ?? "dev").Trim().ToLowerInvariant();
if (mcpInstance is not "dev" and not "prod")
    throw new AgentClientValidationException("NETRATEL_MCP_INSTANCE must be dev or prod.");
var hostContext = new NetRatelMcpHostContext(
    new NetRatelMcpTarget(mcpInstance, apiBaseUri, new Uri("netratel://server/status"), NetRatelMcpCatalog.Revision),
    NetRatelMcpTransport.Stdio,
    "NetRatel.Mcp");
builder.Services.AddSingleton(configuration);
builder.Services.AddSingleton(configurationStore);
builder.Services.AddNetRatelMcpCore(hostContext);
builder.Services.AddSingleton<INetRatelAgentClient, NetRatelAgentClient>();
builder.Services.AddSingleton<INetRatelMcpApiClient, NetRatelMcpStdioApiClient>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools(NetRatelMcpToolDefinitions.CreateForStdio(new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["netratel_config"] = typeof(ReadOnlyTools),
        ["netratel_remote_support_v2"] = typeof(MutationTools)
    }, mcpInstance))
    .WithResources<NetRatelMcpCatalogResources>()
    .WithPrompts<NetRatelPrompts>();

await builder.Build().RunAsync();

return 0;
}
catch (Exception)
{
    await Console.Error.WriteLineAsync("NetRatel MCP could not start. Run NetRatel.Mcp config validate or setup --help explicitly. Diagnostics never write protocol stdout.");
    return 2;
}
