using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using NetRatel.API.Middleware;
using NetRatel.API.Endpoints.Auth;
using NetRatel.Shared.Connectivity;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class IntegrationPairingBodyLimitTests
{
    [Theory]
    [InlineData(McpLocalDelegationEndpoints.AccessStartPath, false)]
    [InlineData(McpLocalDelegationEndpoints.AccessStartPath, true)]
    [InlineData("/api/v2/mcp/operator/access/requests", false)]
    [InlineData("/api/v2/mcp/operator/access/requests", true)]
    [InlineData("/API/V2/MCP/OPERATOR/ACCESS/REQUESTS/", true)]
    [InlineData("/api/v2/account/integration-credentials/credential/access-requests", false)]
    [InlineData("/API/V2/ACCOUNT/INTEGRATION-CREDENTIALS/credential/ACCESS-REQUESTS/request/apply", true)]
    [InlineData(IntegrationPairingProtocol.Route + "/start", false)]
    [InlineData(IntegrationPairingProtocol.Route + "/start", true)]
    [InlineData(HttpHostSetupProtocol.Route + "/start", false)]
    [InlineData(HttpHostSetupProtocol.Route + "/start", true)]
    [InlineData(HttpHostSetupProtocol.AccountRoute + "/approve", false)]
    [InlineData(HttpHostSetupProtocol.AccountRoute + "/approve", true)]
    [InlineData("/API/INTEGRATION-PAIRING/V1/start", true)]
    [InlineData(IntegrationPairingProtocol.AccountRoute + "/approve", false)]
    [InlineData(IntegrationPairingProtocol.AccountRoute + "/approve", true)]
    public async Task Oversized_known_length_and_chunked_pairing_json_is_bounded_before_binding(string path, bool chunked)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.Method = "POST";
        var oversized = Encoding.UTF8.GetBytes("{\"name\":\"" + new string('x', (int)PairingRequestBodyLimitMiddleware.MaximumBytes) + "\"}");
        http.Request.Body = new MemoryStream(oversized);
        if (!chunked) http.Request.ContentLength = oversized.Length;
        http.Response.Body = new MemoryStream();
        var downstreamReached = false;
        var middleware = new PairingRequestBodyLimitMiddleware(async context =>
        {
            downstreamReached = true;
            await context.Request.Body.CopyToAsync(Stream.Null);
            context.Response.StatusCode = 204;
        });
        await middleware.InvokeAsync(http);
        http.Response.StatusCode.Should().Be(413);
        http.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        if (!chunked) downstreamReached.Should().BeFalse();
        http.Response.Body.Position = 0;
        (await new StreamReader(http.Response.Body).ReadToEndAsync()).Should().Contain("pairing-request-too-large").And.NotContain(new string('x', 100));
    }
}
