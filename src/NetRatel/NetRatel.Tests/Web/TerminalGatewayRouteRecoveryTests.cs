using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Shared.Contracts.Terminals;
using NetRatel.Web.Services.Authentication;
using NetRatel.Web.Services.Terminal;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class TerminalGatewayRouteRecoveryTests
{
    [Fact]
    public async Task Fresh_service_resolves_an_existing_gateway_session_before_using_v1()
    {
        var handler = new TerminalRouteHandler(HttpStatusCode.OK);
        var service = CreateService(handler);

        await service.SendInputAsync("gateway-session", "ignored");

        handler.Requests.Should().ContainInOrder(
            "GET /api/v2/gateway-terminal/gateway-session",
            "POST /api/v2/gateway-terminal/gateway-session/stdin");
        handler.Requests.Should().NotContain(request => request.Contains("/api/v1/terminal/gateway-session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Temporary_gateway_route_failure_does_not_fall_back_to_v1()
    {
        var handler = new TerminalRouteHandler(HttpStatusCode.ServiceUnavailable);
        var service = CreateService(handler);

        await service.SendInputAsync("gateway-session", "ignored");

        handler.Requests.Should().ContainInOrder(
            "GET /api/v2/gateway-terminal/gateway-session",
            "POST /api/v2/gateway-terminal/gateway-session/stdin");
        handler.Requests.Should().NotContain(request => request.Contains("/api/v1/terminal/gateway-session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V1_is_used_only_after_gateway_session_lookup_reports_not_found()
    {
        var handler = new TerminalRouteHandler(HttpStatusCode.NotFound);
        var service = CreateService(handler);

        await service.SendInputAsync("legacy-session", "ignored");

        handler.Requests.Should().ContainInOrder(
            "GET /api/v2/gateway-terminal/legacy-session",
            "POST /api/v1/terminal/legacy-session/stdin");
    }

    [Fact]
    public async Task Persisted_gateway_handle_lookup_never_falls_back_to_v1()
    {
        var handler = new TerminalRouteHandler(HttpStatusCode.NotFound);
        var service = CreateService(handler);

        var session = await service.GetGatewaySessionAsync("former-gateway-session");

        session.Should().BeNull();
        handler.Requests.Should().ContainSingle("GET /api/v2/gateway-terminal/former-gateway-session");
        handler.Requests.Should().NotContain(request => request.Contains("/api/v1/terminal/former-gateway-session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Browser_attachment_heartbeat_posts_exact_generation_and_attachment_fences_directly_to_v2()
    {
        var handler = new TerminalRouteHandler(HttpStatusCode.OK);
        var service = CreateService(handler);

        await service.RenewGatewayAttachmentAsync(
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
        handler.Requests.Should().NotContain(request => request.Contains("/api/v1/terminal/gateway-session", StringComparison.Ordinal));
    }

    private static TerminalService CreateService(HttpMessageHandler handler) =>
        new(
            new StaticHttpClientFactory(handler),
            new StubTokenService(),
            NullLogger<TerminalService>.Instance);

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
                return JsonResponse(new TerminalActionResponse("track", "queued", "gateway-session"));
            }

            if (request.Method == HttpMethod.Post && path == "/api/v2/gateway-terminal/gateway-session/attachment/renew")
            {
                return JsonResponse(new TerminalActionResponse("track", "renewed", "gateway-session")
                {
                    AttachmentLeaseId = "lease-7"
                });
            }

            if (request.Method == HttpMethod.Post && path == "/api/v1/terminal/legacy-session/stdin")
            {
                return JsonResponse(new TerminalActionResponse("track", "queued", "legacy-session"));
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
