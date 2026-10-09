using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;
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
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var client = new NetRatelExternalServiceCallbackClient(db, null!, null!, null!, new PairingTransport(new HttpClient(handler)), null!,
            new NoEvents(), NullLogger<NetRatelExternalServiceCallbackClient>.Instance);
        Assert.False(await client.TrySendStatusAsync(new() { NetRatelRequestId = "999", ExecutionId = "8001", RequestId = "parent", RequestTaskId = "task", Status = "Completed" }, "corr"));
        Assert.False(await client.TrySendStatusAsync(new() { ExecutionId = "8001", RequestId = "parent", RequestTaskId = "task", Status = "Completed" }, "corr"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Paired_callback_uses_only_the_fixed_route_and_does_not_follow_redirects_or_read_response_bodies()
    {
        var redirect = new RecordingHandler(HttpStatusCode.TemporaryRedirect);
        var transport = new PairingTransport(new HttpClient(redirect));
        Assert.Equal(307, await transport.PostBusinessStatusAsync("https://peer.example.test", "/api/v1/orchestration/provider/callback",
            new { status = "Completed" }, "scoped-peer-token", "corr", default));
        Assert.Equal(1, redirect.Calls); Assert.Equal("scoped-peer-token", redirect.Token); Assert.Equal("corr", redirect.Correlation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.PostBusinessStatusAsync("https://peer.example.test",
            "/attacker/callback", new { status = "Completed" }, "scoped-peer-token", "corr", default));
        Assert.Equal(1, redirect.Calls);
        var body = new RecordingHandler(HttpStatusCode.NoContent, new string('x', 100000));
        transport = new PairingTransport(new HttpClient(body));
        Assert.Equal(204, await transport.PostBusinessStatusAsync("https://peer.example.test", "/api/v1/orchestration/provider/callback",
            new { status = "Completed" }, "scoped-peer-token", "corr", default));
    }

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
