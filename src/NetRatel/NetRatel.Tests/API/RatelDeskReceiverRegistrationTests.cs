using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Bootstrap;
using NetRatel.Infrastructure.RatelDesk;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RatelDeskReceiverRegistrationTests
{
    [Theory]
    [InlineData(RatelDeskReceiverHttpPipeline.ManualClient)]
    [InlineData(RatelDeskReceiverHttpPipeline.ManagedClient)]
    public async Task Receiver_client_removes_global_resilience_replays_without_changing_other_clients(string name)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler(options =>
        { options.Retry.Delay = TimeSpan.Zero; options.Retry.UseJitter = false; }));
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(++Calls == 1
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        }
    }
}
