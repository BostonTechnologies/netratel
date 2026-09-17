using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NetRatel.Web.Services.Telemetry;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class GatewayTelemetryLiveStreamServiceTests
{
    [Fact]
    public async Task SubscribeAsync_ParsesCommentsWhitespaceAndMultilineTelemetryEvent()
    {
        var expected = new GatewayTelemetryLiveEvent(
            "Live", "Live", 7, 11, null, 1000, true, true, true, 1000,
            "0.4.130-rc.1", 3, DateTimeOffset.UtcNow.AddMinutes(1), "interactive-viewer");
        var json = JsonSerializer.Serialize(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var split = json.LastIndexOf(',', json.Length / 2);
        var payload = $"  : keep-alive\n event: telemetry\n data: {json[..(split + 1)]}\n data: {json[(split + 1)..]}\n\n";
        var service = new GatewayTelemetryLiveStreamService(new StaticHttpClientFactory(payload));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscription = service.SubscribeAsync(9, Guid.NewGuid(), 1000, cancellation.Token).GetAsyncEnumerator();

        (await subscription.MoveNextAsync()).Should().BeTrue();
        subscription.Current.State.Should().Be("Connecting");
        (await subscription.MoveNextAsync()).Should().BeTrue();
        subscription.Current.Should().BeEquivalentTo(expected);
    }

    private sealed class StaticHttpClientFactory(string payload) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StreamHandler(payload), disposeHandler: true)
        {
            BaseAddress = new Uri("https://netratel.test")
        };
    }

    private sealed class StreamHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
            });
    }
}
