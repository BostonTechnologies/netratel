using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.API.Services;
using NetRatel.API.Services.Events;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Events;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using NetRatel.Shared.Contracts;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class DevelopmentMcpClientObservabilityEndpointTests
{
    [Fact]
    public async Task LogRoutes_RequireAnEligibleTarget_AndBoundedOperationsAreAudited()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/logs";

        var sources = await client.GetAsync($"{root}/sources");
        var history = await client.GetAsync($"{root}/history?sourceId=netratel-runtime&pageSize=10&text=health");
        var search = await client.GetAsync($"{root}/search?sourceId=netratel-runtime&text=health");
        var tail = await client.GetAsync($"{root}/tail?sourceId=netratel-runtime&windowSeconds=1&maxRecords=1");

        sources.StatusCode.Should().Be(HttpStatusCode.OK);
        sources.Headers.Should().Contain(header => header.Key == "X-Development-Operation-Audit-Id");
        history.StatusCode.Should().Be(HttpStatusCode.OK);
        (await history.Content.ReadAsStringAsync()).Should().Contain("history-record");
        search.StatusCode.Should().Be(HttpStatusCode.OK);
        (await search.Content.ReadAsStringAsync()).Should().Contain("history-record");
        tail.StatusCode.Should().Be(HttpStatusCode.OK);
        (await tail.Content.ReadAsStringAsync()).Should().Contain("tail-record");
        app.Services.GetRequiredService<TestDispatcher>().Operations.Should().Equal(LogQueryOperation.History, LogQueryOperation.History, LogQueryOperation.StartLive, LogQueryOperation.StopLive);
        app.Services.GetRequiredService<TestTargetAuthority>().AcceptedOperations.Should().Equal(
            DevelopmentOperatorOperation.ClientLogRead,
            DevelopmentOperatorOperation.ClientLogRead,
            DevelopmentOperatorOperation.ClientLogRead,
            DevelopmentOperatorOperation.ClientLogRead);

        app.Services.GetRequiredService<TestTargetAuthority>().Enabled = false;
        var denied = await client.GetAsync($"{root}/sources");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).Should().Contain("target_not_authorized");
    }

    [Fact]
    public async Task Resync_RefreshesBoundedHistory_AndAuditsTheConfirmedTargetAction()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        app.Services.GetRequiredService<TestDispatcher>().HistoryResyncRequired = true;
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/logs";

        var response = await client.PostAsync($"{root}/resync", new StringContent("{\"sourceId\":\"netratel-runtime\"}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("resyncCompleted").And.Contain("resyncGuidance").And.Contain("resyncObserved").And.Contain("history-record");
        content.Should().Contain("\"resyncObserved\":true").And.Contain("\"resyncRequired\":false").And.Contain("\"resyncCompleted\":true");
        app.Services.GetRequiredService<TestDispatcher>().Operations.Should().Equal(LogQueryOperation.History);
        app.Services.GetRequiredService<TestTargetAuthority>().AcceptedOperations.Should().Equal(DevelopmentOperatorOperation.ClientLogResync);
    }

    [Fact]
    public async Task TelemetryRoutes_ReturnOnlyAcceptedSnapshots_AndAuditTheRead()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/telemetry";

        var snapshot = await client.GetAsync($"{root}/snapshot");
        var window = await client.GetAsync($"{root}/stream-window?windowSeconds=1&maxSamples=1");

        snapshot.StatusCode.Should().Be(HttpStatusCode.OK);
        (await snapshot.Content.ReadAsStringAsync()).Should().Contain("akka-shadow");
        window.StatusCode.Should().Be(HttpStatusCode.OK);
        (await window.Content.ReadAsStringAsync()).Should().Contain("samples");
        app.Services.GetRequiredService<TestTargetAuthority>().AcceptedOperations.Should().Equal(
            DevelopmentOperatorOperation.ClientTelemetryRead,
            DevelopmentOperatorOperation.ClientTelemetryRead);
    }

    [Fact]
    public async Task ObservabilityRoutes_RejectInvalidOrOpenRequestsBeforeDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var logs = $"/api/v2/development/mcp/agents/3/{agentId:D}/logs";
        var telemetry = $"/api/v2/development/mcp/agents/3/{agentId:D}/telemetry";

        var invalidLog = await client.GetAsync($"{logs}/history?sourceId=netratel-runtime&pageSize=101");
        var invalidSearch = await client.GetAsync($"{logs}/search?sourceId=netratel-runtime");
        var invalidTail = await client.GetAsync($"{logs}/tail?sourceId=netratel-runtime&windowSeconds=16");
        var invalidTelemetry = await client.GetAsync($"{telemetry}/stream-window?maxSamples=21");

        invalidLog.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        invalidSearch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        invalidTail.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        invalidTelemetry.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        app.Services.GetRequiredService<TestDispatcher>().Operations.Should().BeEmpty();
        app.Services.GetRequiredService<TestTargetAuthority>().AcceptedOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task History_PropagatesAStructuredGatewayCursorRejection()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        app.Services.GetRequiredService<TestDispatcher>().HistoryErrorCode = "invalid_cursor";
        var client = AuthorizedClient(app);

        var response = await client.GetAsync($"/api/v2/development/mcp/agents/3/{agentId:D}/logs/history?sourceId=netratel-runtime&cursor=invalid");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_cursor");
    }

    [Fact]
    public async Task LogRoutes_RejectUnknownSourcesBeforeGatewayDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/logs";

        var history = await client.GetAsync($"{root}/history?sourceId=not-a-source");
        var tail = await client.GetAsync($"{root}/tail?sourceId=not-a-source&windowSeconds=1&maxRecords=1");

        history.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await history.Content.ReadAsStringAsync()).Should().Contain("invalid_source");
        tail.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await tail.Content.ReadAsStringAsync()).Should().Contain("invalid_source");
        app.Services.GetRequiredService<TestDispatcher>().Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task OptionalGatewayRoutes_StartAndFailClosedWhenTheirServicesAreUnavailable()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeOptionalGateways: false);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}";

        var logs = await client.GetAsync($"{root}/logs/sources");
        var telemetry = await client.GetAsync($"{root}/telemetry/snapshot");

        logs.StatusCode.Should().Be(HttpStatusCode.NotFound);
        telemetry.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static HttpClient AuthorizedClient(IHost app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        return client;
    }

    private static async Task<IHost> BuildAppAsync(Guid agentId, bool includeOptionalGateways = true)
    {
        var client = new ClientKey(3, agentId);
        var options = new NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            LogGatewayEnabled = true,
            LogAuthorityEnabled = true,
            TelemetryShadowEnabled = true,
            TelemetryAuthorityEnabled = true
        };
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Development);
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddHttpContextAccessor();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.AddAuthorization(policyOptions => policyOptions.AddPolicy("Operator", policy => policy.RequireAuthenticatedUser()));
                services.AddSingleton(options);
                services.AddScoped<McpOperatorClientObservabilityService>();
                if (includeOptionalGateways)
                {
                    services.AddSingleton(new TestLogRegistry(client));
                    services.AddSingleton<IAgentLogGatewaySessionRegistry>(provider => provider.GetRequiredService<TestLogRegistry>());
                    services.AddSingleton(new TestDispatcher());
                    services.AddSingleton<IAgentLogGatewayQueryDispatcher>(provider => provider.GetRequiredService<TestDispatcher>());
                    services.AddSingleton<IClientTelemetryRouter>(new TestTelemetryRouter(client));
                    services.AddSingleton<IGatewayTelemetryLiveRegistry, GatewayTelemetryLiveRegistry>();
                }
                services.AddSingleton(new TestTargetAuthority(agentId));
                services.AddSingleton<IDevelopmentOperatorTargetAuthority>(provider => provider.GetRequiredService<TestTargetAuthority>());
                services.AddScoped<ICorrelationContext, HttpCorrelationContext>();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapDevelopmentMcpClientObservabilityEndpoints());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class TestLogRegistry(ClientKey client) : IAgentLogGatewaySessionRegistry
    {
        public event Action<GatewayLogBatchEvent>? LogBatchAccepted
        {
            add { }
            remove { }
        }
        public AgentLogRegistration Register(ClientKey value, Guid connectionId, ulong connectionEpoch, AgentLogHello hello, bool provisional = false) => throw new NotSupportedException();
        public bool TryCompleteResync(ClientKey value, string sourceId) => value == client && sourceId == "netratel-runtime";
        public IReadOnlyList<GatewayLogSourceDescriptorDto> GetSources(ClientKey value) => value == client
            ? [new("netratel-runtime", "runtime", "Runtime", "linux", true, null, true, true, true, ["text"])]
            : [];
        public GatewayLogPageDto Query(ClientKey value, GatewayLogPageRequest request) => new([], null, null, false, 0, false);
    }

    private sealed class TestDispatcher : IAgentLogGatewayQueryDispatcher
    {
        public List<LogQueryOperation> Operations { get; } = [];
        public string? HistoryErrorCode { get; set; }
        public bool HistoryResyncRequired { get; set; }
        public AgentLogQueryRegistration Register(AgentLogRegistration registration, bool provisional = false) => throw new NotSupportedException();
        public Task<GatewayLogPageDto> QueryAsync(ClientKey client, GatewayLogPageRequest request, LogQueryOperation operation, CancellationToken cancellationToken)
        {
            Operations.Add(operation);
            var message = operation == LogQueryOperation.History ? "history-record" : operation == LogQueryOperation.StartLive ? "tail-record" : "stopped";
            var record = new GatewayLogRecordDto("cursor", 1, DateTimeOffset.UtcNow, "Information", request.SourceId, null, null, null, null, null, null, message, null, false);
            return Task.FromResult(new GatewayLogPageDto([record], null, null, false, 0, operation == LogQueryOperation.History && HistoryResyncRequired,
                operation == LogQueryOperation.History ? HistoryErrorCode : null));
        }
    }

    private sealed class TestTelemetryRouter(ClientKey client) : IClientTelemetryRouter
    {
        private readonly TelemetrySnapshot _snapshot = new(
            client, 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new NetRatel.Application.Telemetry.TelemetryCpu(20, null, 4), new NetRatel.Application.Telemetry.TelemetryMemory(100, 50, 50, 50), [], [], null, "akka-shadow", true);
        public Task<TelemetryMessageResult> RecordAsync(RecordTelemetrySnapshot message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientTelemetryState> GetSnapshotAsync(ClientKey requested, CancellationToken cancellationToken) => Task.FromResult(new ClientTelemetryState(requested, requested == client ? _snapshot : null));
        public Task<ClientTelemetryReadModelSnapshot> GetReadModelAsync(CancellationToken cancellationToken) => Task.FromResult(new ClientTelemetryReadModelSnapshot([_snapshot], DateTimeOffset.UtcNow));
        public Task<ClientTelemetryRouteStatus> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(new ClientTelemetryRouteStatus(1, 1, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test", "test"));
    }

    private sealed class TestTargetAuthority(Guid agentId) : IDevelopmentOperatorTargetAuthority
    {
        private readonly DevelopmentOperatorTargetGrantView _grant = new(Guid.NewGuid(), 3, agentId, DevelopmentOperatorTargetClassification.DedicatedQa, DevelopmentOperatorOperationScope.Observability, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), true, "test-evidence");
        public List<DevelopmentOperatorOperation> AcceptedOperations { get; } = [];
        public bool Enabled { get; set; } = true;
        public Task<DevelopmentOperatorTargetGrantView> GrantAsync(DevelopmentOperatorTargetGrantRequest request, CancellationToken cancellationToken) => Task.FromResult(_grant);
        public Task<DevelopmentOperatorTargetGrantView?> RevokeAsync(int tenantId, Guid requestedAgentId, string reason, string actorId, string correlationId, CancellationToken cancellationToken) => Task.FromResult<DevelopmentOperatorTargetGrantView?>(null);
        public Task<DevelopmentOperatorTargetGrantView?> GetActiveGrantAsync(int tenantId, Guid requestedAgentId, CancellationToken cancellationToken) => Task.FromResult<DevelopmentOperatorTargetGrantView?>(Enabled && tenantId == 3 && requestedAgentId == agentId ? _grant : null);
        public Task<DevelopmentOperatorTargetDecision> EvaluateAsync(DevelopmentOperatorTargetRequest request, CancellationToken cancellationToken) => Task.FromResult(new DevelopmentOperatorTargetDecision(Enabled, Enabled ? null : "target_not_authorized", Enabled ? _grant.GrantId : null, request));
        public Task<DevelopmentOperatorAcceptedAudit> RecordAcceptedAsync(DevelopmentOperatorTargetDecision decision, CancellationToken cancellationToken)
        {
            AcceptedOperations.Add(decision.Request.Operation);
            return Task.FromResult(new DevelopmentOperatorAcceptedAudit(Guid.NewGuid(), decision.Request.TenantId, decision.Request.AgentId, decision.Request.Operation, decision.Request.ActorId, decision.Request.CorrelationId, DateTimeOffset.UtcNow));
        }
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "test-admin")], Scheme.Name)), Scheme.Name)));
    }
}
