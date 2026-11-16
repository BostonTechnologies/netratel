using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class McpOperatorOutputDispatchTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/tmp", true)]
    [InlineData("/home/operator/app", true)]
    [InlineData("/srv/application with spaces ", true)]
    [InlineData(@"C:\Windows\System32", true)]
    [InlineData(@"D:\Applications", true)]
    [InlineData("/", false)]
    [InlineData("/tmp", false)]
    public async Task Command_preview_honors_unrestricted_directory_intent_and_preserves_custom_rejections(string directory, bool unrestricted)
    {
        await using var app = await BuildAsync("command");
        if (unrestricted)
            ((Admission)app.Services.GetRequiredService<IMcpOperatorRouteAdmission>()).OverrideConstraints = McpIntegrationPolicyCatalog.AllAccessLimits.Effective();
        using var client = app.GetTestClient();
        var response = await client.PostAsJsonAsync($"/api/v2/mcp/operator/agents/42/{AgentId:D}/commands/preview", new
        { command = "true", shell = "bash", workingDirectory = directory, timeoutSeconds = 60, maximumOutputBytes = 4096 });
        var allowed = unrestricted || directory == "/tmp";
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        if (!allowed) Assert.Contains("command_working_directory_not_allowed", await response.Content.ReadAsStringAsync());
        if (allowed)
        {
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(directory, payload.RootElement.GetProperty("workingDirectory").GetString());
        }
        Assert.Equal(0, app.Services.GetRequiredService<Dispatcher>().Count);
    }
}
