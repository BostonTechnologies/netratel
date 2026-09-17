using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Middleware;
using NetRatel.Application.Events;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class CorrelationMiddlewareTests
{
    [Fact]
    public async Task CorrelationIdMiddleware_EchoesIncomingCorrelationHeader()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.Add(CorrelationConstants.HeaderName, "corr-incoming");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(CorrelationConstants.HeaderName).Should().Contain("corr-incoming");
    }

    [Fact]
    public async Task CorrelationIdMiddleware_CreatesCorrelationHeaderWhenMissing()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync("/probe");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues(CorrelationConstants.HeaderName)
            .Should()
            .ContainSingle(value => value.StartsWith("corr-", StringComparison.Ordinal));
    }

    private static async Task<IHost> BuildHostAsync()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.Configure(app =>
                {
                    app.UseMiddleware<CorrelationIdMiddleware>();
                    app.UseMiddleware<CorrelationLoggingMiddleware>();
                    app.Run(async context => await context.Response.WriteAsync("ok"));
                });
            });

        var host = await builder.StartAsync();
        return host;
    }
}
