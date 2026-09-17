using System.Net;
using System.Text.Json;
using FluentAssertions;
using NetRatel.Web.Services.Clients;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class GatewayClientActionApiServiceTests
{
    [Fact]
    public async Task Ping_Uses_The_Agent_Id_Keyed_V2_Route()
    {
        var agentId = Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46");
        var factory = new RecordingHttpClientFactory(agentId);
        var service = new GatewayClientActionApiService(factory);

        var result = await service.PingAsync(3, agentId);

        result.AgentId.Should().Be(agentId);
        factory.Request.Should().Be((HttpMethod.Post, $"/api/v2/agents/3/{agentId:D}/ping"));
    }

    private sealed class RecordingHttpClientFactory(Guid agentId) : IHttpClientFactory
    {
        public (HttpMethod Method, string Path)? Request { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(this, agentId)) { BaseAddress = new Uri("https://netratel.test") };

        private sealed class Handler(RecordingHttpClientFactory owner, Guid agentId) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Request = (request.Method, request.RequestUri!.AbsolutePath);
                var payload = new GatewayPingResult(3, agentId, Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow, 12.5, "akka-gateway");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                });
            }
        }
    }
}
