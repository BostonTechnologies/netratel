using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRatel.Application.Events;
using NetRatel.API.Services.Orchestration;
using Xunit;

namespace NetRatel.Tests.API;

public class NetRatelExternalServiceCallbackClientTests
{
    [Fact]
    public async Task SendStatusAsync_SendsBearerJwtAndCorrelationHeader()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://external-service.local") };
        var token = CreateToken("external-service.api");
        var tokenService = new StubTokenService(token);
        var options = Options.Create(new NetRatelExternalServiceCallbackOptions
        {
            BaseUrl = "https://external-service.local",
            Audience = "external-service.api",
            ClientId = "netratel.api"
        });
        var sut = new NetRatelExternalServiceCallbackClient(
            httpClient,
            tokenService,
            options,
            new RecordingEventRecorder(),
            NullLogger<NetRatelExternalServiceCallbackClient>.Instance);

        await sut.SendStatusAsync(
            new NetRatelExternalServiceCallbackRequest
            {
                RequestTaskId = Guid.NewGuid().ToString("N"),
                ExecutionId = "run-1",
                Status = "running"
            },
            "corr-123");

        Assert.Single(handler.Requests);
        var request = handler.Requests[0];
        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.True(request.Headers.Contains("X-Correlation-Id"));
        Assert.Equal("corr-123", request.Headers.GetValues("X-Correlation-Id").Single());

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(request.Headers.Authorization.Parameter);
        Assert.Equal("external-service.api", jwt.Audiences.Single());
    }

    [Fact]
    public async Task SendStatusAsync_Retries_On5xx()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new(HttpStatusCode.InternalServerError),
            new(HttpStatusCode.BadGateway),
            new(HttpStatusCode.NoContent)
        ]);
        var handler = new RecordingHandler(_ => responses.Dequeue());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://external-service.local") };
        var sut = new NetRatelExternalServiceCallbackClient(
            httpClient,
            new StubTokenService(CreateToken("external-service.api")),
            Options.Create(new NetRatelExternalServiceCallbackOptions { BaseUrl = "https://external-service.local", Audience = "external-service.api" }),
            new RecordingEventRecorder(),
            NullLogger<NetRatelExternalServiceCallbackClient>.Instance);

        await sut.SendStatusAsync(
            new NetRatelExternalServiceCallbackRequest
            {
                RequestTaskId = Guid.NewGuid().ToString("N"),
                ExecutionId = "run-2",
                Status = "failed",
                Message = "failure"
            },
            "corr-500");

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task SendStatusAsync_DoesNotRetry_OnForbidden()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://external-service.local") };
        var sut = new NetRatelExternalServiceCallbackClient(
            httpClient,
            new StubTokenService(CreateToken("external-service.api")),
            Options.Create(new NetRatelExternalServiceCallbackOptions { BaseUrl = "https://external-service.local", Audience = "external-service.api" }),
            new RecordingEventRecorder(),
            NullLogger<NetRatelExternalServiceCallbackClient>.Instance);

        await sut.SendStatusAsync(
            new NetRatelExternalServiceCallbackRequest
            {
                RequestTaskId = Guid.NewGuid().ToString("N"),
                ExecutionId = "run-3",
                Status = "succeeded"
            },
            "corr-403");

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SendStatusAsync_RecordsRejectedEvent_OnForbidden()
    {
        var events = new RecordingEventRecorder();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("forbidden")
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://external-service.local") };
        var sut = new NetRatelExternalServiceCallbackClient(
            httpClient,
            new StubTokenService(CreateToken("external-service.api")),
            Options.Create(new NetRatelExternalServiceCallbackOptions { BaseUrl = "https://external-service.local", Audience = "external-service.api" }),
            events,
            NullLogger<NetRatelExternalServiceCallbackClient>.Instance);

        await sut.SendStatusAsync(
            new NetRatelExternalServiceCallbackRequest
            {
                RequestTaskId = Guid.NewGuid().ToString("N"),
                ExecutionId = "run-4",
                Status = "failed"
            },
            "corr-403");

        var recorded = Assert.Single(events.Events);
        Assert.Equal("DomainEvent.Orchestration.ExternalServiceCallbackRejected", recorded.EventType);
        Assert.Equal("Warning", recorded.Severity);
        Assert.Equal("corr-403", recorded.CorrelationId);
    }

    private static string CreateToken(string audience)
    {
        var key = new SymmetricSecurityKey(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTimeOffset.UtcNow;
        var token = new JwtSecurityToken(
            issuer: "https://netratel.example.invalid",
            audience: audience,
            claims:
            [
                new Claim("azp", "netratel.api"),
                new Claim("client_id", "netratel.api")
            ],
            notBefore: now.AddMinutes(-1).UtcDateTime,
            expires: now.AddMinutes(5).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class StubTokenService(string token) : INetRatelSystemTokenService
    {
        public Task<string> GetTokenAsync(string audience, CancellationToken ct = default) => Task.FromResult(token);
    }

    private sealed class RecordingEventRecorder : IEventRecorder
    {
        public List<DomainEvent> Events { get; } = new();

        public Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default)
        {
            Events.Add(domainEvent);
            return Task.CompletedTask;
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

            return clone;
        }
    }
}
