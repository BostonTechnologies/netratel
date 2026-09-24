using FluentAssertions;
using NetRatel.Shared.Connectivity;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpResourceUriTests
{
    [Theory]
    [InlineData("what ?")]
    [InlineData("/mcp")]
    [InlineData("http://mcp.example.test/mcp")]
    [InlineData("https://user@mcp.example.test/mcp")]
    [InlineData("https://mcp.example.test/mcp?x=1")]
    [InlineData("https://mcp.example.test/mcp#section")]
    [InlineData("https://mcp.example.test/other")]
    [InlineData("https://mcp.example.test/MCP")]
    public void Invalid_public_resource_is_rejected(string input) =>
        McpResourceUri.TryNormalize(input, out _).Should().BeFalse();

    [Fact]
    public void Host_and_trailing_slash_are_canonical_but_path_case_and_base_remain_distinct()
    {
        McpResourceUri.Equivalent("https://MCP.example.test:443/base/mcp/", "https://mcp.example.test/base/mcp").Should().BeTrue();
        McpResourceUri.Equivalent("https://mcp.example.test/Base/mcp", "https://mcp.example.test/base/mcp").Should().BeFalse();
        McpResourceUri.Equivalent("https://mcp.example.test/base/mcp", "https://other.example.test/base/mcp").Should().BeFalse();
    }
}
