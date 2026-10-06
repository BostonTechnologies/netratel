using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Infrastructure.ServiceLinks.Network;
using Xunit;

namespace NetRatel.Tests.ServiceLinks;

public sealed class ServiceLinkDynamicNetworkPolicyTests
{
    [Theory]
    [InlineData("http://[fd00:ec2::254]/")]
    [InlineData("https://[fd00:ec2::254]/")]
    public void Metadata_IPv6_is_denied_for_literals_and_every_DNS_answer_even_with_private_opt_in(string endpoint)
    {
        var metadata = IPAddress.Parse("fd00:ec2::254");
        Assert.Throws<ArgumentException>(() => ServiceLinkEndpointPolicy.Validate(new Uri(endpoint), "peer", true));
        Assert.Throws<ArgumentException>(() => ServiceLinkEndpointPolicy.ValidateResolvedAddresses(
            new Uri("https://peer.example.test/"), [metadata], "peer", true));
        Assert.Throws<ArgumentException>(() => ServiceLinkEndpointPolicy.ValidateResolvedAddresses(
            new Uri("https://peer.example.test/"), [IPAddress.Parse("fd00:ec2::254%7")], "peer", true));
        Assert.Throws<ArgumentException>(() => ServiceLinkEndpointPolicy.ValidateResolvedAddresses(
            new Uri("https://peer.example.test/"), [IPAddress.Parse("10.2.3.4"), metadata], "peer", true));
        ServiceLinkEndpointPolicy.ValidateResolvedAddresses(new Uri("https://peer.example.test/"),
            [IPAddress.Parse("10.2.3.4")], "peer", true);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("fd12::1234")]
    [InlineData("127.0.0.1")]
    public void Private_HTTPS_literals_and_DNS_answers_need_the_current_opt_in(string address)
    {
        var ip = IPAddress.Parse(address);
        var host = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address;
        Assert.Throws<ArgumentException>(() => ServiceLinkEndpointPolicy.Validate(new Uri($"https://{host}/"), "peer", false));
        Assert.Throws<ArgumentException>(() => ServiceLinkEndpointPolicy.ValidateResolvedAddresses(
            new Uri("https://peer.example.test/"), [IPAddress.Parse("8.8.8.8"), ip], "peer", false));
        ServiceLinkEndpointPolicy.ValidateResolvedAddresses(new Uri("https://peer.example.test/"), [ip], "peer", true);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Existing_transport_uses_reloaded_common_options_and_cannot_dispatch_private_HTTPS(bool identity, bool linking)
    {
        var configuration = Configuration(identity, linking);
        using var provider = Services(configuration);
        var monitor = provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>();
        var handler = new CountingHandler();
        using var client = new HttpClient(handler);
        var transport = new ServiceLinkTransport(client, provider.GetRequiredService<IOptions<ServiceLinkOptions>>(), monitor);
        await transport.GetAsync<System.Text.Json.JsonDocument>("https://10.1.2.3/probe", CancellationToken.None);
        Assert.Equal(1, handler.Requests);
        RemoveOptIn(configuration);
        Assert.False(monitor.CurrentValue.AllowPrivateHttp);
        await Assert.ThrowsAsync<ArgumentException>(() => transport.GetAsync<System.Text.Json.JsonDocument>(
            "https://10.1.2.3/probe", CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Existing_HTTPS_client_rechecks_actual_connections_and_rejects_a_stale_request_option_after_reload()
    {
        var configuration = Configuration(false, true);
        using var provider = Services(configuration);
        var monitor = provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        await using var app = builder.Build();
        var connections = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        app.MapGet("/probe", (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            connections.TryAdd(context.Connection.Id, 0);
            return Microsoft.AspNetCore.Http.Results.Json(new { accepted = true });
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var endpoint = new UriBuilder(address) { Host = "localhost", Path = "/probe" }.Uri;
        using var sockets = ServiceLinkSafeHttpMessageHandler.Create(currentAllowPrivateHttp: () => monitor.CurrentValue.AllowPrivateHttp);
        // Pin only this disposable test server's certificate; production TLS validation is unchanged.
        sockets.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, _) => remote is not null &&
            remote.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData);
        using var client = new HttpClient(sockets) { Timeout = TimeSpan.FromSeconds(10) };
        var transport = new ServiceLinkTransport(client, provider.GetRequiredService<IOptions<ServiceLinkOptions>>(), monitor);
        using (await transport.GetAsync<System.Text.Json.JsonDocument>(endpoint.AbsoluteUri, CancellationToken.None)) { }
        using (await transport.GetAsync<System.Text.Json.JsonDocument>(endpoint.AbsoluteUri, CancellationToken.None)) { }
        Assert.Equal(2, connections.Count);

        using var stale = new HttpRequestMessage(HttpMethod.Get, endpoint);
        stale.Options.Set(ServiceLinkSafeHttpMessageHandler.AllowPrivateHttpOption, true);
        RemoveOptIn(configuration);
        var error = await Record.ExceptionAsync(async () => { using var response = await client.SendAsync(stale); });
        Assert.NotNull(error);
        Assert.True(error is HttpRequestException or ArgumentException, "The current connection policy must reject the stale private opt-in.");
        Assert.Equal(2, connections.Count);
    }

    private static IConfigurationRoot Configuration(bool identity, bool linking) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceIdentity:AllowPrivateHttp"] = identity.ToString(),
            ["ServiceLinks:AllowPrivateHttp"] = linking.ToString()
        }).Build();

    private static ServiceProvider Services(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddNetRatelServiceIdentity(configuration);
        services.AddOptions<ServiceLinkOptions>().Bind(configuration.GetSection(ServiceLinkOptions.SectionName));
        return services.BuildServiceProvider();
    }

    private static void RemoveOptIn(IConfigurationRoot configuration)
    {
        configuration["ServiceIdentity:AllowPrivateHttp"] = "false";
        configuration["ServiceLinks:AllowPrivateHttp"] = "false";
        configuration.Reload();
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
