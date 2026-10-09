using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Bootstrap;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.RatelDesk;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RatelDeskReceiverRegistrationTests
{
    [Theory]
    [InlineData("https", 443, ReceiverWireValidation.CapabilitiesPath)]
    [InlineData("http", 80, ReceiverWireValidation.CapabilitiesPath)]
    [InlineData("https", 443, "/api/v1/integrations/netratel/incident-receipts/.")]
    [InlineData("https", 443, "/api/v1/integrations/netratel/incident-receipts/..")]
    public async Task Receiver_client_preserves_the_approved_request_uri_through_the_real_connection_callback(string scheme, int port, string suffix)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddServiceDiscovery();
        services.ConfigureHttpClientDefaults(client => client.AddServiceDiscovery());
        services.AddRatelDeskReceiverAdapter(new ConfigurationBuilder().Build());
        string? observedOriginal = null;
        string? observedAbsolute = null;
        services.AddHttpClient(RatelDeskReceiverHttpPipeline.ManagedClient).ConfigurePrimaryHttpMessageHandler(provider =>
        {
            var handler = RatelDeskReceiverSafeHttpMessageHandler.Create(RatelDeskAuthenticationMode.PairedSystem,
                provider.GetRequiredService<RatelDeskReceiverNetworkPolicy>());
            var connect = handler.ConnectCallback!;
            handler.ConnectCallback = (context, _) =>
            {
                observedOriginal = context.InitialRequestMessage.RequestUri!.OriginalString;
                observedAbsolute = context.InitialRequestMessage.RequestUri.AbsoluteUri;
                return connect(context, new CancellationToken(true));
            };
            return handler;
        });
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(RatelDeskReceiverHttpPipeline.ManagedClient);
        var approved = scheme + "://receiver.example.test/help";
        var endpoint = approved + suffix;
        var destination = suffix == ReceiverWireValidation.CapabilitiesPath ? new Uri(endpoint) :
            new Uri(endpoint, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using var request = new HttpRequestMessage(HttpMethod.Get, destination);
        request.Options.Set(RatelDeskReceiverSafeHttpMessageHandler.ApprovedApiBaseOption, approved);
        var error = await Record.ExceptionAsync(() => client.SendAsync(request, TestContext.Current.CancellationToken));
        Assert.NotNull(error);
        Assert.NotNull(observedOriginal);
        Assert.Equal(endpoint, observedAbsolute);
        // These are synthetic fixed addresses. Production callback cancellation prevents DNS/socket I/O.
        Assert.True(observedOriginal == endpoint,
            $"The actual callback changed the approved URI representation (explicitDefaultPort={observedOriginal == scheme + "://receiver.example.test:" + port + "/help" + suffix}).");
        Assert.DoesNotContain(Flatten(error!), exception => exception is InvalidDataException);
        Assert.Contains(Flatten(error!), exception => exception is OperationCanceledException);

        static IEnumerable<Exception> Flatten(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException) yield return current;
        }
    }

    [Theory]
    [InlineData(RatelDeskReceiverHttpPipeline.ManagedClient)]
    public async Task Receiver_client_removes_global_resilience_replays_without_changing_other_clients(string name)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddServiceDiscovery();
        services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler(options =>
        { options.Retry.Delay = TimeSpan.Zero; options.Retry.UseJitter = false; }));
        services.ConfigureHttpClientDefaults(client => client.AddServiceDiscovery());
        services.AddRatelDeskReceiverAdapter(new ConfigurationBuilder().Build());
        var transport = new RejectingTransport();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => transport);
        var ordinaryTransport = new FailOnceTransport();
        services.AddHttpClient("ordinary-client").ConfigurePrimaryHttpMessageHandler(() => ordinaryTransport);
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        using var response = await client.PostAsync("https://receiver.example/api/v2/integration/incidents",
            new StringContent("{}"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        transport.Calls.Should().Be(1);
        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
        using var ordinaryClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient("ordinary-client");
        using var ordinaryResponse = await ordinaryClient.GetAsync("https://receiver.example/ordinary",
            TestContext.Current.CancellationToken);
        ordinaryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        ordinaryTransport.Calls.Should().Be(2);
        ordinaryTransport.OriginalUri.Should().Be("https://receiver.example:443/ordinary");
    }

    private sealed class RejectingTransport : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class FailOnceTransport : HttpMessageHandler
    {
        public int Calls;
        public string? OriginalUri;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            OriginalUri = request.RequestUri?.OriginalString;
            return Task.FromResult(new HttpResponseMessage(++Calls == 1
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        }
    }
}
