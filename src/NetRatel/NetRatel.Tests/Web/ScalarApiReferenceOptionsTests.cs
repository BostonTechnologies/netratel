using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NetRatel.Web.OpenApi;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class ScalarApiReferenceOptionsTests
{
    [Fact]
    public void BuildPublicServerUrl_Uses_Web_Root_Without_Api_Prefix()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("netratel.example.invalid");

        var serverUrl = ScalarApiReferenceOptions.BuildPublicServerUrl(context.Request);

        serverUrl.Should().Be("https://netratel.example.invalid");
        new Uri(new Uri(serverUrl), "/api/v1/tenants").ToString()
            .Should().Be("https://netratel.example.invalid/api/v1/tenants");
    }
}
