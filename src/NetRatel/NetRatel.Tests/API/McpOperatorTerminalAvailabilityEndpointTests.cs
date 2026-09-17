using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Services;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Terminals;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorTerminalAvailabilityEndpointTests
{
    [Fact]
    public async Task Terminal_stream_replays_only_for_the_signed_owner_and_preserves_cursor_metadata()
    {
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var admission = new RecordingAdmission();
        using var app = await BuildAppAsync(agentId, admission, sessionId);
        var client = M2mClient(app);
        var registry = app.Services.GetRequiredService<AvailableTerminalRegistry>();
        registry.OutputWindow = new([new(ulong.MaxValue, "retained marker"u8.ToArray())], ulong.MaxValue, true, true, false);
        var path = $"/api/v2/mcp/operator/agents/42/{agentId:D}/terminal/sessions/{sessionId}/stream-window?afterSequence=18446744073709551614&maxRecords=2&windowSeconds=1";
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        registry.ReplayCalls.Should().BeEmpty();

        void Delegate(string subject)
        {
            client.DefaultRequestHeaders.Remove(McpOperatorDelegationOptions.HeaderName);
            client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName,
                app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
                    new McpOperatorDelegationIdentity(subject, "operator-client", "operator-client", ["netratel-operators"], ["Operator"], ["netratel.mcp.observe"]),
                    new McpOperatorDelegationRequest("netratel_terminal", "stream_window", "terminal-output-request",
                        "https://mcp.dev.example/mcp", "dev", 42, agentId, "terminal-output-correlation")));
        }
        Delegate("another-operator@example.test");
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        registry.ReplayCalls.Should().BeEmpty();
        Delegate("operator@example.test");
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("nextSequence").GetString().Should().Be("18446744073709551615");
        body.RootElement.GetProperty("records")[0].GetString().Should().Be("retained marker");
        body.RootElement.GetProperty("gap").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        registry.ReplayCalls.Should().ContainSingle().Which.Should().Be((sessionId, 1UL, ulong.MaxValue - 1, 2, 16 * 1024, TimeSpan.FromSeconds(1)));
        admission.Recorded.Should().ContainSingle().Which.Operation.Should().Be("netratel_terminal/stream_window");
    }

    [Fact]
    public void Route_IsExplicitlyVersionedAndRequiresTheMcpM2MIdentity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IMcpOperatorRouteAdmission>(_ => throw new NotSupportedException());
        var app = builder.Build();

        app.MapMcpOperatorTerminalAvailabilityEndpoints();

        var route = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/terminal/availability");
        route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(metadata => metadata.Policy)
            .Should().Contain("M2MOnly");
    }

    [Fact]
    public void Production_session_routes_are_explicitly_versioned_and_require_the_mcp_m2m_identity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IClientPresenceRouter>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorConfirmationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorTerminalSessionStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorTerminalActionStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<NetRatelAkkaMigrationOptions>();
        builder.Services.AddSingleton<IAgentTerminalSessionRegistry>(new AvailableTerminalRegistry(Guid.Empty));
        var app = builder.Build();

        app.MapMcpOperatorTerminalSessionEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/terminal/sessions", StringComparison.Ordinal) is true)
            .ToArray();
        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/terminal/sessions/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/terminal/sessions/confirm",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/terminal/sessions/{sessionId}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/terminal/sessions/{sessionId}/input",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/terminal/sessions/{sessionId}/stream-window",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/terminal/sessions/{sessionId}/resize",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/terminal/sessions/{sessionId}/close",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/terminal/sessions/{sessionId}/diagnostics");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }

    [Fact]
    public async Task Route_RequiresExactDelegation_AndAuditsTheReadBeforeReturningGatewayFacts()
    {
        var agentId = Guid.NewGuid();
        var admission = new RecordingAdmission();
        using var app = await BuildAppAsync(agentId, admission);
        var client = M2mClient(app);
        var path = $"/api/v2/mcp/operator/agents/42/{agentId:D}/terminal/availability";

        var missingDelegation = await client.GetAsync(path);

        missingDelegation.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await missingDelegation.Content.ReadAsStringAsync()).Should().Contain("delegated_identity_required");

        var tokens = app.Services.GetRequiredService<McpOperatorDelegationTokenService>();
        var assertion = tokens.Create(
            new McpOperatorDelegationIdentity(
                "operator@example.test",
                "operator-client",
                "operator-client",
                ["netratel-operators"],
                ["Operator"],
                ["netratel.mcp.observe"]),
            new McpOperatorDelegationRequest(
                "netratel_terminal",
                "availability",
                "request-42",
                "https://mcp.dev.example/mcp",
                "dev",
                42,
                agentId,
                "correlation-42"));
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);

        var response = await client.GetFromJsonAsync<McpOperatorTerminalAvailabilityResponse>(path);

        response.Should().NotBeNull();
        response!.TenantId.Should().Be(42);
        response.AgentId.Should().Be(agentId);
        response.AvailableShells.Should().Equal("sh");
        response.SupportsIdempotentClose.Should().BeTrue();
        response.CorrelationId.Should().Be("correlation-42");
        admission.Evaluated.Should().ContainSingle().Which.Should().Match<McpOperatorRouteAccessRequest>(request =>
            request.TargetOnline &&
            request.CapabilityAvailable &&
            request.RequiredScopes.Count == 1 &&
            request.RequiredScopes.Contains("netratel.mcp.observe") &&
            request.Principal.Subject == "operator@example.test");
        admission.Recorded.Should().ContainSingle().Which.RequestId.Should().Be("request-42");
        response.AuditId.Should().Be(admission.Recorded[0].AuditId);
    }

    [Fact]
    public async Task Route_RejectsDelegationBoundToAnotherTarget()
    {
        var requestedAgentId = Guid.NewGuid();
        var admission = new RecordingAdmission();
        using var app = await BuildAppAsync(requestedAgentId, admission);
        var client = M2mClient(app);
        var assertion = app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
            new McpOperatorDelegationIdentity("operator@example.test", null, null, [], [], ["netratel.mcp.observe"]),
            new McpOperatorDelegationRequest(
                "netratel_terminal",
                "availability",
                "wrong-target-request",
                "https://mcp.dev.example/mcp",
                "dev",
                42,
                Guid.NewGuid(),
                "correlation-42"));
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/42/{requestedAgentId:D}/terminal/availability");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("delegated_identity_invalid");
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("summary").GetString().Should().Be("MCP operator terminal availability was not admitted.");
        document.RootElement.GetProperty("failure").GetProperty("safeDetails").GetString()
            .Should().Be("A valid signed operator delegation was not available for this exact terminal availability operation.");
        admission.Evaluated.Should().BeEmpty();
        admission.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Route_MapsAChangedPolicyDuringPreResponseAuditToAStructuredDenial()
    {
        var agentId = Guid.NewGuid();
        var admission = new RecordingAdmission("target_policy_missing");
        using var app = await BuildAppAsync(agentId, admission);
        var client = M2mClient(app);
        var assertion = app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
            new McpOperatorDelegationIdentity("operator@example.test", null, null, [], [], ["netratel.mcp.observe"]),
            new McpOperatorDelegationRequest(
                "netratel_terminal",
                "availability",
                "policy-race-request",
                "https://mcp.dev.example/mcp",
                "dev",
                42,
                agentId,
                "correlation-42"));
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);

        var response = await client.GetAsync($"/api/v2/mcp/operator/agents/42/{agentId:D}/terminal/availability");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("target_policy_missing").And.Contain("policy");
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("failure").GetProperty("safeDetails").GetString()
            .Should().Be("The terminal availability request did not satisfy the current operator policy.");
        admission.Evaluated.Should().ContainSingle();
        admission.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Terminal_input_replays_the_same_signed_delivery_without_requeuing_bytes()
    {
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var admission = new RecordingAdmission();
        using var app = await BuildAppAsync(agentId, admission, sessionId);
        var client = M2mClient(app);
        var assertion = app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
            new McpOperatorDelegationIdentity(
                "operator@example.test",
                "operator-client",
                "operator-client",
                ["netratel-operators"],
                ["Operator"],
                ["netratel.mcp.execute"]),
            new McpOperatorDelegationRequest(
                "netratel_terminal",
                "send_input",
                "terminal-input-delivery-42",
                "https://mcp.dev.example/mcp",
                "dev",
                42,
                agentId,
                "terminal-input-correlation-42"));
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);
        var path = $"/api/v2/mcp/operator/agents/42/{agentId:D}/terminal/sessions/{sessionId}/input";

        var first = await client.PostAsJsonAsync(path, new McpOperatorTerminalInputRequest("echo netratel\n"));
        var second = await client.PostAsJsonAsync(path, new McpOperatorTerminalInputRequest("echo netratel\n"));
        var firstBody = await first.Content.ReadAsStringAsync();
        var replayedBody = await second.Content.ReadAsStringAsync();

        first.StatusCode.Should().Be(HttpStatusCode.Accepted, firstBody);
        second.StatusCode.Should().Be(HttpStatusCode.Accepted, replayedBody);
        var firstAction = JsonSerializer.Deserialize<McpOperatorTerminalAction>(firstBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var replayedAction = JsonSerializer.Deserialize<McpOperatorTerminalAction>(replayedBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        firstAction.Should().BeEquivalentTo(new McpOperatorTerminalAction(sessionId, "input_queued", "terminal-input-correlation-42"));
        replayedAction.Should().BeEquivalentTo(new McpOperatorTerminalAction(sessionId, "input_queued", "terminal-input-correlation-42") { Replayed = true });
        app.Services.GetRequiredService<AvailableTerminalRegistry>().InputCalls.Should().Be(1);
        admission.Recorded.Should().ContainSingle().Which.RequestId.Should().Be("terminal-input-delivery-42");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var action = await db.McpOperatorTerminalActions.SingleAsync();
        action.Operation.Should().Be("send_input");
        action.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
        action.ResultReference.Should().Be("input_queued");
        action.AcceptedAuditId.Should().NotBeNull();
    }

    [Fact]
    public async Task Terminal_close_dispatches_the_durable_close_after_marking_the_lease_pending()
    {
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var admission = new RecordingAdmission();
        using var app = await BuildAppAsync(agentId, admission, sessionId);
        var client = M2mClient(app);
        var assertion = app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
            new McpOperatorDelegationIdentity(
                "operator@example.test",
                "operator-client",
                "operator-client",
                ["netratel-operators"],
                ["Operator"],
                ["netratel.mcp.execute"]),
            new McpOperatorDelegationRequest(
                "netratel_terminal",
                "close",
                "terminal-close-delivery-42",
                "https://mcp.dev.example/mcp",
                "dev",
                42,
                agentId,
                "terminal-close-correlation-42"));
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);

        var response = await client.PostAsync(
            $"/api/v2/mcp/operator/agents/42/{agentId:D}/terminal/sessions/{sessionId}/close",
            null);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        app.Services.GetRequiredService<AvailableTerminalRegistry>().CloseCalls.Should().ContainSingle()
            .Which.Should().Be((sessionId, 1UL, "operator_requested_close"));
        admission.Recorded.Should().ContainSingle().Which.RequestId.Should().Be("terminal-close-delivery-42");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.McpOperatorTerminalSessions.SingleAsync()).State.Should().Be(McpOperatorTerminalSessionState.Closing);
        (await db.McpOperatorTerminalActions.SingleAsync()).ResultReference.Should().Be("close_queued");
    }

    [Fact]
    public async Task Terminal_close_treats_an_already_absent_session_as_idempotently_complete()
    {
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var admission = new RecordingAdmission();
        using var app = await BuildAppAsync(agentId, admission);
        var client = M2mClient(app);
        var assertion = app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
            new McpOperatorDelegationIdentity(
                "operator@example.test",
                "operator-client",
                "operator-client",
                ["netratel-operators"],
                ["Operator"],
                ["netratel.mcp.execute"]),
            new McpOperatorDelegationRequest(
                "netratel_terminal",
                "close",
                "terminal-close-retry-42",
                "https://mcp.dev.example/mcp",
                "dev",
                42,
                agentId,
                "terminal-close-retry-correlation-42"));
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);

        var response = await client.PostAsync(
            $"/api/v2/mcp/operator/agents/42/{agentId:D}/terminal/sessions/{sessionId}/close",
            null);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        JsonSerializer.Deserialize<McpOperatorTerminalAction>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().Be(new McpOperatorTerminalAction(sessionId, "close_already_complete", "terminal-close-retry-correlation-42"));
        app.Services.GetRequiredService<AvailableTerminalRegistry>().CloseCalls.Should().BeEmpty();
        admission.Recorded.Should().ContainSingle().Which.RequestId.Should().Be("terminal-close-retry-42");
    }

    [Fact]
    public async Task Terminal_close_acknowledges_a_closing_lease_while_the_gateway_reconnects()
    {
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var admission = new RecordingAdmission();
        using var app = await BuildAppAsync(agentId, admission, sessionId, terminalAvailable: false);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var record = await db.McpOperatorTerminalSessions.SingleAsync();
            record.State = McpOperatorTerminalSessionState.Closing;
            record.CloseReason = "operator_requested_close";
            record.CloseRequestedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var client = M2mClient(app);
        var assertion = app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
            new McpOperatorDelegationIdentity(
                "operator@example.test",
                "operator-client",
                "operator-client",
                ["netratel-operators"],
                ["Operator"],
                ["netratel.mcp.execute"]),
            new McpOperatorDelegationRequest(
                "netratel_terminal",
                "close",
                "terminal-close-reconnect-42",
                "https://mcp.dev.example/mcp",
                "dev",
                42,
                agentId,
                "terminal-close-reconnect-correlation-42"));
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);

        var response = await client.PostAsync(
            $"/api/v2/mcp/operator/agents/42/{agentId:D}/terminal/sessions/{sessionId}/close",
            null);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        JsonSerializer.Deserialize<McpOperatorTerminalAction>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Should().Be(new McpOperatorTerminalAction(sessionId, "close_pending", "terminal-close-reconnect-correlation-42"));
        app.Services.GetRequiredService<AvailableTerminalRegistry>().CloseCalls.Should().BeEmpty();
        admission.Recorded.Should().BeEmpty();
    }

    [Fact]
    public async Task Terminal_expiry_marks_an_already_absent_closing_session_closed()
    {
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var admission = new RecordingAdmission();
        using var app = await BuildAppAsync(agentId, admission, sessionId, closeIsAbsent: true);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var record = await db.McpOperatorTerminalSessions.SingleAsync();
            record.State = McpOperatorTerminalSessionState.Closing;
            record.CloseRequestedAtUtc = DateTimeOffset.UtcNow;
            record.CloseReason = "operator_requested_close";
            await db.SaveChangesAsync();
        }
        var service = new ProductionMcpTerminalExpiryService(
            app.Services.GetRequiredService<IServiceScopeFactory>(),
            app.Services.GetRequiredService<AvailableTerminalRegistry>(),
            app.Services.GetRequiredService<ILogger<ProductionMcpTerminalExpiryService>>());

        await service.SweepOnceAsync(CancellationToken.None);

        app.Services.GetRequiredService<AvailableTerminalRegistry>().CloseCalls.Should().BeEmpty();
        using var verificationScope = app.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await verificationDb.McpOperatorTerminalSessions.SingleAsync()).State.Should().Be(McpOperatorTerminalSessionState.Closed);
    }

    private static HttpClient M2mClient(IHost app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("M2M");
        return client;
    }

    private static async Task<IHost> BuildAppAsync(
        Guid agentId,
        RecordingAdmission admission,
        string? terminalSessionId = null,
        bool closeIsAbsent = false,
        bool terminalAvailable = true)
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var delegationOptions = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-dev",
            Audience = "netratel-api-dev",
            ServicePrincipal = "netratel-mcp-http-dev",
            KeyId = "dev-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes")),
            LifetimeSeconds = 90
        };
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Development);
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "M2M";
                    options.DefaultChallengeScheme = "M2M";
                }).AddScheme<AuthenticationSchemeOptions, TestM2mAuthenticationHandler>("M2M", _ => { });
                services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy =>
                {
                    policy.AddAuthenticationSchemes("M2M");
                    policy.RequireAuthenticatedUser();
                }));
                services.AddSingleton(delegationOptions);
                services.AddSingleton<McpOperatorDelegationTokenService>();
                services.AddSingleton(admission);
                services.AddSingleton<IMcpOperatorRouteAdmission>(admission);
                services.AddSingleton(new NetRatelAkkaMigrationOptions
                {
                    Enabled = true,
                    PresenceEnabled = true,
                    GatewayEnabled = true,
                    PresenceAuthorityEnabled = true,
                    TerminalGatewayEnabled = true,
                    TerminalAuthorityEnabled = true
                });
                services.AddSingleton<IClientPresenceRouter>(new TestPresence(new ClientKey(42, agentId)));
                services.AddSingleton<IMcpOperatorConfirmationService>(_ => throw new NotSupportedException());
                services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(databaseName));
                services.AddScoped<IMcpOperatorTerminalSessionStore, McpOperatorTerminalSessionStore>();
                services.AddScoped<IMcpOperatorTerminalActionStore, McpOperatorTerminalActionStore>();
                var registry = new AvailableTerminalRegistry(agentId, closeIsAbsent, terminalAvailable);
                services.AddSingleton(registry);
                services.AddSingleton<IAgentTerminalSessionRegistry>(registry);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseMiddleware<McpOperatorDelegationMiddleware>();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapMcpOperatorTerminalAvailabilityEndpoints();
                    endpoints.MapMcpOperatorTerminalSessionEndpoints();
                });
            });
        });

        var app = await builder.StartAsync();
        if (terminalSessionId is not null)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.McpOperatorTerminalSessions.Add(new McpOperatorTerminalSessionRecord
            {
                Id = Guid.NewGuid(),
                SessionId = terminalSessionId,
                TenantId = 42,
                AgentId = agentId,
                Generation = 1,
                Subject = "operator@example.test",
                ClientId = "operator-client",
                McpResource = "https://mcp.dev.example/mcp",
                McpInstance = "dev",
                PolicyId = Guid.Parse("9252da81-08cf-4f62-a546-241428329d2b"),
                PolicyVersion = 1,
                AcceptedAuditId = Guid.NewGuid(),
                ShellType = "sh",
                WorkingDirectory = "/srv/netratel",
                Columns = 120,
                Rows = 32,
                EffectiveConstraintsJson = JsonSerializer.Serialize(
                    new McpOperatorConstraints(MaxOutputBytes: 16 * 1024),
                    McpOperatorJsonContext.Default.McpOperatorConstraints),
                State = McpOperatorTerminalSessionState.Opened,
                CreatedAtUtc = now,
                LastActivityAtUtc = now,
                IdleExpiresAtUtc = now.AddMinutes(5),
                ExpiresAtUtc = now.AddMinutes(30),
                Version = 1
            });
            await db.SaveChangesAsync();
        }

        return app;
    }

    private sealed class RecordingAdmission(string? recordFailureCode = null) : IMcpOperatorRouteAdmission
    {
        private static readonly Guid PolicyId = Guid.Parse("9252da81-08cf-4f62-a546-241428329d2b");
        public List<McpOperatorRouteAccessRequest> Evaluated { get; } = [];
        public List<McpOperatorAcceptedAudit> Recorded { get; } = [];

        public Task<McpOperatorRouteAdmission> EvaluateAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken)
        {
            Evaluated.Add(request);
            var operation = McpOperatorOperationCatalog.Find(request.Tool, request.Operation)!;
            var access = new McpOperatorAccessRequest(
                request.Environment,
                request.Principal,
                request.TenantId,
                request.AgentId,
                McpOperatorTargetClassification.DevelopmentSafe,
                operation.OperationFamily,
                $"{request.Tool}/{request.Operation}",
                request.RequiredScopes,
                operation.ConfirmationClass,
                request.CorrelationId,
                request.RequestId,
                "test-target-digest",
                new HashSet<string>(["qa"], StringComparer.Ordinal),
                request.McpResource,
                request.McpInstance,
                request.Tool,
                TargetOnline: request.TargetOnline,
                CapabilityAvailable: request.CapabilityAvailable);
            var decision = !request.TargetOnline
                ? McpOperatorDecision.Denied(access, "target_offline", McpOperatorAuthorizationLayer.Target, [PolicyId])
                : !request.CapabilityAvailable
                    ? McpOperatorDecision.Denied(access, "capability_unavailable", McpOperatorAuthorizationLayer.Capability, [PolicyId])
                    : new McpOperatorDecision(true, null, null, [PolicyId], new McpOperatorConstraints(), "test-target-digest", access, 1);
            return Task.FromResult(new McpOperatorRouteAdmission(decision, operation));
        }

        public Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken)
        {
            if (recordFailureCode is not null)
                throw new McpOperatorAdmissionRejectedException(recordFailureCode);

            var operation = McpOperatorOperationCatalog.Find(request.Tool, request.Operation)!;
            var audit = new McpOperatorAcceptedAudit(
                Guid.NewGuid(),
                PolicyId,
                request.Environment,
                request.ServicePrincipal,
                request.Principal.Subject,
                request.Principal.ClientId,
                request.Principal.AuthorizedParty,
                request.Principal.Groups.Order(StringComparer.Ordinal).ToArray(),
                request.Principal.Roles.Order(StringComparer.Ordinal).ToArray(),
                request.Principal.Scopes.Order(StringComparer.Ordinal).ToArray(),
                request.McpResource,
                request.McpInstance,
                request.Tool,
                request.TenantId,
                request.AgentId,
                operation.OperationFamily,
                $"{request.Tool}/{request.Operation}",
                request.CorrelationId,
                request.RequestId,
                DateTimeOffset.UtcNow);
            Recorded.Add(audit);
            return Task.FromResult(audit);
        }
    }

    private sealed class AvailableTerminalRegistry(Guid availableAgentId, bool closeIsAbsent = false, bool terminalAvailable = true) : IAgentTerminalSessionRegistry, IAgentTerminalOutputReplayRegistry
    {
        public GatewayTerminalOutputWindow? OutputWindow { get; set; }
        public List<(string SessionId, ulong Generation, ulong AfterSequence, int Records, int Bytes, TimeSpan Wait)> ReplayCalls { get; } = [];
        public Task<GatewayTerminalOutputWindow> ReadOutputWindowAsync(string sessionId, ulong generation, ulong afterSequence,
            int maximumRecords, int maximumBytes, TimeSpan wait, CancellationToken cancellationToken)
        {
            ReplayCalls.Add((sessionId, generation, afterSequence, maximumRecords, maximumBytes, wait));
            return Task.FromResult(OutputWindow ?? throw new InvalidOperationException("The test must provide a replay window."));
        }

        private int _inputCalls;
        private readonly List<(string SessionId, ulong Generation, string Reason)> _closeCalls = [];

        public int InputCalls => _inputCalls;
        public IReadOnlyList<(string SessionId, ulong Generation, string Reason)> CloseCalls => _closeCalls;

        public AgentTerminalGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, IReadOnlyList<string> availableShells, IReadOnlyList<string>? capabilities = null) =>
            new(Channel.CreateUnbounded<GatewayTerminalFrame>().Reader, () => { });

        public GatewayTerminalAvailability? GetAvailability(ClientKey client) =>
            terminalAvailable && client.TenantId == 42 && client.AgentId == availableAgentId
                ? new GatewayTerminalAvailability(Guid.NewGuid(), 1, ["sh"], DateTimeOffset.UtcNow, true)
                : null;

        public Task<GatewayTerminalSession> OpenAsync(ClientKey client, string shellType, string? workingDirectory, int columns, int rows, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task SendInputAsync(string sessionId, ulong generation, ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            Interlocked.Increment(ref _inputCalls);
            return Task.CompletedTask;
        }

        public Task ResizeAsync(string sessionId, ulong generation, int columns, int rows, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task CloseAsync(string sessionId, ulong generation, string reason, CancellationToken ct)
        {
            if (closeIsAbsent)
                throw new TerminalGatewayActionException("terminal_session_not_found", "The terminal session is already absent.");
            _closeCalls.Add((sessionId, generation, reason));
            return Task.CompletedTask;
        }

        public GatewayTerminalOutputSubscription Subscribe(string sessionId, ulong generation) =>
            throw new NotSupportedException();

        public GatewayTerminalSession? Get(string sessionId) => null;
        public bool TryReceiveOpened(ClientKey client, TerminalSessionOpened opened) => false;
        public Task<bool> TryReceiveOutputAsync(ClientKey client, TerminalOutput output, CancellationToken ct) => Task.FromResult(false);
        public bool TryReceiveResizeApplied(ClientKey client, TerminalResizeApplied resize) => false;
        public bool TryReceiveClosed(ClientKey client, TerminalSessionClosed closed) => false;
        public bool TryReceiveFailed(ClientKey client, TerminalSessionFailed failed) => false;
    }

    private sealed class TestPresence(ClientKey client) : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey value, CancellationToken cancellationToken) => Task.FromResult(new ClientPresenceSnapshot(
            value,
            value == client ? ShadowPresenceStatus.Online : ShadowPresenceStatus.Offline,
            1,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            "1.0",
            ["terminal"],
            null,
            "test",
            true));
    }

    private sealed class TestM2mAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Request.Headers.Authorization == "M2M"
                ? Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "netratel-mcp-http-dev")], Scheme.Name)),
                    Scheme.Name)))
                : Task.FromResult(AuthenticateResult.Fail("Missing M2M authentication."));
    }
}
