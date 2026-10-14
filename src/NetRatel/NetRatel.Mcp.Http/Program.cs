using NetRatel.Mcp.Http;

if (NetRatel.Setup.SetupCommand.IsSetupInvocation(args))
{
    Environment.ExitCode = await NetRatel.Setup.SetupCommand.RunAsync(args, NetRatel.AgentClient.ConfigurationSurface.HttpMcpOutbound);
    return;
}

var builder = WebApplication.CreateBuilder(args);
NetRatelMcpHttpApplication.ConfigureServices(builder);

var app = builder.Build();
NetRatelMcpHttpApplication.ConfigurePipeline(app);
app.Run();

public partial class Program;
