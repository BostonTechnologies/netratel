using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Services.ComputerUse;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.ComputerUse;
using NetRatel.Shared.Contracts.RemoteSupport;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ComputerUseOperatorEndpointTests
{
    private static readonly Guid Agent = Guid.Parse("67adb9f6-b12d-45cd-a3f9-7aa4a0e70442");
    private static readonly Guid Connection = Guid.Parse("23804a3b-9bdd-46e6-9eb9-e6bcb9785611");
    private static readonly ComputerUseTarget Target = new(3, "fixture-user-hash", 100, "window-100");
    private const string View = "netratel.mcp.cua.view";
    private const string Control = "netratel.mcp.cua.control";

    [Fact]
    public async Task Default_disabled_and_mismatched_delegation_have_durable_refusals_without_dispatch()
    {
        await using var fixture = new Fixture(enabled: false);
        var disabled = await fixture.Service.ReadTargetAsync(7, Agent, "capabilities", new(Target, "cua_driver"), Context("capabilities"), default);
        Status(disabled).Should().Be(503);
        var wrongTenant = Context("capabilities", tenantId: 8);
        Status(await fixture.Service.ReadTargetAsync(7, Agent, "capabilities", new(Target, "cua_driver"), wrongTenant, default)).Should().Be(401);
        fixture.Gateway.Commands.Should().BeEmpty();
        (await fixture.Db.ComputerUseAudits.Select(row => row.Code).ToArrayAsync()).Should().BeEquivalentTo("computer_use_disabled", "delegated_identity_invalid");
    }

    [Fact]
    public async Task Cross_tenant_policy_denial_and_terminal_only_scope_do_not_grant_desktop_access()
    {
        await using var fixture = new Fixture();
        var crossTenant = await fixture.Service.ReadTargetAsync(8, Agent, "capabilities", new(Target, "cua_driver"), Context("capabilities", tenantId: 8), default);
        Status(crossTenant).Should().Be(403);
        var terminalScope = Context("open_view", scopes: ["netratel.mcp.execute"]);
        var open = await fixture.Service.OpenViewAsync(7, Agent, new(Target, "cua_driver", new string('k', 32)), terminalScope, default);
        Status(open).Should().Be(403);
        fixture.Gateway.Commands.Should().BeEmpty();
        (await fixture.Db.ComputerUseAudits.Select(row => row.Code).ToArrayAsync()).Should().BeEquivalentTo("tenant_not_authorized", "oauth_scope_missing");
    }

    [Fact]
    public async Task Exact_interactive_identity_and_fresh_inventory_are_required_before_provider_dispatch()
    {
        await using var fixture = new Fixture();
        var result = await fixture.Service.ReadTargetAsync(7, Agent, "list_windows", new(Target with { WindowsSessionId = 4 }, "cua_driver"), Context("list_windows"), default);
        Status(result).Should().Be(409);
        fixture.Preparation.Stale = true;
        result = await fixture.Service.ReadTargetAsync(7, Agent, "list_windows", new(Target, "cua_driver"), Context("list_windows"), default);
        Status(result).Should().Be(409);
        fixture.Gateway.Commands.Should().BeEmpty();
        (await fixture.Db.ComputerUseAudits.Select(row => row.Code).ToArrayAsync()).Should().BeEquivalentTo("computer_use_session_unavailable", "computer_use_inventory_stale");
    }

    [Fact]
    public async Task Pure_view_lease_observes_but_never_accepts_input_or_another_owner()
    {
        await using var fixture = new Fixture();
        var opened = await fixture.Service.OpenViewAsync(7, Agent, new(Target, "cua_driver", new string('k', 32)), Context("open_view", scopes: [View]), default);
        var id = Json(opened).GetProperty("session").GetProperty("sessionId").GetGuid();
        fixture.Gateway.Commands.Single().ControlAllowed.Should().BeFalse();
        var observed = await fixture.Service.ObserveAsync(7, Agent, id, new(), Context("observe", scopes: [View]), default);
        Json(observed).GetProperty("result").GetProperty("status").GetString().Should().Be(ComputerUseStatuses.Observed);
        var hidden = await fixture.Service.GetAsync(7, Agent, id, Context("get", subject: "other@example.test"), default);
        Status(hidden).Should().Be(404);
        var action = await fixture.Service.PreviewActAsync(7, Agent, id, new("observation-1", new("key", Key: "Enter")), Context("preview_act"), default);
        Status(action).Should().Be(409);
        fixture.Gateway.Commands.Should().HaveCount(2);
    }

    [Fact]
    public async Task Preview_is_inert_action_replay_is_at_most_once_and_unverified_effect_remains_explicit()
    {
        await using var fixture = new Fixture();
        var openPreview = await fixture.Service.PreviewOpenAsync(7, Agent, new(Target, "cua_driver"), Context("preview_open"), default);
        fixture.Gateway.Commands.Should().BeEmpty();
        var openPlan = Json(openPreview);
        var opened = await fixture.Service.OpenAsync(7, Agent, new(Target, "cua_driver", Text(openPlan, "planToken"), Text(openPlan, "idempotencyKey")), Context("open"), default);
        var id = Json(opened).GetProperty("session").GetProperty("sessionId").GetGuid();
        var payload = new ComputerUseAction("type", ElementId: "entry-1", Text: "private-fixture-value");
        var preview = await fixture.Service.PreviewActAsync(7, Agent, id, new("observation-1", payload), Context("preview_act"), default);
        fixture.Gateway.Commands.Should().ContainSingle();
        var plan = Json(preview);
        var request = new ComputerUseActConfirmRequest("observation-1", payload, Text(plan, "planToken"), Text(plan, "idempotencyKey"));
        var action = await fixture.Service.ActAsync(7, Agent, id, request, Context("act"), default);
        Json(action).GetProperty("result").GetProperty("status").GetString().Should().Be(ComputerUseStatuses.AppliedUnverified);
        Json(action).GetProperty("result").GetProperty("mayHaveApplied").GetBoolean().Should().BeTrue();
        var replay = await fixture.Service.ActAsync(7, Agent, id, request, Context("act"), default);
        Json(replay).GetProperty("replayed").GetBoolean().Should().BeTrue();
        fixture.Gateway.Commands.Should().HaveCount(2);
        JsonSerializer.Serialize(await fixture.Db.ComputerUseAudits.ToArrayAsync()).Should().NotContain("private-fixture-value");
    }

    [Fact]
    public async Task Direct_api_rejects_arbitrary_key_chords_and_audits_the_refusal()
    {
        await using var fixture = new Fixture();
        var preview = Json(await fixture.Service.PreviewOpenAsync(7, Agent, new(Target, "cua_driver"), Context("preview_open"), default));
        var opened = Json(await fixture.Service.OpenAsync(7, Agent, new(Target, "cua_driver", Text(preview, "planToken"), Text(preview, "idempotencyKey")), Context("open"), default));
        var id = opened.GetProperty("session").GetProperty("sessionId").GetGuid();
        var refused = await fixture.Service.PreviewActAsync(7, Agent, id, new("observation-1", new("key", Key: "Ctrl+Alt+Delete")), Context("preview_act"), default);
        Status(refused).Should().Be(400);
        (await fixture.Db.ComputerUseAudits.AnyAsync(row => row.Code == "unsupported_key" && row.Outcome == "refused")).Should().BeTrue();
        fixture.Gateway.Commands.Should().ContainSingle();
    }

    private static HttpContext Context(string operation, int tenantId = 7, string subject = "operator@example.test", string[]? scopes = null)
    {
        var http = new DefaultHttpContext();
        http.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = new McpOperatorDelegation(
            new(subject, "fixture-client", "fixture-client", [], ["AutomationOperator"], scopes ?? [View, Control]),
            "fixture-mcp", "netratel_computer_use", operation, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(1),
            "https://mcp.example.test/mcp", "dev", tenantId, Agent, "fixture-correlation");
        return http;
    }
    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 200;
    private static JsonElement Json(IResult result) => JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, ComputerUseContract.JsonOptions);
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;

    private sealed class Fixture(bool enabled = true) : IAsyncDisposable
    {
        public OrchestratorDbContext Db { get; } = new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public RecordingGateway Gateway { get; } = new();
        public Preparation Preparation { get; } = new();
        private ComputerUseOperatorService? _service;
        public ComputerUseOperatorService Service => _service ??= new(new Admission(), new Confirmations(), new ComputerUseSessionStore(Db), Gateway,
            new Presence(), new Controls(), Preparation, new Environment(), TimeProvider.System, new Options(enabled));
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class Options(bool enabled) : IOptionsMonitor<ComputerUseOptions>
    {
        public ComputerUseOptions CurrentValue { get; } = new() { Enabled = enabled };
        public ComputerUseOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ComputerUseOptions, string?> listener) => null;
    }
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ComputerUseTests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class RecordingGateway : IComputerUseGateway
    {
        public List<ComputerUseCommand> Commands { get; } = [];
        public Task<ComputerUseResult> ExecuteAsync(ClientKey client, Guid connectionId, ulong epoch, ComputerUseCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            var status = command.Operation == "observe" ? ComputerUseStatuses.Observed : command.Operation == "act" ? ComputerUseStatuses.AppliedUnverified : ComputerUseStatuses.Completed;
            return Task.FromResult(new ComputerUseResult(1, command.OperationId, command.SessionId, status, Provider: command.Provider, MayHaveApplied: command.Operation == "act"));
        }
    }
    private sealed class Admission : IMcpOperatorRouteAdmission
    {
        private static readonly Guid Policy = Guid.NewGuid();
        public Task<McpOperatorRouteAdmission> EvaluateAsync(McpOperatorRouteAccessRequest request, CancellationToken ct)
        {
            var descriptor = McpOperatorOperationCatalog.Find(request.Tool, request.Operation)!;
            var access = new McpOperatorAccessRequest(request.Environment, request.Principal, request.TenantId, request.AgentId,
                McpOperatorTargetClassification.DevelopmentSafe, descriptor.OperationFamily, $"{request.Tool}/{request.Operation}",
                request.RequiredScopes, descriptor.ConfirmationClass, request.CorrelationId, request.RequestId, "fixture-digest", null,
                request.McpResource, request.McpInstance, request.Tool);
            var code = request.TenantId != 7 ? "tenant_not_authorized" : !request.RequiredScopes.IsSubsetOf(request.Principal.Scopes)
                ? "oauth_scope_missing" : !request.TargetOnline ? "target_offline" : !request.CapabilityAvailable ? "capability_unavailable" : null;
            var decision = code is null ? new McpOperatorDecision(true, null, null, [Policy], new(), "fixture-digest", access, 1)
                : McpOperatorDecision.Denied(access, code, McpOperatorAuthorizationLayer.Policy);
            return Task.FromResult(new McpOperatorRouteAdmission(decision, descriptor));
        }
        public async Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorRouteAccessRequest request, CancellationToken ct)
        {
            var admission = await EvaluateAsync(request, ct);
            if (!admission.Decision.IsAllowed) throw new McpOperatorAdmissionRejectedException(admission.Decision.FailureCode!);
            return new(Guid.NewGuid(), Policy, request.Environment, request.ServicePrincipal, request.Principal.Subject,
                request.Principal.ClientId, request.Principal.AuthorizedParty, [], [], request.Principal.Scopes.ToArray(),
                request.McpResource, request.McpInstance, request.Tool, request.TenantId, request.AgentId,
                admission.Operation!.OperationFamily, $"{request.Tool}/{request.Operation}", request.CorrelationId, request.RequestId, DateTimeOffset.UtcNow);
        }
    }
    private sealed class Confirmations : IMcpOperatorConfirmationService
    {
        private readonly Dictionary<string, (Guid Id, string Key, string Hash, McpOperatorIdempotencyOutcome? Outcome)> _plans = [];
        public Task<McpOperatorConfirmationPlan> CreatePlanAsync(McpOperatorConfirmationPlanRequest request, CancellationToken ct)
        {
            var token = Guid.NewGuid().ToString("N");
            var key = Guid.NewGuid().ToString("N");
            _plans[token] = (Guid.NewGuid(), key, request.PayloadHash, null);
            return Task.FromResult(new McpOperatorConfirmationPlan(token, key, DateTimeOffset.UtcNow.AddMinutes(1), McpOperatorConfirmationClass.RemoteExecution, request.PayloadHash, "fixture-digest"));
        }
        public Task<McpOperatorConfirmationAdmission> ConfirmAsync(McpOperatorConfirmationRequest request, CancellationToken ct)
        {
            if (!_plans.TryGetValue(request.PlanToken, out var plan) || plan.Key != request.IdempotencyKey || plan.Hash != request.PayloadHash)
                return Task.FromResult(new McpOperatorConfirmationAdmission(false, false, "confirmation_plan_invalid", null, null, null));
            return Task.FromResult(new McpOperatorConfirmationAdmission(plan.Outcome is null, plan.Outcome is not null, null, plan.Id, plan.Outcome, null));
        }
        public Task CompleteAsync(Guid id, McpOperatorIdempotencyOutcome outcome, string? result, CancellationToken ct)
        {
            var found = _plans.Single(item => item.Value.Id == id);
            _plans[found.Key] = (id, found.Value.Key, found.Value.Hash, outcome);
            return Task.CompletedTask;
        }
    }
    private sealed class Controls : IAgentControlSessionRegistry
    {
        public AgentControlAvailability? GetAvailability(ClientKey client) => new(Connection, 2, [ComputerUseContract.Capability]);
        public bool IsAvailable(ClientKey client) => true;
        public bool TryGetLatestPing(ClientKey client, out AgentControlPingResult result) { result = null!; return false; }
        public AgentControlSessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool provisional = false) => throw new NotSupportedException();
        public Task<AgentControlPingResult> RequestPingAsync(ClientKey client, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Presence : IClientPresenceRouter
    {
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken ct) => Task.FromResult(new ClientPresenceSnapshot(client, ClientPresenceStatus.Online, 2, Connection, 1, DateTimeOffset.UtcNow, "fixture", [ComputerUseContract.Capability], null, "fixture", true));
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken ct) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken ct) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Preparation : IRemoteSupportV2PreparationRegistry
    {
        public bool Stale { get; set; }
        public RemoteSupportTargetInventoryProjection? GetInventory(ClientKey client)
        {
            var now = DateTimeOffset.UtcNow;
            return new(new(1, client.TenantId, client.AgentId, 1, now, now.AddSeconds(Stale ? -1 : 60),
                [new(Target.WindowsSessionId, "active", Target.UserSidHash, true, true, false, false, true, true, "fixture")]), now, now.AddSeconds(Stale ? -1 : 60));
        }
        public RemoteSupportV2CapabilitySnapshot? GetCapabilities(ClientKey client) => null;
        public Task RequestInventoryRefreshAsync(ClientKey client, CancellationToken ct) => Task.CompletedTask;
        public Task<RemoteSupportPreparedTargetResult> PrepareAsync(ClientKey client, RemoteSupportOperatorBinding binding, RemoteSupportTargetDescriptor target, CancellationToken ct) => throw new NotSupportedException();
        public Task<RemoteSupportPreparedTargetResult> PrepareMediaAsync(RemoteSupportSessionKey session, RemoteSupportOperatorBinding binding, RemoteSupportTargetDescriptor target, CancellationToken ct) => throw new NotSupportedException();
        public RemoteSupportV2PreparationRegistration Register(ClientKey client, Guid connectionId, ulong epoch, string protocol, IReadOnlyList<string> capabilities) => throw new NotSupportedException();
        public bool TryReceiveInventory(ClientKey client, RemoteSupportV2InventorySnapshot snapshot) => throw new NotSupportedException();
        public bool TryCompletePreparation(ClientKey client, RemoteSupportV2PreparedTarget target) => throw new NotSupportedException();
    }
}
