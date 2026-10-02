using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Web.Services.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientServicesApiServiceTests
{
    private static readonly Guid AgentId = Guid.Parse("663305b5-7fb0-40a3-bea4-1a270a78e134");

    [Fact]
    public async Task Reads_Cached_Model_And_Refresh_Status_At_Exact_Authenticated_Client_Routes()
    {
        var factory = new Factory(request => request.Method == HttpMethod.Get
            ? new(HttpStatusCode.OK) { Content = JsonContent.Create(Model()) }
            : new(HttpStatusCode.OK) { Content = JsonContent.Create(new ClientServicesRefreshResponse(ClientServicesRefreshStatus.Throttled, null, DateTimeOffset.UtcNow.AddSeconds(15))) });
        var api = new ClientServicesApiService(factory);
        var model = await api.GetAsync(3, AgentId);
        model.Should().NotBeNull();
        model!.AgentId.Should().Be(AgentId);
        var refresh = await api.RefreshAsync(3, AgentId);
        refresh.Status.Should().Be(ClientServicesRefreshStatus.Throttled);
        factory.Requests.Should().Equal($"GET /api/v2/agents/3/{AgentId:D}/services", $"POST /api/v2/agents/3/{AgentId:D}/services/refresh");
    }

    [Fact]
    public async Task Denied_Live_Admission_Stops_Instead_Of_Retrying_Or_Disclosing_A_Model()
    {
        var factory = new Factory(_ => new(HttpStatusCode.Forbidden));
        var live = new ClientServicesLiveStreamService(factory);
        var events = new List<ClientServicesLiveEvent>();
        await foreach (var item in live.SubscribeAsync(3, AgentId, CancellationToken.None)) events.Add(item);
        events.Select(item => item.State).Should().Equal("Connecting", "Unavailable");
        events.Should().OnlyContain(item => item.Snapshot == null);
        factory.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Live_Stream_Accepts_Empty_Cache_And_Ignores_Wrong_Tenant_Events()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var payload = $"event:services\ndata:{JsonSerializer.Serialize(Model() with { TenantId = 99 }, jsonOptions)}\n\n" +
                      $"event:services\ndata:{JsonSerializer.Serialize(Model(), jsonOptions)}\n\n";
        var factory = new Factory(_ => new(HttpStatusCode.OK) { Content = new StringContent(payload) });
        var live = new ClientServicesLiveStreamService(factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var events = live.SubscribeAsync(3, AgentId, cancellation.Token).GetAsyncEnumerator();
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.State.Should().Be("Connecting");
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.State.Should().Be("Live");
        events.Current.Snapshot!.TenantId.Should().Be(3);
        events.Current.Snapshot.LastCompleteInventory.Should().BeNull();
    }

    private static ClientServicesReadModelDto Model() => new(3, AgentId, null, null, [], [], 0, false, false, DateTimeOffset.UtcNow);

    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public List<string> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(new Handler(this, respond)) { BaseAddress = new Uri("https://fixture.test") };
        private sealed class Handler(Factory factory, Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                factory.Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
                return Task.FromResult(respond(request));
            }
        }
    }
}
