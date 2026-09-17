using NetRatel.Mcp.Http;

var builder = WebApplication.CreateBuilder(args);
NetRatelMcpHttpApplication.ConfigureServices(builder);

var app = builder.Build();
NetRatelMcpHttpApplication.ConfigurePipeline(app);
app.Run();

public partial class Program;
