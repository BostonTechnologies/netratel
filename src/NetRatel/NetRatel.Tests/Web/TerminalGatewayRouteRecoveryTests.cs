using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using NetRatel.Shared.Contracts.Terminals;
using NetRatel.Web.Services.Authentication;
using NetRatel.Web.Services.Terminal;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class TerminalGatewayRouteRecoveryTests
{
    [Fact]
    public async Task Input_for_an_unknown_session_uses_only_the_gateway_route()
    {
        var handler = new TerminalRouteHandler(HttpStatusCode.OK);
        using var host = CreateService(handler);

        await host.Service.SendInputAsync("gateway-session", "ignored");

        handler.Requests.Should().ContainSingle("POST /api/v2/gateway-terminal/gateway-session/stdin");
    }

    [Fact]
    public async Task Temporary_gateway_route_failure_is_returned_without_a_route_probe_or_fallback()
    {
        var handler = new TerminalRouteHandler(HttpStatusCode.ServiceUnavailable);
        using var host = CreateService(handler);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => host.Service.SendInputAsync("gateway-session", "ignored"));

        failure.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        handler.Requests.Should().ContainSingle("POST /api/v2/gateway-terminal/gateway-session/stdin");
    }

    [Fact]
    public async Task Missing_gateway_session_is_not_redirected_to_a_v1_session()
    {
        var handler = new TerminalRouteHandler(HttpStatusCode.NotFound);
        using var host = CreateService(handler);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => host.Service.SendInputAsync("legacy-session", "ignored"));

        failure.StatusCode.Should().Be(HttpStatusCode.NotFound);
        handler.Requests.Should().ContainSingle("POST /api/v2/gateway-terminal/legacy-session/stdin");
        handler.Requests.Should().NotContain(request => request.Contains("/api/v1/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Persisted_gateway_handle_lookup_never_falls_back_to_v1()
    {
        var handler = new TerminalRouteHandler(HttpStatusCode.NotFound);
        using var host = CreateService(handler);

        var session = await host.Service.GetGatewaySessionAsync("former-gateway-session");

        session.Should().BeNull();
        handler.Requests.Should().ContainSingle("GET /api/v2/gateway-terminal/former-gateway-session");
        handler.Requests.Should().NotContain(request => request.Contains("/api/v1/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Browser_attachment_heartbeat_posts_exact_generation_and_attachment_fences_directly_to_v2()
    {
        var handler = new TerminalRouteHandler(HttpStatusCode.OK);
        using var host = CreateService(handler);

        await host.Service.RenewGatewayAttachmentAsync(
            "gateway-session",
            7,
            "lease-7",
            "browser-7",
            claimOwnership: true);

        handler.Requests.Should().ContainSingle("POST /api/v2/gateway-terminal/gateway-session/attachment/renew");
        handler.RequestBodies.Should().ContainSingle().Which.Should().Contain("\"generation\":7");
        handler.RequestBodies.Single().Should().Contain("\"attachmentLeaseId\":\"lease-7\"");
        handler.RequestBodies.Single().Should().Contain("\"browserAttachmentId\":\"browser-7\"");
        handler.RequestBodies.Single().Should().Contain("\"claimOwnership\":true");
        handler.Requests.Should().NotContain(request => request.Contains("/api/v1/", StringComparison.Ordinal));
    }

    private static TerminalServiceHost CreateService(HttpMessageHandler handler) => new(handler);

    private sealed class TerminalServiceHost : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly IServiceScope _scope;

        public TerminalServiceHost(HttpMessageHandler handler)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHttpClientFactory>(new StaticHttpClientFactory(handler));
            services.AddHttpContextAccessor();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton<ITokenService, StubTokenService>();
            services.AddScoped<OperatorApiCredentialState>();
            services.AddScoped<OperatorApiCredentialProvider>();
            _services = services.BuildServiceProvider(validateScopes: true);
            _scope = _services.CreateScope();
            Service = new TerminalService(
                _scope.ServiceProvider.GetRequiredService<IHttpClientFactory>(),
                _scope.ServiceProvider.GetRequiredService<OperatorApiCredentialProvider>(),
                NullLogger<TerminalService>.Instance);
        }

        public TerminalService Service { get; }

        public void Dispose()
        {
            _scope.Dispose();
            _services.Dispose();
        }
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://netratel.test")
        };
    }

    private sealed class TerminalRouteHandler(HttpStatusCode gatewayLookupStatus) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add($"{request.Method.Method} {path}");
            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/api/v2/gateway-terminal/", StringComparison.Ordinal))
            {
                return gatewayLookupStatus == HttpStatusCode.OK
                    ? JsonResponse(new TerminalSessionDto(
                        "gateway-session",
                        "agent",
                        "bash",
                        "active",
                        true,
                        1,
                        1,
                        null))
                    : new HttpResponseMessage(gatewayLookupStatus);
            }

            if (request.Method == HttpMethod.Post && path == "/api/v2/gateway-terminal/gateway-session/stdin")
            {
                return gatewayLookupStatus == HttpStatusCode.OK
                    ? JsonResponse(new TerminalActionResponse("track", "queued", "gateway-session"))
                    : new HttpResponseMessage(gatewayLookupStatus);
            }

            if (request.Method == HttpMethod.Post && path == "/api/v2/gateway-terminal/gateway-session/attachment/renew")
            {
                return JsonResponse(new TerminalActionResponse("track", "renewed", "gateway-session")
                {
                    AttachmentLeaseId = "lease-7"
                });
            }

            if (request.Method == HttpMethod.Post && path == "/api/v2/gateway-terminal/legacy-session/stdin")
            {
                return new HttpResponseMessage(gatewayLookupStatus);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse<T>(T value) =>
            new(HttpStatusCode.Accepted)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
            };
    }

    private sealed class StubTokenService : ITokenService
    {
        public Task<string> GetValidAccessTokenAsync() => Task.FromResult("token");
    }
}
