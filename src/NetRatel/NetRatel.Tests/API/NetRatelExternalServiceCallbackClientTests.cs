using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.Services;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class NetRatelExternalServiceCallbackClientTests
{
    [Fact]
    public async Task Saved_scoped_callback_sends_bearer_and_original_correlation_to_the_fixed_provider_route()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.NoContent);
        Assert.True(await fixture.Client.TrySendStatusAsync(fixture.Request, "corr-paired"));
        var request = Assert.Single(fixture.Http.Callbacks);
        Assert.Equal("https://peer.example.test/api/v1/orchestration/provider/callback", request.Url);
        Assert.Equal("Bearer", request.Scheme); Assert.Equal("scoped-access-token", request.Token);
        Assert.Equal("corr-paired", request.Correlation);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("8001", body.RootElement.GetProperty("executionId").GetString());
        Assert.Equal("parent-task", body.RootElement.GetProperty("requestTaskId").GetString());
        Assert.DoesNotContain("owning-layer-fixture-secret", request.Body);
    }

    [Fact]
    public async Task Retry_after_5xx_keeps_the_same_correlated_body_and_rechecks_current_authority()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.NoContent);
        Assert.True(await fixture.Client.TrySendStatusAsync(fixture.Request, "corr-paired"));
        Assert.Equal(3, fixture.Http.Callbacks.Count); Assert.Equal(3, fixture.Http.TokenRequests);
        Assert.All(fixture.Http.Callbacks, request => { Assert.Equal(fixture.Http.Callbacks[0].Body, request.Body); Assert.Equal("corr-paired", request.Correlation); });
    }

    [Fact]
    public async Task Forbidden_callback_stops_retry_and_records_one_safe_correlated_rejection()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.Forbidden);
        Assert.False(await fixture.Client.TrySendStatusAsync(fixture.Request, "corr-paired"));
        Assert.Single(fixture.Http.Callbacks);
        var recorded = Assert.Single(fixture.Events.Values);
        Assert.Equal("DomainEvent.Orchestration.ExternalServiceCallbackRejected", recorded.EventType);
        Assert.Equal("corr-paired", recorded.CorrelationId); Assert.Equal("Warning", recorded.Severity);
        var safe = JsonSerializer.Serialize(recorded);
        Assert.DoesNotContain("owning-layer-fixture-secret", safe); Assert.DoesNotContain("scoped-access-token", safe);
    }

    [Fact]
    public async Task Capability_removed_during_token_exchange_never_sends_the_callback()
    {
        await using var fixture = await Fixture.CreateAsync(HttpStatusCode.NoContent);
        fixture.Http.BeforeToken = async () =>
        {
            (await fixture.Db.Set<PairingConnectionRecord>().SingleAsync()).Active = false;
            await fixture.Db.SaveChangesAsync();
        };
        Assert.False(await fixture.Client.TrySendStatusAsync(fixture.Request, "corr-paired"));
        Assert.Equal(1, fixture.Http.TokenRequests); Assert.Empty(fixture.Http.Callbacks);
        Assert.Equal("DomainEvent.Orchestration.ExternalServiceCallbackRejected", Assert.Single(fixture.Events.Values).EventType);
    }

    private sealed class Fixture(OrchestratorDbContext db, NetRatelIdentityDbContext identity,
        NetRatelExternalServiceCallbackClient client, NetRatelExternalServiceCallbackRequest request, Handler http, Events events) : IAsyncDisposable
    {
        internal OrchestratorDbContext Db => db;
        internal NetRatelExternalServiceCallbackClient Client => client;
        internal NetRatelExternalServiceCallbackRequest Request => request;
        internal Handler Http => http;
        internal Events Events => events;
        internal static async Task<Fixture> CreateAsync(params HttpStatusCode[] statuses)
        {
            var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
            var identity = new NetRatelIdentityDbContext(new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
            await PairingBusinessAuthorityFixture.SeedAdministratorAsync(identity);
            var agent = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            db.Tenants.Add(new() { Id = 71, Name = "Permitted tenant", CreatedAtUtc = now, UpdatedAtUtc = now });
            db.Agents.Add(new() { Id = agent, TenantId = 71, Status = AgentStatus.Active, CreatedAtUtc = now });
            db.Jobs.Add(new() { Id = 7001, TenantId = 71, AgentId = agent, CreatedAtUtc = now, UpdatedAtUtc = now }); await db.SaveChangesAsync();
            var options = new Monitor(); var authority = new PairingAuthority(new PairingBusinessAuthorityFixture.Access(), identity, db);
            var registry = new ServicePrincipalRegistry(db, new RuntimeOptions(options), options, new EmptyServiceClientDeploymentCatalog(), authority, TimeProvider.System);
            var protection = new EphemeralDataProtectionProvider();
            var credential = await PairingBusinessAuthorityFixture.CreateAsync(db, registry, 71, [ServiceIdentityScopes.OrchestrationInvoke], protection: protection);
            var requests = new RequestService(db); var runs = new JobRunService(db);
            var source = "service:" + credential.Principal.Id.ToString("N");
            var stored = await requests.CreateAsync(new(source, agent.ToString("D"), "7001", "{}", 71, agent));
            await requests.UpdateAsync(new(stored.Id, null, null, null, "8001", "Processing", null, null, null, null));
            await runs.UpsertRunAsync(new(8001, 7001, 71, agent.ToString("D"), source, JobRunState.Running, 0, now, now, null, null, "{}", null, agent));
            db.Add(new ManagedOrchestrationRequestBinding { RequestId = stored.Id, ServicePrincipalId = credential.Principal.Id,
                LinkId = credential.Principal.LinkId, LinkRevision = credential.Principal.LinkRevision, GrantHash = credential.Principal.GrantHash,
                PeerInstanceId = credential.Principal.PeerInstanceId, PeerTenantId = credential.Principal.PeerTenantId,
                TenantId = 71, AgentId = agent, JobDefinitionId = "7001", ExecutionId = "8001", ParentRequestId = "parent-request", RequestTaskId = "parent-task",
                CorrelationId = "corr-paired", CallbackUrl = "https://peer.example.test/api/v1/orchestration/provider/callback" }); await db.SaveChangesAsync();
            var http = new Handler(statuses); var transport = new PairingTransport(new HttpClient(http));
            var identities = new InstallationIdentityStore(db, new FlowSource(), options);
            var profiles = new PairingBusinessProfileService(db, protection, authority, identities, transport);
            var events = new Events();
            var client = new NetRatelExternalServiceCallbackClient(db, profiles, requests, runs, transport, registry, events, NullLogger<NetRatelExternalServiceCallbackClient>.Instance);
            var request = new NetRatelExternalServiceCallbackRequest { NetRatelRequestId = stored.Id.ToString(), NetRatelRunId = "8001", RequestId = stored.Id.ToString(), RequestTaskId = "parent-task", ExecutionId = "8001", Status = "running" };
            return new(db, identity, client, request, http, events);
        }
        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await identity.DisposeAsync(); }
    }
    private sealed class Monitor : IOptionsMonitor<ServiceIdentityOptions>
    { public ServiceIdentityOptions CurrentValue => new() { Enabled = true, InstanceId = "c0c8e681-b1d0-4e44-92c2-50dce9d0c2ce" }; public ServiceIdentityOptions Get(string? name) => CurrentValue; public IDisposable? OnChange(Action<ServiceIdentityOptions, string?> listener) => null; }
    private sealed class RuntimeOptions(Monitor options) : IServiceIdentityRuntimeOptions
    { public Task<ServiceIdentityOptions> GetAsync(CancellationToken ct = default) => Task.FromResult(options.CurrentValue); }
    private sealed class FlowSource : IFlowSourceIdentityResolver
    { public Task<Guid> EnsureAsync(CancellationToken ct) => Task.FromResult(Guid.Parse("00000000-0000-4000-8000-000000000075")); }
    private sealed class Events : IEventRecorder
    { internal List<DomainEvent> Values { get; } = []; public Task RecordAsync(DomainEvent value, CancellationToken ct = default) { Values.Add(value); return Task.CompletedTask; } }
    private sealed record Sent(string Url, string? Scheme, string? Token, string Correlation, string Body);
    private sealed class Handler(HttpStatusCode[] statuses) : HttpMessageHandler
    {
        internal int TokenRequests;
        internal List<Sent> Callbacks { get; } = [];
        internal Func<Task>? BeforeToken { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/connect/token")
            {
                TokenRequests++; if (BeforeToken is not null) await BeforeToken();
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"scoped-access-token\"}") };
            }
            var index = Callbacks.Count;
            Callbacks.Add(new(request.RequestUri.AbsoluteUri, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
                request.Headers.GetValues("X-Correlation-Id").Single(), await request.Content!.ReadAsStringAsync(ct)));
            return new(statuses[Math.Min(index, statuses.Length - 1)]);
        }
    }
}
