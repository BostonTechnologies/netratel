using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using NetRatel.Web.Services.ServiceLinks;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace NetRatel.Tests.Web;

public sealed class ServiceLinkResponseProtectionTests
{
    [Theory]
    [InlineData("no-store")]
    [InlineData("public, max-age=600")]
    public async Task Protected_response_keeps_canonical_security_headers_after_actual_proxy_header_copy(string downstreamCacheControl)
    {
        const string body = "Synthetic nonsecret response";
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().Configure(app =>
            app.Run(async context =>
            {
                ServiceLinkBrowserEndpoints.ProtectResponse(context);
                using var downstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
                downstream.Headers.TryAddWithoutValidation("Cache-Control", downstreamCacheControl);
                downstream.Headers.Pragma.ParseAdd("no-cache");
                await HttpTransformer.Default.TransformResponseAsync(context, downstream, context.RequestAborted);
                await context.Response.WriteAsync(body, context.RequestAborted);
            }))).StartAsync();

        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/connect/token");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.CacheControl.Public);
        Assert.Equal("no-cache", response.Headers.GetValues("Pragma").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }
}
