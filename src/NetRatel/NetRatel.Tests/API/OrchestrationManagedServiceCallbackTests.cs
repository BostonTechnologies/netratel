using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceLinks;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class OrchestrationManagedServiceCallbackTests
{
    [Fact]
    public async Task Managed_execution_with_forged_local_request_id_never_falls_back_to_legacy_signer_or_http()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        db.Set<ManagedOrchestrationRequestBinding>().Add(new() { RequestId = 12, ExecutionId = "8001", ServicePrincipalId = Guid.NewGuid(), TenantId = 71, AgentId = Guid.NewGuid(), JobDefinitionId = "7001", ParentRequestId = "parent", RequestTaskId = "task", CorrelationId = "corr" });
        await db.SaveChangesAsync();
        var handler = new RecordingHandler(HttpStatusCode.OK); var signer = new RecordingSigner();
        var client = new NetRatelExternalServiceCallbackClient(new HttpClient(handler), signer,
            Options.Create(new NetRatelExternalServiceCallbackOptions { BaseUrl = "https://legacy.example.test", Audience = "legacy" }),
            new NoEvents(), NullLogger<NetRatelExternalServiceCallbackClient>.Instance, db);
        Assert.False(await client.TrySendStatusAsync(new() { NetRatelRequestId = "999", ExecutionId = "8001", RequestId = "parent", RequestTaskId = "task", Status = "Completed" }, "corr"));
        Assert.False(await client.TrySendStatusAsync(new() { ExecutionId = "8001", RequestId = "parent", RequestTaskId = "task", Status = "Completed" }, "corr"));
        Assert.Equal(0, signer.Calls); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Legacy_callback_uses_retained_token_and_actual_provider_route()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent); var signer = new RecordingSigner();
        var client = new NetRatelExternalServiceCallbackClient(new HttpClient(handler), signer,
            Options.Create(new NetRatelExternalServiceCallbackOptions { BaseUrl = "https://legacy.example.test/base", Audience = "legacy" }),
            new NoEvents(), NullLogger<NetRatelExternalServiceCallbackClient>.Instance);
        Assert.True(await client.TrySendStatusAsync(new() { ExecutionId = "8001", RequestTaskId = "task", Status = "Completed" }, "corr"));
        Assert.Equal(1, signer.Calls); Assert.Equal("https://legacy.example.test/base/api/v1/orchestration/provider/callback", handler.LastUrl);
        Assert.Equal("legacy-issued-token", handler.Token); Assert.Equal("corr", handler.Correlation);
    }

    [Fact]
    public async Task Safe_managed_transport_rejects_redirect_and_bounds_unknown_length_response()
    {
        var redirect = new RecordingHandler(HttpStatusCode.TemporaryRedirect);
        var transport = new ServiceLinkTransport(new HttpClient(redirect), Options.Create(new ServiceLinkOptions()));
        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => transport.PostStatusAsync("https://peer.example.test/api/v1/orchestration/provider/callback", new { status = "Completed" }, "corr", "scoped-peer-token", default));
        Assert.Equal("peer-redirect-rejected", error.Code); Assert.Equal(1, redirect.Calls); Assert.Equal("scoped-peer-token", redirect.Token);
        var body = new RecordingHandler(HttpStatusCode.OK, new string('x', 5000));
        transport = new ServiceLinkTransport(new HttpClient(body), Options.Create(new ServiceLinkOptions { MaximumPayloadBytes = 4096 }));
        error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => transport.PostStatusAsync("https://peer.example.test/api/v1/orchestration/provider/callback", new { status = "Completed" }, "corr", "scoped-peer-token", default));
        Assert.Equal("peer-response-too-large", error.Code);
    }

    private sealed class RecordingSigner : INetRatelSystemTokenService
    { public int Calls; public Task<string> GetTokenAsync(string audience, CancellationToken ct = default) { Calls++; return Task.FromResult("legacy-issued-token"); } }
    private sealed class NoEvents : IEventRecorder { public Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default) => Task.CompletedTask; }
    private sealed class RecordingHandler(HttpStatusCode status, string? body = null) : HttpMessageHandler
    {
        public int Calls; public string? LastUrl; public string? Token; public string? Correlation;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; LastUrl = request.RequestUri!.AbsoluteUri; Token = request.Headers.Authorization?.Parameter; Correlation = request.Headers.GetValues("X-Correlation-Id").Single();
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.TemporaryRedirect) response.Headers.Location = new Uri("https://different.example.test/");
            if (body is not null) response.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)));
            return Task.FromResult(response);
        }
    }
}
