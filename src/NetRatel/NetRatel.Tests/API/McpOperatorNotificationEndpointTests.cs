using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.API.Services;
using NetRatel.Application.Notifications;
using NetRatel.Application.Operations;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorNotificationEndpointTests
{
    [Fact]
    public void Production_notification_routes_are_control_plane_and_m2m_only()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapMcpOperatorNotificationEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/notifications", StringComparison.Ordinal) is true)
            .ToArray();

        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/notifications/",
            "/api/v2/mcp/operator/notifications/{id:guid}",
            "/api/v2/mcp/operator/notifications/summary",
            "/api/v2/mcp/operator/notifications/unread-errors",
            "/api/v2/mcp/operator/notifications/preview/mark-read",
            "/api/v2/mcp/operator/notifications/confirm/mark-read");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }

    [Fact]
    public async Task Mark_read_is_bound_to_the_signed_delegated_operator_and_replays_without_a_second_write()
    {
        var notifications = new RecordingNotifications();
        var authorization = new RecordingAuthorization();
        using var app = await BuildAppAsync(notifications, authorization);
        var id = Guid.NewGuid();
        var client = ClientFor(app, "mark_read");

        var previewResponse = await client.PostAsJsonAsync("/api/v2/mcp/operator/notifications/preview/mark-read", new { ids = new[] { id } });
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorNotificationMarkReadPreview>();
        var confirmed = await client.PostAsJsonAsync("/api/v2/mcp/operator/notifications/confirm/mark-read", new { ids = new[] { id }, planToken = preview!.PlanToken, idempotencyKey = preview.IdempotencyKey });
        var replayed = await client.PostAsJsonAsync("/api/v2/mcp/operator/notifications/confirm/mark-read", new { ids = new[] { id }, planToken = preview.PlanToken, idempotencyKey = preview.IdempotencyKey });

        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replayed.Content.ReadFromJsonAsync<McpOperatorNotificationMarkReadResult>())!.Replayed.Should().BeTrue();
        notifications.MarkReadCalls.Should().ContainSingle();
        notifications.MarkReadCalls.Single().Subject.Should().Be("delegated.operator@example.test");
        notifications.MarkReadCalls.Single().Ids.Should().Equal(id);
        authorization.Accepted.Should().ContainSingle();
        authorization.Requests.All(request =>
            request.TenantId == 0 && request.AgentId is null && request.OperationFamily == McpOperatorOperationFamily.Notifications &&
            request.Principal.Subject == "delegated.operator@example.test" && request.RequiredScopes.SetEquals(new[] { "netratel.mcp.write" })).Should().BeTrue();
    }

    [Fact]
    public async Task Event_retry_is_control_plane_admitted_and_replays_without_a_second_dispatch()
    {
        var events = new RecordingEvents();
        var authorization = new RecordingAuthorization();
        using var app = await BuildAppAsync(new RecordingNotifications(), authorization, events: events);
        var eventId = Guid.NewGuid();

        var preview = await ClientFor(app, "preview_retry", "netratel_events", "netratel.mcp.execute")
            .PostAsync($"/api/v2/mcp/operator/events/{eventId:D}/retry/preview", null);
        var plan = await preview.Content.ReadFromJsonAsync<McpOperatorEventPreview>();
        var confirmed = await ClientFor(app, "retry", "netratel_events", "netratel.mcp.execute")
            .PostAsJsonAsync($"/api/v2/mcp/operator/events/{eventId:D}/retry/confirm", new { planToken = plan!.PlanToken, idempotencyKey = plan.IdempotencyKey });
        var replayed = await ClientFor(app, "retry", "netratel_events", "netratel.mcp.execute")
            .PostAsJsonAsync($"/api/v2/mcp/operator/events/{eventId:D}/retry/confirm", new { planToken = plan.PlanToken, idempotencyKey = plan.IdempotencyKey });

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replayed.Content.ReadFromJsonAsync<McpOperatorEventMutationResult>())!.Replayed.Should().BeTrue();
        events.RetryCalls.Should().Equal(eventId);
        authorization.Requests.Where(request => request.Tool == "netratel_events").Should().OnlyContain(request =>
            request.TenantId == 0 && request.AgentId == null && request.OperationFamily == McpOperatorOperationFamily.Events &&
            request.RequiredScopes.SetEquals(new[] { "netratel.mcp.execute" }));
    }

    [Fact]
    public async Task Connectivity_test_is_server_owned_and_replays_without_second_probe()
    {
        var connectivity = new RecordingConnectivity();
        using var app = await BuildAppAsync(new RecordingNotifications(), new RecordingAuthorization(), connectivity: connectivity);

        var preview = await ClientFor(app, "preview_test", "netratel_connectivity", "netratel.mcp.execute")
            .PostAsync("/api/v2/mcp/operator/connectivity/test/preview", null);
        var plan = await preview.Content.ReadFromJsonAsync<McpOperatorConnectivityTestPreview>();
        var confirmed = await ClientFor(app, "test", "netratel_connectivity", "netratel.mcp.execute")
            .PostAsJsonAsync("/api/v2/mcp/operator/connectivity/test/confirm", new { planToken = plan!.PlanToken, idempotencyKey = plan.IdempotencyKey });
        var replayed = await ClientFor(app, "test", "netratel_connectivity", "netratel.mcp.execute")
            .PostAsJsonAsync("/api/v2/mcp/operator/connectivity/test/confirm", new { planToken = plan.PlanToken, idempotencyKey = plan.IdempotencyKey });

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replayed.Content.ReadFromJsonAsync<McpOperatorConnectivityTestConfirmed>())!.Replayed.Should().BeTrue();
        connectivity.TestCalls.Should().Be(1);
        (await confirmed.Content.ReadAsStringAsync()).Should().NotContain("https://").And.NotContain("secret");
    }

    [Fact]
    public async Task Development_host_accepts_a_dev_control_plane_assertion_against_development_policy()
    {
        var authorization = new RecordingAuthorization();
        var requiredScope = new[] { "netratel.mcp.admin" };
        using var app = await BuildAppAsync(new RecordingNotifications(), authorization, environmentName: Environments.Development);

        var response = await ClientFor(app, "settings", "netratel_connectivity", "netratel.mcp.admin", "dev")
            .GetAsync("/api/v2/mcp/operator/connectivity/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        authorization.Requests.Should().ContainSingle(request =>
            request.Environment == McpOperatorEnvironment.Development &&
            request.Tool == "netratel_connectivity" &&
            request.Operation == "netratel_connectivity/settings" &&
            request.RequiredScopes.SetEquals(requiredScope));
    }

    private static HttpClient ClientFor(
        IHost app,
        string operation,
        string tool = "netratel_notifications",
        string scope = "netratel.mcp.write",
        string instance = "prod")
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("M2M");
        var assertion = app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
            new McpOperatorDelegationIdentity("delegated.operator@example.test", "notification-client", null, [], ["Operator", "Administrator"], [scope]),
            new McpOperatorDelegationRequest(tool, operation, $"request-{Guid.NewGuid():N}", "https://mcp.{instance}.example/mcp", instance, null, null, "notification-correlation"));
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName, assertion);
        return client;
    }

    private static async Task<IHost> BuildAppAsync(
        RecordingNotifications notifications,
        RecordingAuthorization authorization,
        RecordingEvents? events = null,
        RecordingConnectivity? connectivity = null,
        string? environmentName = null)
    {
        var delegationOptions = new McpOperatorDelegationOptions
        {
            Enabled = true,
            Issuer = "netratel-mcp-test",
            Audience = "netratel-api-test",
            ServicePrincipal = "netratel-mcp-http-test",
            KeyId = "test-2026-08",
            SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes"))
        };
        var confirmations = new RecordingConfirmations();
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(environmentName ?? Environments.Production);
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
                services.AddSingleton<IMcpOperatorAuthorization>(authorization);
                services.AddSingleton<IMcpOperatorConfirmationService>(confirmations);
                services.AddSingleton<INetRatelNotificationService>(notifications);
                services.AddSingleton<IMcpOperatorEventAuthority>(events ?? new RecordingEvents());
                services.AddSingleton<IMcpOperatorConnectivityAuthority>(connectivity ?? new RecordingConnectivity());
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseMiddleware<McpOperatorDelegationMiddleware>();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapMcpOperatorNotificationEndpoints();
                    endpoints.MapMcpOperatorEventEndpoints();
                    endpoints.MapMcpOperatorConnectivityEndpoints();
                });
            });
        });
        return await builder.StartAsync();
    }

    private sealed class RecordingAuthorization : IMcpOperatorAuthorization
    {
        private static readonly Guid PolicyId = Guid.Parse("dfe3ebfb-2af0-47c6-b4f5-6e26f2346f4d");
        public List<McpOperatorAccessRequest> Requests { get; } = [];
        public List<McpOperatorDecision> Accepted { get; } = [];

        public Task<bool> HasTenantVisibilityAsync(McpOperatorEnvironment environment, McpOperatorPrincipal principal, int tenantId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<McpOperatorDecision> EvaluateAsync(McpOperatorAccessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new McpOperatorDecision(true, null, null, [PolicyId], new McpOperatorConstraints(), request.TargetSetDigest, request, 1));
        }

        public Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorDecision decision, string servicePrincipal, CancellationToken cancellationToken)
        {
            Accepted.Add(decision);
            return Task.FromResult(new McpOperatorAcceptedAudit(Guid.NewGuid(), PolicyId, decision.Request.Environment, servicePrincipal,
                decision.Request.Principal.Subject, decision.Request.Principal.ClientId, decision.Request.Principal.AuthorizedParty,
                [], [], [], decision.Request.McpResource, decision.Request.McpInstance, decision.Request.Tool, decision.Request.TenantId,
                decision.Request.AgentId, decision.Request.OperationFamily, decision.Request.Operation, decision.Request.CorrelationId,
                decision.Request.RequestId, DateTimeOffset.UtcNow));
        }
    }

    private sealed class RecordingConfirmations : IMcpOperatorConfirmationService
    {
        private const string PlanToken = "1niCnzRgqsyDqbqxq4hoYMfGMrmadawRXxEewjFSTko";
        private const string IdempotencyKey = "M79he2GLdw5Xti4pOm8Dvo2MyYp8TtxFCIWfdceS3m0";
        private readonly Dictionary<string, (Guid Id, McpOperatorIdempotencyOutcome Outcome, string? Result)> _completed = [];

        public Task<McpOperatorConfirmationPlan> CreatePlanAsync(McpOperatorConfirmationPlanRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new McpOperatorConfirmationPlan(PlanToken, IdempotencyKey, DateTimeOffset.UtcNow.AddMinutes(5), request.Decision.Request.ConfirmationClass, request.PayloadHash, request.Decision.TargetSetDigest));

        public Task<McpOperatorConfirmationAdmission> ConfirmAsync(McpOperatorConfirmationRequest request, CancellationToken cancellationToken)
        {
            if (_completed.TryGetValue(request.PayloadHash, out var prior))
                return Task.FromResult(new McpOperatorConfirmationAdmission(false, true, null, prior.Id, prior.Outcome, prior.Result));
            var id = Guid.NewGuid();
            _completed[request.PayloadHash] = (id, McpOperatorIdempotencyOutcome.Pending, null);
            return Task.FromResult(new McpOperatorConfirmationAdmission(true, false, null, id, McpOperatorIdempotencyOutcome.Pending, null));
        }

        public Task CompleteAsync(Guid idempotencyId, McpOperatorIdempotencyOutcome outcome, string? resultReference, CancellationToken cancellationToken)
        {
            var key = _completed.Single(pair => pair.Value.Id == idempotencyId).Key;
            _completed[key] = (idempotencyId, outcome, resultReference);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNotifications : INetRatelNotificationService
    {
        public List<(string Subject, IReadOnlyList<Guid> Ids)> MarkReadCalls { get; } = [];
        public Task<PagedResult<NetRatelNotificationDto>> GetPageAsync(string userId, int page, int pageSize, string? eventType, string? correlationId, string? entityId, string? status, DateTimeOffset? from, DateTimeOffset? to, string? search, string? source, NetRatelNotificationSeverity? severity, CancellationToken ct) => Task.FromResult(new PagedResult<NetRatelNotificationDto>([], page, pageSize, 0));
        public Task<NetRatelNotificationDto?> GetByIdAsync(Guid id, string userId, CancellationToken ct) => Task.FromResult<NetRatelNotificationDto?>(null);
        public Task<IReadOnlyList<NetRatelNotificationDto>> GetUnreadErrorsAsync(string userId, int take, CancellationToken ct) => Task.FromResult<IReadOnlyList<NetRatelNotificationDto>>([]);
        public Task<NetRatelNotificationSummaryDto> GetSummaryAsync(string userId, CancellationToken ct) => Task.FromResult(new NetRatelNotificationSummaryDto());
        public Task<int> MarkReadAsync(string userId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
        {
            MarkReadCalls.Add((userId, ids.ToArray()));
            return Task.FromResult(ids.Count);
        }
        public Task RetryAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task DisableAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingEvents : IMcpOperatorEventAuthority
    {
        public List<Guid> RetryCalls { get; } = [];
        public Task<IReadOnlyList<McpOperatorEventSummary>> ListAsync(int page, int pageSize, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<McpOperatorEventSummary>>([]);
        public Task<McpOperatorEventSummary?> GetAsync(Guid eventId, CancellationToken cancellationToken) =>
            Task.FromResult<McpOperatorEventSummary?>(new McpOperatorEventSummary(eventId, "DomainEvent.Test", DateTimeOffset.UtcNow, "test", "Pending", NetRatelNotificationSeverity.Info, 0));
        public Task<McpOperatorEventMutationOutcome> RetryAsync(Guid eventId, CancellationToken cancellationToken)
        {
            RetryCalls.Add(eventId);
            return Task.FromResult(new McpOperatorEventMutationOutcome(true));
        }
        public Task<McpOperatorEventMutationOutcome> DisableAsync(Guid eventId, CancellationToken cancellationToken) => Task.FromResult(new McpOperatorEventMutationOutcome(true));
    }

    private sealed class RecordingConnectivity : IMcpOperatorConnectivityAuthority
    {
        public int TestCalls { get; private set; }
        public Task<McpOperatorConnectivitySettings> GetSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new McpOperatorConnectivitySettings(true, true, true, "ExternalService"));
        public McpOperatorNetRatelConnectivity GetNetRatel() => new(true, true, true, true, true);
        public Task<McpOperatorConnectivityTestResult> TestAsync(CancellationToken cancellationToken)
        {
            TestCalls++;
            return Task.FromResult(new McpOperatorConnectivityTestResult([new McpOperatorConnectivityProbe("RemoteHealth", "Green", 200, 10, "ExternalService")], false));
        }
    }

    private sealed class TestM2mAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Request.Headers.Authorization == "M2M"
                ? Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "netratel-mcp-http-test")], Scheme.Name)), Scheme.Name)))
                : Task.FromResult(AuthenticateResult.Fail("Missing M2M authentication."));
    }
}
