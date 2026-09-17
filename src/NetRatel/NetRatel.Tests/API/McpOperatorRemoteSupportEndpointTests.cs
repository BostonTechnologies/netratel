using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
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
using NetRatel.Shared.Operations;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorRemoteSupportEndpointTests
{
    private static readonly Guid Agent = Guid.Parse("18d31616-e870-4e22-b35d-1b228b1cc85f");
    private static string Root => $"/api/v2/mcp/operator/agents/42/{Agent:D}/remote-support";

    [Theory]
    [InlineData("presence")]
    [InlineData("capabilities")]
    [InlineData("inventory")]
    public async Task Reads_require_exact_delegation_and_audit_the_operator_subject(string operation)
    {
        var admission = new RecordingAdmission();
        using var app = await BuildAsync(admission, new Preparation());
        var client = app.GetTestClient();
        (await client.GetAsync(Root + "/" + operation)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("M2M");
        (await client.GetAsync(Root + "/" + operation)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Delegate(app, client, operation, Guid.NewGuid());
        (await client.GetAsync(Root + "/" + operation)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        admission.Recorded.Should().BeEmpty();
        Delegate(app, client, operation, Agent);
        (await client.GetAsync(Root + "/" + operation)).StatusCode.Should().Be(HttpStatusCode.OK);
        admission.Recorded.Should().ContainSingle().Which.Subject.Should().Be("operator@example.test");
        admission.Evaluated.Should().ContainSingle().Which.RequiredScopes.Should().Contain("netratel.mcp.observe");
    }

    [Fact]
    public async Task Policy_revocation_before_read_returns_actionable_403()
    {
        using var app = await BuildAsync(new RecordingAdmission("target_policy_missing"), new Preparation());
        var client = app.GetTestClient(); Delegate(app, client, "inventory", Agent);
        var response = await client.GetAsync(Root + "/inventory");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("target_policy_missing").And.Contain("netratel.mcp.observe");
    }

    [Fact]
    public async Task Disabled_inventory_profile_starts_without_preparation_registration_and_reports_unavailability()
    {
        using var app = await BuildAsync(new RecordingAdmission(), new Preparation(), inventoryEnabled: false);
        var client = app.GetTestClient();
        foreach (var operation in new[] { "presence", "capabilities", "inventory" })
        {
            Delegate(app, client, operation, Agent);
            var response = await client.GetAsync(Root + "/" + operation);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            if (operation == "presence") continue;
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("enabled").GetBoolean().Should().BeFalse();
            json.GetProperty("hasSnapshot").GetBoolean().Should().BeFalse();
            json.GetProperty("transportAvailable").GetBoolean().Should().BeFalse();
        }
        Delegate(app, client, "refresh_inventory", Agent);
        (await client.PostAsync(Root + "/refresh-inventory/preview", null)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Cached_fresh_inventory_does_not_claim_a_connected_transport()
    {
        using var app = await BuildAsync(new RecordingAdmission(), new Preparation { Available = false });
        var client = app.GetTestClient(); Delegate(app, client, "inventory", Agent);
        var response = await client.GetAsync(Root + "/inventory");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("hasSnapshot").GetBoolean().Should().BeTrue();
        json.GetProperty("fresh").GetBoolean().Should().BeTrue();
        json.GetProperty("transportAvailable").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_preview_has_no_dispatch_and_replay_never_enqueues_again(bool closeChannel)
    {
        var preparation = new Preparation { CloseChannel = closeChannel };
        var admission = new RecordingAdmission();
        using var app = await BuildAsync(admission, preparation);
        var client = app.GetTestClient(); Delegate(app, client, "refresh_inventory", Agent);
        var preview = await client.PostAsync(Root + "/refresh-inventory/preview", null);
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        preparation.Refreshes.Should().Be(0);
        admission.Recorded.Should().BeEmpty();
        var plan = await preview.Content.ReadFromJsonAsync<JsonElement>();
        var request = new { planToken = plan.GetProperty("planToken").GetString(), idempotencyKey = plan.GetProperty("idempotencyKey").GetString() };
        var expected = closeChannel ? HttpStatusCode.GatewayTimeout : HttpStatusCode.OK;
        (await client.PostAsJsonAsync(Root + "/refresh-inventory/confirm", request)).StatusCode.Should().Be(expected);
        (await client.PostAsJsonAsync(Root + "/refresh-inventory/confirm", request)).StatusCode.Should().Be(expected);
        preparation.Refreshes.Should().Be(1);
        admission.Recorded.Should().ContainSingle();
    }

    [Fact]
    public async Task Refresh_without_transport_is_not_previewed_as_ready()
    {
        var preparation = new Preparation { Available = false };
        using var app = await BuildAsync(new RecordingAdmission(), preparation);
        var client = app.GetTestClient(); Delegate(app, client, "refresh_inventory", Agent);
        (await client.PostAsync(Root + "/refresh-inventory/preview", null)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        preparation.Refreshes.Should().Be(0);
    }

    private static void Delegate(IHost app, HttpClient client, string operation, Guid target)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("M2M");
        client.DefaultRequestHeaders.Remove(McpOperatorDelegationOptions.HeaderName);
        client.DefaultRequestHeaders.Add(McpOperatorDelegationOptions.HeaderName,
            app.Services.GetRequiredService<McpOperatorDelegationTokenService>().Create(
                new McpOperatorDelegationIdentity("operator@example.test", "operator-client", "operator-client", [], ["Operator"], ["netratel.mcp.observe", "netratel.mcp.execute"]),
                new McpOperatorDelegationRequest("netratel_remote_support_v2", operation, "remote-support-test",
                    "https://mcp.dev.example/mcp", "dev", 42, target, "remote-support-test-correlation")));
    }

    private static async Task<IHost> BuildAsync(RecordingAdmission admission, Preparation preparation, bool inventoryEnabled = true)
    {
        return await Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Development).UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("M2M").AddScheme<AuthenticationSchemeOptions, TestM2mAuthenticationHandler>("M2M", _ => { });
                services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser()));
                services.AddSingleton(new McpOperatorDelegationOptions { Enabled = true, Issuer = "netratel-mcp-dev",
                    Audience = "netratel-api-dev", ServicePrincipal = "netratel-mcp-http-dev", KeyId = "test-key",
                    SharedKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("delegation-test-key-must-be-at-least-32-bytes")), LifetimeSeconds = 90 });
                services.AddSingleton<McpOperatorDelegationTokenService>();
                services.AddSingleton<IMcpOperatorRouteAdmission>(admission);
                services.AddSingleton<IClientPresenceRouter>(new TestPresence(new ClientKey(42, Agent)));
                if (inventoryEnabled) services.AddSingleton<IRemoteSupportV2PreparationRegistry>(preparation);
                services.AddSingleton<IMcpOperatorConfirmationService>(new Confirmations());
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton(new NetRatelAkkaMigrationOptions { Enabled = true, PresenceAuthorityEnabled = true,
                    RemoteSupportGatewayEnabled = true, RemoteSupportAuthorityEnabled = true, RemoteSupportV2InventoryEnabled = inventoryEnabled });
            });
            web.Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseMiddleware<McpOperatorDelegationMiddleware>(); app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapMcpOperatorRemoteSupportEndpoints());
            });
        }).StartAsync();
    }

    private sealed class Preparation : IRemoteSupportV2PreparationRegistry
    {
        public bool Available { get; init; } = true;
        public bool CloseChannel { get; init; }
        public int Refreshes { get; private set; }
        public RemoteSupportTargetInventoryProjection? GetInventory(ClientKey client) => new(
            new(2, client.TenantId, client.AgentId, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), []),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        public RemoteSupportV2CapabilitySnapshot? GetCapabilities(ClientKey client) => Available
            ? new(client.TenantId, client.AgentId, Guid.NewGuid(), 1, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)) : null;
        public Task RequestInventoryRefreshAsync(ClientKey client, CancellationToken cancellationToken)
        {
            Refreshes++;
            return CloseChannel ? Task.FromException(new ChannelClosedException()) : Task.CompletedTask;
        }
        public Task<RemoteSupportPreparedTargetResult> PrepareAsync(ClientKey client, RemoteSupportOperatorBinding binding, RemoteSupportTargetDescriptor target, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoteSupportPreparedTargetResult> PrepareMediaAsync(RemoteSupportSessionKey session, RemoteSupportOperatorBinding binding, RemoteSupportTargetDescriptor target, CancellationToken cancellationToken) => throw new NotSupportedException();
        public RemoteSupportV2PreparationRegistration Register(ClientKey client, Guid connectionId, ulong epoch, string protocol, IReadOnlyList<string> capabilities) => throw new NotSupportedException();
        public bool TryReceiveInventory(ClientKey client, RemoteSupportV2InventorySnapshot snapshot) => throw new NotSupportedException();
        public bool TryCompletePreparation(ClientKey client, RemoteSupportV2PreparedTarget target) => throw new NotSupportedException();
    }

    private sealed class Confirmations : IMcpOperatorConfirmationService
    {
        private readonly Guid _id = Guid.NewGuid();
        private McpOperatorIdempotencyOutcome? _outcome;
        private string? _result;
        public Task<McpOperatorConfirmationPlan> CreatePlanAsync(McpOperatorConfirmationPlanRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new McpOperatorConfirmationPlan(new string('p', 32), new string('i', 32), DateTimeOffset.UtcNow.AddMinutes(1), McpOperatorConfirmationClass.RemoteExecution, request.PayloadHash, null));
        public Task<McpOperatorConfirmationAdmission> ConfirmAsync(McpOperatorConfirmationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new McpOperatorConfirmationAdmission(_outcome is null, _outcome is not null, null, _id, _outcome, _result));
        public Task CompleteAsync(Guid id, McpOperatorIdempotencyOutcome outcome, string? result, CancellationToken cancellationToken)
        { _outcome = outcome; _result = result; return Task.CompletedTask; }
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
