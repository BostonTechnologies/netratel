using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NetRatel.Web.Configuration;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class ForwardedHeadersMiddlewareTests
{
    [Theory]
    [InlineData("198.51.100.20", "app.example.test", "http|internal.test|198.51.100.20")]
    [InlineData("203.0.113.10", "app.example.test", "https|app.example.test|192.0.2.50")]
    [InlineData("203.0.113.10", "attacker.example.test", "https|internal.test|192.0.2.50")]
    public async Task Forwarding_and_callback_origin_respect_peer_and_host_allowlists(
        string peer, string forwardedHost, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:KnownProxies:0"] = "203.0.113.10",
            ["ForwardedHeaders:AllowedHosts:0"] = "app.example.test"
        }).Build();
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddAuthentication("oidc").AddCookie("session").AddOpenIdConnect("oidc", options =>
            {
                options.ClientId = "proxy-test";
                options.SignInScheme = "session";
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://issuer.example.test",
                    AuthorizationEndpoint = "https://issuer.example.test/authorize"
                };
            });
        }).Configure(app =>
        {
            app.UseForwardedHeaders(ProxyTrustOptions.Create(configuration));
            app.Run(context =>
            {
                context.Response.Headers["X-Test-Effective"] =
                    $"{context.Request.Scheme}|{context.Request.Host}|{context.Connection.RemoteIpAddress}";
                return context.ChallengeAsync("oidc");
            });
        })).StartAsync();
        var server = host.GetTestServer();
        var response = await server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("internal.test");
            context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-For"] = "192.0.2.50";
        });
        response.Response.Headers["X-Test-Effective"].ToString().Should().Be(expected);
        var parts = expected.Split('|');
        var query = QueryHelpers.ParseQuery(new Uri(response.Response.Headers.Location.ToString()).Query);
        query["redirect_uri"].ToString().Should().Be($"{parts[0]}://{parts[1]}/signin-oidc");
    }
}
