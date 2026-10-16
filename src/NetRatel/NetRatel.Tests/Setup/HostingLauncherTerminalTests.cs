using System.Net;
using NetRatel.AgentClient;
using NetRatel.Setup;
using Xunit;

namespace NetRatel.Tests.Setup;

public sealed class HostingLauncherTerminalTests
{
    [Theory]
    [InlineData("https://web.example/base", "https://web.example/base")]
    [InlineData("https://web.example/base/", "https://web.example/base")]
    [InlineData("https://web.example/base/mcp", "https://web.example/base")]
    [InlineData("https://web.example/nested/site/mcp/", "https://web.example/nested/site")]
    [InlineData("https://web.example:443/base/mcp", "https://web.example:443/base")]
    public void Web_and_mcp_addresses_preserve_the_deployment_base_path(string input, string expected)
    {
        var parsed = InstanceSetupAddress.Parse(input);
        Assert.Equal(expected, parsed.Origin);
        Assert.Equal(expected + "/mcp", parsed.McpResource);
    }

    [Theory]
    [InlineData("https://web.example/base/../mcp")]
    [InlineData("https://web.example/base/%2e%2e/mcp")]
    [InlineData("https://web.example/base%2fmcp")]
    [InlineData("https://web.example/base//mcp")]
    [InlineData("https://web.example/base%252fmcp")]
    public void Deployment_paths_reject_traversal_and_encoded_separators(string input)
        => Assert.Throws<AgentClientValidationException>(() => InstanceSetupAddress.Parse(input));

    [Fact]
    public async Task Missing_manager_stops_before_host_approval_or_verifier_requests()
    {
        var path = Path.Combine(Path.GetTempPath(), "netratel-manager-" + Guid.NewGuid().ToString("N"));
        var requests = 0;
        using var handler = new RejectRequestsHandler(() => requests++);
        using var model = new HostingSetupViewModel(new Dictionary<string, string>
        {
            ["--output-directory"] = path,
            ["--instance"] = "https://web.example/base/mcp",
            ["--deployment-manager"] = ""
        }, handler);
        try
        {
            await model.NextAsync();
            Assert.Equal(HostingSetupStep.Instance, model.Step);
            Assert.Contains("deployment_manager_unknown", model.Error.Value);
            Assert.Contains("./setup-http-mcp.sh", model.Error.Value);
            Assert.Equal(0, requests);
            Assert.Null(model.Result);
        }
        finally
        {
            await model.CleanupAsync();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private sealed class RejectRequestsHandler(Action count) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            count();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
