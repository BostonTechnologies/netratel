using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NetRatel.AgentClient;
using NetRatel.Mcp;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Core.Prompts;
using NetRatel.Mcp.Tools;
using NetRatel.Shared.Service.Logging;
using Microsoft.Extensions.Options;

if (args is ["http-bridge", .. var bridgeArguments])
    return await HttpMcpBridgeCommand.RunAsync(bridgeArguments);

if (NetRatel.Setup.SetupCommand.IsSetupInvocation(args))
    return await NetRatel.Setup.SetupCommand.RunAsync(args, ConfigurationSurface.StdioMcp);

try
{
var builder = Host.CreateApplicationBuilder(args);

// MCP stdio reserves stdout for protocol frames. The console logger is explicitly stderr-only.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
try
{
    _ = NetRatelLoggingOptions.Load(builder.Configuration);
}
catch (InvalidOperationException)
{
    await Console.Error.WriteLineAsync("Invalid NetRatel:Logging configuration. Use Trace, Debug, Information, Warning, Error, Critical or None and bounded category overrides.");
    return 2;
}
builder.Services.AddOptions<NetRatelLoggingOptions>()
    .Bind(builder.Configuration.GetSection(NetRatelLoggingOptions.SectionName))
    .Validate(value => value.IsValid(), "Invalid NetRatel:Logging diagnostic thresholds.")
    .ValidateOnStart();
builder.Services.AddOptions<LoggerFilterOptions>().PostConfigure<IOptions<NetRatelLoggingOptions>>((filters, configured) =>
{
    var diagnosticOptions = configured.Value;
    // One canonical diagnostic contract; legacy Logging provider rules do not
    // bypass its threshold. stdout remains exclusively the MCP protocol.
    filters.Rules.Clear();
    filters.MinLevel = LogLevel.Trace;
    filters.Rules.Add(new LoggerFilterRule(null, null, null, (_, category, level) => diagnosticOptions.IsEnabled(category, level)));
});

var configurationPath = AgentClientConfigurationResolver.GetMcpConfigurationPath(Environment.GetEnvironmentVariable);
var effective = new NetRatelConfigurationService().Load(new(ConfigurationSurface.StdioMcp, configurationPath));
var validation = effective.Validate();
if (!validation.IsValid)
{
    // Typed validation returns fixed field diagnostics, never supplied values or exception text.
    foreach (var error in validation.Errors)
        await Console.Error.WriteLineAsync(error);
    await Console.Error.WriteLineAsync("NetRatel MCP configuration is invalid. Run NetRatel.Mcp config validate or setup explicitly.");
    return validation.ExitCode;
}
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
    "NetRatel.Mcp")
{
    ReleaseIdentity = NetRatelMcpReleaseIdentity.ReadAssemblyMetadata(typeof(NetRatelMcpStdioApiClient).Assembly)
};
builder.Services.AddSingleton(configuration);
builder.Services.AddSingleton(configurationStore);
builder.Services.AddNetRatelMcpCore(hostContext);
builder.Services.AddSingleton<INetRatelAgentClient, NetRatelAgentClient>();
builder.Services.AddSingleton<INetRatelMcpApiClient, NetRatelMcpStdioApiClient>();
builder.Services
    .AddMcpServer(options => options.ServerInfo = new Implementation
    {
        Name = hostContext.ServerName,
        Version = hostContext.ReleaseIdentity?.ProductVersion ?? "unknown"
    })
    .WithStdioServerTransport()
    .WithTools(NetRatelMcpToolDefinitions.CreateForStdio(new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["netratel_config"] = typeof(ReadOnlyTools),
        ["netratel_remote_support_v2"] = typeof(MutationTools)
    }, mcpInstance, NetRatelMcpStdioJsonContext.Default))
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
