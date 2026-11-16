using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class McpOperatorClientFileEndpointTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/home/operator/app/.env", "/")]
    [InlineData("/srv/application/secrets/settings.json", "/")]
    [InlineData(@"C:\Windows\System32\config.txt", @"C:\")]
    [InlineData(@"D:\Applications\credentials\settings.json", @"D:\")]
    public async Task Explicit_unrestricted_roots_admit_accessible_locations_and_preserve_native_path_validation(string path, string nativeRoot)
    {
        var agent = Guid.NewGuid();
        using var targetApp = await BuildAppAsync(agent, includeReadRoot: true);
        targetApp.Services.GetRequiredService<TestAdmission>().ReadRootsOverride = ["*"];
        var response = await AuthorizedClient(targetApp).GetAsync($"/api/v2/mcp/operator/agents/7/{agent:D}/files/stat?path={Uri.EscapeDataString(path)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(nativeRoot, Assert.Single(targetApp.Services.GetRequiredService<TestFileRegistry>().StatPolicies).AllowedRoots.Single());
    }
}
