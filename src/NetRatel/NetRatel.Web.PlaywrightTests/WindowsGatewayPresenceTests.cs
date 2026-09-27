namespace NetRatel.Web.PlaywrightTests;

public sealed class WindowsGatewayPresenceTests
{
    [Fact]
    public void Windows_presence_poll_uses_the_canonical_client_presence_route()
    {
        Assert.Equal(
            "/api/v2/client-presence?online=true",
            LocalFirstComposeBrowserSmokeTests.WindowsPresenceEndpoint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tenantId\":2,\"agentId\":\"6bd25dd0-c82e-407a-8f27-5bd892ead974\",\"isAuthoritative\":true}")]
    [InlineData("{\"tenantId\":1,\"agentId\":\"not-a-guid\",\"isAuthoritative\":true}")]
    [InlineData("{\"tenantId\":1,\"agentId\":\"00000000-0000-0000-0000-000000000000\",\"isAuthoritative\":true}")]
    [InlineData("{\"tenantId\":1,\"agentId\":\"6bd25dd0-c82e-407a-8f27-5bd892ead974\",\"isAuthoritative\":false}")]
    public void Presence_parser_rejects_null_non_objects_and_invalid_presence(string? json)
    {
        Assert.False(
            LocalFirstComposeBrowserSmokeTests.TryParseWindowsPresence(json, out var presence));
        Assert.Null(presence);
    }

    [Fact]
    public void Presence_parser_accepts_a_valid_serialized_gateway_presence()
    {
        const string json = """
            {"tenantId":1,"agentId":"6bd25dd0-c82e-407a-8f27-5bd892ead974","isAuthoritative":true}
            """;

        Assert.True(
            LocalFirstComposeBrowserSmokeTests.TryParseWindowsPresence(json, out var presence));
        Assert.NotNull(presence);
        Assert.Equal(1, presence.TenantId);
        Assert.Equal("6bd25dd0-c82e-407a-8f27-5bd892ead974", presence.AgentId);
        Assert.True(presence.IsAuthoritative);
    }
}
