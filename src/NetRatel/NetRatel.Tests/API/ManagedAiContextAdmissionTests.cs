using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Security.Integration;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ManagedAiContextAdmissionTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("get")]
    [InlineData("send")]
    [InlineData("respond")]
    [InlineData("reconcile")]
    [InlineData("archive")]
    public async Task Client_policy_deny_overrides_tenant_allow_for_every_investigation_operation(string operation)
    {
        await using var scenario = await Scenario.CreateAsync(operation);
        scenario.Investigation.AgentIds = [scenario.AllowedAgent, scenario.DeniedAgent];
        await scenario.DenyAsync(scenario.DeniedAgent);

        var result = await scenario.InvokeAsync(scenario.Request(), confirm: true);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(403);
        (await scenario.Db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
        (await scenario.Db.Set<AiInvestigationRecord>().CountAsync()).Should().Be(1);
        (await scenario.Db.Set<AiInvestigationEventRecord>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Cached_offline_context_is_admitted_but_policy_revocation_before_acceptance_blocks_dispatch()
    {
        await using var scenario = await Scenario.CreateAsync("send");
        var admitted = await scenario.Admission.EvaluateAsync(scenario.Descriptor, scenario.Request(), scenario.Http, default);
        admitted.FailureCode.Should().BeNull();
        admitted.ContextRoutes.Should().ContainSingle().Which.TargetOnline.Should().BeFalse();
        admitted.ContextRoutes[0].Operation.Should().Be("send");
        admitted.ContextRoutes[0].RequiredScopes.Should().Equal("netratel.mcp.execute");

        await scenario.DenyAsync(scenario.AllowedAgent);
        var error = await Assert.ThrowsAsync<McpOperatorAdmissionRejectedException>(() => scenario.Admission.RecordAcceptedAsync(admitted, default));

        error.FailureCode.Should().Be("target_policy_denied");
        (await scenario.Db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_cannot_include_a_client_from_another_tenant()
    {
        await using var scenario = await Scenario.CreateAsync("create");
        scenario.Investigation.AgentIds = [scenario.ForeignAgent];
        var result = await scenario.Admission.EvaluateAsync(scenario.Descriptor, scenario.Request(), scenario.Http, default);
        result.FailureCode.Should().Be("target_not_found");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("get")]
    public async Task Alert_linked_context_requires_monitoring_read_before_review(string operation)
    {
        await using var scenario = await Scenario.CreateAsync(operation);
        scenario.Investigation.OccurrenceId = Guid.NewGuid();
        await scenario.Db.SaveChangesAsync();
        scenario.Access.Permissions.Remove(NetRatelPermissions.MonitoringRead);

        var result = await scenario.Admission.EvaluateAsync(scenario.Descriptor, scenario.Request(), scenario.Http, default);

        result.FailureCode.Should().Be("permission_denied");
    }

    [Fact]
    public async Task Client_discovery_filters_current_policy_on_its_exact_operation()
    {
        await using var scenario = await Scenario.CreateAsync("clients");
        await scenario.DenyAsync(scenario.DeniedAgent);
        await scenario.DenyAsync(scenario.AllowedAgent, operation: "send");

        var result = await scenario.InvokeAsync(scenario.Request(), confirm: false);

        var clients = Assert.IsAssignableFrom<IReadOnlyList<InvestigationClientDto>>(((IValueHttpResult)result).Value);
        clients.Should().ContainSingle().Which.AgentId.Should().Be(scenario.AllowedAgent);
    }

    [Fact]
    public async Task Investigation_discovery_omits_whole_denied_context_and_preserves_bounded_page_offsets()
    {
        await using var scenario = await Scenario.CreateAsync("list");
        scenario.Investigation.LastActivityUtc = DateTimeOffset.UtcNow.AddDays(-1);
        for (var index = 0; index < 100; index++)
            scenario.Db.Add(new AiInvestigationRecord
            {
                TenantId = 7, RequestId = Guid.NewGuid(), Title = "Denied context",
                AgentIds = [scenario.AllowedAgent, scenario.DeniedAgent], LastActivityUtc = DateTimeOffset.UtcNow.AddMinutes(-index)
            });
        await scenario.DenyAsync(scenario.DeniedAgent);

        var first = await scenario.InvokeAsync(scenario.Request(), confirm: false);
        var firstPage = Assert.IsType<ManagedAiInvestigationPage>(((IValueHttpResult)first).Value);
        firstPage.Items.Should().BeEmpty();
        firstPage.HasMore.Should().BeTrue();
        firstPage.NextSkip.Should().Be(100);
        var nextRequest = JsonSerializer.SerializeToElement(new { tenantId = 7, skip = firstPage.NextSkip });
        var next = await scenario.InvokeAsync(nextRequest, confirm: false);
        var nextPage = Assert.IsType<ManagedAiInvestigationPage>(((IValueHttpResult)next).Value);
        nextPage.Items.Should().ContainSingle().Which.Id.Should().Be(scenario.Investigation.Id);
        nextPage.HasMore.Should().BeFalse();
        nextPage.NextSkip.Should().BeNull();
    }

    [Fact]
    public async Task Removed_context_client_is_re_resolved_from_persistence()
    {
        await using var scenario = await Scenario.CreateAsync("get");
        var agent = await scenario.Db.Agents.SingleAsync(row => row.Id == scenario.AllowedAgent);
        agent.DeletedAtUtc = DateTimeOffset.UtcNow;
        await scenario.Db.SaveChangesAsync();

        var result = await scenario.Admission.EvaluateAsync(scenario.Descriptor, scenario.Request(), scenario.Http, default);

        result.FailureCode.Should().Be("target_not_found");
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public Guid AllowedAgent { get; } = Guid.NewGuid();
        public Guid DeniedAgent { get; } = Guid.NewGuid();
        public Guid ForeignAgent { get; } = Guid.NewGuid();
        public OrchestratorDbContext Db { get; } = new(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        public CurrentAccess Access { get; } = new();
        public required ManagedOperationDescriptor Descriptor { get; init; }
        public ManagedOperationAdmission Admission { get; private set; } = null!;
        public DefaultHttpContext Http { get; } = new() { TraceIdentifier = "ai-context-request" };
        public AiInvestigationRecord Investigation { get; private set; } = null!;
        private ServiceProvider _provider = null!;

        public static async Task<Scenario> CreateAsync(string operation)
        {
            var scenario = new Scenario { Descriptor = ManagedOperationCatalog.Find("netratel_ai", operation)! };
            scenario.Db.Tenants.AddRange(new Tenant { Id = 7, Name = "Selected tenant" }, new Tenant { Id = 8, Name = "Other tenant" });
            foreach (var agent in new[] { scenario.AllowedAgent, scenario.DeniedAgent, scenario.ForeignAgent })
            {
                var tenant = agent == scenario.ForeignAgent ? 8 : 7;
                scenario.Db.Agents.Add(new Agent { Id = agent, TenantId = tenant, Name = agent.ToString(), DeviceInfoJson = "{}" });
                scenario.Db.McpOperatorTargetProfiles.Add(new McpOperatorTargetProfileRecord
                {
                    AgentId = agent, TenantId = tenant, Classification = McpOperatorTargetClassification.ManagedStandard,
                    TagsJson = "[]", UpdatedBy = "policy-admin", Version = 1
                });
            }
            scenario.Db.McpOperatorPolicies.Add(scenario.Policy(McpOperatorPolicyEffect.Allow, null, operation));
            scenario.Investigation = new AiInvestigationRecord
            {
                TenantId = 7, RequestId = Guid.NewGuid(), Title = "Investigate availability", AgentIds = [scenario.AllowedAgent],
                LastActivityUtc = DateTimeOffset.UtcNow, CreatedBy = "owner-1"
            };
            scenario.Db.Add(scenario.Investigation);
            await scenario.Db.SaveChangesAsync();
            var policy = new McpOperatorAuthorization(scenario.Db);
            scenario.Admission = new(scenario.Access, new NoExternalIdentity(), policy,
                new McpOperatorRouteAdmissionService(scenario.Db, policy), scenario.Db, new OfflinePresence(),
                new HostingEnvironment { EnvironmentName = "Production" });
            scenario._provider = new ServiceCollection().AddSingleton<IEffectiveAccessService>(scenario.Access)
                .AddSingleton(scenario.Db).AddSingleton(new InvestigationStore(scenario.Db, TimeProvider.System)).BuildServiceProvider();
            scenario.Http.RequestServices = scenario._provider;
            scenario.Http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("netratel_principal_id", "owner-1"),
                new Claim(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType, "credential-1"),
                new Claim("auth_mode", "integration_credential"), new Claim("integration_credential_purpose", "api")
            ], IntegrationCredentialAuthenticationHandler.SchemeName));
            return scenario;
        }

        public async Task DenyAsync(Guid agentId, string? operation = null)
        {
            Db.McpOperatorPolicies.Add(Policy(McpOperatorPolicyEffect.Deny, agentId, operation ?? Descriptor.Operation));
            await Db.SaveChangesAsync();
        }

        private McpOperatorPolicyRecord Policy(McpOperatorPolicyEffect effect, Guid? agentId, string operation) => new()
        {
            Id = Guid.NewGuid(), Name = "Current AI operation policy", Environment = McpOperatorEnvironment.Production, Effect = effect,
            PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject, PrincipalSelectorValue = "owner-1",
            TargetSelectorKind = agentId is null ? McpOperatorTargetSelectorKind.Tenant : McpOperatorTargetSelectorKind.ExactAgent,
            TenantId = 7, AgentId = agentId, OperationFamily = McpOperatorOperationCatalog.Find("netratel_ai", operation)!.OperationFamily,
            Operation = $"netratel_ai/{operation}", ConstraintsJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "policy-admin", Version = 1
        };

        public JsonElement Request()
        {
            var request = new JsonObject { ["tenantId"] = 7 };
            if (Descriptor.IdProperty is { } idProperty) request[idProperty] = Investigation.Id;
            if (Descriptor.ApprovalTarget) request["callId"] = "approval-1";
            object? data = Descriptor.Operation switch
            {
                "create" => new InvestigationCreateRequest(Guid.NewGuid(), "A new investigation", Investigation.AgentIds, Investigation.OccurrenceId),
                "send" => new InvestigationMessageRequest(Guid.NewGuid(), "Check the selected clients."),
                "respond" => new InvestigationApprovalRequest("allow"),
                "archive" => new InvestigationArchiveRequest(Guid.Empty),
                _ => null
            };
            if (data is not null) request["data"] = JsonSerializer.SerializeToNode(data, data.GetType(), ManagedOperationCatalog.JsonOptions);
            return JsonSerializer.SerializeToElement(request, ManagedOperationCatalog.JsonOptions);
        }

        public async Task<IResult> InvokeAsync(JsonElement request, bool confirm)
        {
            Http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request, confirm })));
            return await ManagedOperationEndpoints.InvokeAsync(Descriptor.Tool, Descriptor.Operation, Http, Admission, default);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private sealed class CurrentAccess : IEffectiveAccessService
    {
        public HashSet<string> Permissions { get; } = new(StringComparer.Ordinal)
        {
            NetRatelPermissions.TelemetryRead, NetRatelPermissions.ClientManagement,
            NetRatelPermissions.SecretUse, NetRatelPermissions.MonitoringRead
        };
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(principal.FindFirstValue("netratel_principal_id") == "owner-1" && tenantId == 7 && Permissions.Contains(permission));
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSnapshot("owner-1", false, false, Permissions));
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoExternalIdentity : IApplicationPrincipalResolver
    {
        public Task<string?> ResolveExternalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class OfflinePresence : IClientPresenceReadModel
    {
        public Task<ClientPresenceReadModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new ClientPresenceReadModelSnapshot(0, []));
        public Task<ClientPresenceSnapshot?> GetClientSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => Task.FromResult<ClientPresenceSnapshot?>(null);
    }
}
