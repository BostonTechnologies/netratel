using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using NetRatel.Web.OpenApi;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class ScalarApiReferenceOptionsTests
{
    [Fact]
    public void BuildPublicServerUrl_uses_configured_origin_without_API_prefix_or_request_host()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("untrusted.example.invalid");

        var serverUrl = ScalarApiReferenceOptions.BuildPublicServerUrl(context.Request, new Uri("https://netratel.example.invalid/"));

        serverUrl.Should().Be("https://netratel.example.invalid");
        new Uri(new Uri(serverUrl), "/api/v1/tenants").ToString()
            .Should().Be("https://netratel.example.invalid/api/v1/tenants");
    }
    [Fact]
    public void Unconfigured_origin_uses_relative_Web_root()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("untrusted.example.invalid");
        ScalarApiReferenceOptions.BuildPublicServerUrl(context.Request).Should().Be("/");
    }
}
