using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using NetRatel.API.Services.Orchestration;
using NetRatel.Shared.Connectivity;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RuntimeM2MConnectivityServiceTests
{
    [Fact]
    public async Task GetAsync_ResolvesExternalServiceSettingsFromRuntimeOptions()
    {
        var sut = new RuntimeM2MConnectivityService(
            new StubHttpClientFactory(new HttpClient()),
            new StubTokenService("token"),
            Options.Create(new NetRatelExternalServiceCallbackOptions
            {
                BaseUrl = "https://external-service.local/",
                Audience = "external-service.api"
            }));

        var settings = await sut.GetAsync(CancellationToken.None);

        Assert.True(settings.Enabled);
        Assert.Equal("https://external-service.local", settings.RemoteBaseUrl);
        Assert.Equal("external-service.api", settings.RemoteAudience);
        Assert.Equal("ExternalService", settings.RemoteSystemName);
    }

    [Fact]
    public async Task TestAsync_AcquiresTokenAndCallsProtectedExternalServicePing()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"service":"ExternalService.API"}""")
        });
        var tokenService = new StubTokenService("bearer-token");
        var clientFactory = new StubHttpClientFactory(new HttpClient(handler));
        var sut = new RuntimeM2MConnectivityService(
            clientFactory,
            tokenService,
            Options.Create(new NetRatelExternalServiceCallbackOptions
            {
                BaseUrl = "https://external-service.local",
                Audience = "external-service.api"
            }));

        var result = await sut.TestAsync(new M2MConnectivityTestRequestDto(), CancellationToken.None);

        Assert.All(result.Probes, probe => Assert.Equal(TrafficLight.Green, probe.Status));
        Assert.Equal("external-service.api", tokenService.Audiences.Single());
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://external-service.local/api/v1/orchestration/netratel/m2m/ping", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("bearer-token", request.Headers.Authorization?.Parameter);
        Assert.Contains("ConnectivityProbe", clientFactory.Names);
    }

    private sealed class StubTokenService(string token) : INetRatelSystemTokenService
    {
        public List<string> Audiences { get; } = new();

        public Task<string> GetTokenAsync(string audience, CancellationToken ct = default)
        {
            Audiences.Add(audience);
            return Task.FromResult(token);
        }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public List<string> Names { get; } = new();

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return client;
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            return Task.FromResult(_responder(request));
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Headers.Authorization is not null)
            {
                clone.Headers.Authorization = new AuthenticationHeaderValue(
                    request.Headers.Authorization.Scheme,
                    request.Headers.Authorization.Parameter);
            }

            return clone;
        }
    }
}
