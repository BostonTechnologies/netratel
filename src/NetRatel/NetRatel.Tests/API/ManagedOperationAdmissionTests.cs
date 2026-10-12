using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.API.Security.Integration;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ManagedOperationAdmissionTests
{
    [Fact]
    public async Task Api_credential_preview_changes_nothing_and_revocation_blocks_confirmation()
    {
        await using var scenario = await Scenario.CreateAsync("netratel_clients", "set_metadata");
        var request = JsonSerializer.SerializeToElement(new { tenantId = 7, agentId = scenario.AgentId,
            data = new { friendlyName = "Updated name", tags = new[] { "linux", "production" } } });
        var before = await scenario.Db.Agents.SingleAsync();
        var preview = await scenario.InvokeAsync(request, false);
        ((IStatusCodeHttpResult)preview).StatusCode.Should().Be(200);
        before.FriendlyName.Should().BeNull();
        (await scenario.Db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
        scenario.Access.Revoked = true;
        ((IStatusCodeHttpResult)await scenario.InvokeAsync(request, true)).StatusCode.Should().Be(403);
        before.FriendlyName.Should().BeNull();
        (await scenario.Db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Confirmed_metadata_update_uses_canonical_actor_and_cannot_expand_tenant_authority()
    {
        await using var scenario = await Scenario.CreateAsync("netratel_clients", "set_metadata");
        var request = JsonSerializer.SerializeToElement(new { tenantId = 7, agentId = scenario.AgentId,
            data = new { friendlyName = "Gateway one", tags = new[] { "edge" } } });
        ((IStatusCodeHttpResult)await scenario.InvokeAsync(request, true)).StatusCode.Should().Be(200);
        (await scenario.Db.Agents.SingleAsync()).FriendlyName.Should().Be("Gateway one");
        var audit = await scenario.Db.McpOperatorAcceptedAudits.SingleAsync();
        audit.Subject.Should().Be("owner-1");
        audit.ClientId.Should().Be("credential-1");
        audit.TenantId.Should().Be(7);
        scenario.Http.User.FindFirstValue("netratel_principal_id").Should().Be("owner-1");
        var foreign = JsonSerializer.SerializeToElement(new { tenantId = 8, agentId = scenario.AgentId,
            data = new { friendlyName = "Foreign name", tags = Array.Empty<string>() } });
        ((IStatusCodeHttpResult)await scenario.InvokeAsync(foreign, true)).StatusCode.Should().Be(403);
        (await scenario.Db.Agents.SingleAsync()).FriendlyName.Should().Be("Gateway one");
    }

    [Theory]
    [InlineData("service")]
    [InlineData("http_mcp")]
    [InlineData("mixed")]
    public async Task Service_tokens_raw_MCP_credentials_and_mixed_identities_cannot_become_application_actors(string kind)
    {
        await using var scenario = await Scenario.CreateAsync("netratel_flows", "list");
        if (kind == "service") scenario.Http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("auth_mode", "service"), new Claim("netratel_principal_id", "owner-1")], "M2M"));
        else if (kind == "http_mcp") ((ClaimsIdentity)scenario.Http.User.Identity!).RemoveClaim(scenario.Http.User.FindFirst("integration_credential_purpose")!);
        else scenario.Http.User.AddIdentity(new ClaimsIdentity([new Claim("role", "Operator")]));
        if (kind == "http_mcp") ((ClaimsIdentity)scenario.Http.User.Identity!).AddClaim(new Claim("integration_credential_purpose", "http_mcp"));
        var result = await scenario.Admission.EvaluateAsync(scenario.Descriptor, Request(), scenario.Http, default);
        result.FailureCode.Should().Be("delegated_identity_invalid");
        scenario.Access.Observed.Should().BeEmpty();
    }

    [Fact]
    public async Task Signed_external_actor_is_resolved_by_issuer_and_subject_without_copying_privileged_roles()
    {
        await using var scenario = await Scenario.CreateAsync("netratel_flows", "list");
        (await scenario.Db.McpOperatorPolicies.SingleAsync()).PrincipalSelectorValue = "external-1";
        await scenario.Db.SaveChangesAsync();
        scenario.Http.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = Delegation("netratel_flows", "list", 7,
            new("external-1", "oauth-client", null, [], ["Operator", "PolicyAdministrator"], ["netratel.mcp.observe"])
            { ExternalIssuer = "https://id.example/realm" });
        var result = await scenario.Admission.EvaluateAsync(scenario.Descriptor, Request(), scenario.Http, default);
        result.FailureCode.Should().BeNull();
        scenario.Resolver.LastIssuer.Should().Be("https://id.example/realm");
        scenario.Resolver.LastSubject.Should().Be("external-1");
        result.User!.FindFirstValue("netratel_principal_id").Should().Be("owner-1");
        result.User!.FindAll(ClaimTypes.Role).Should().BeEmpty();
        result.User!.FindAll("roles").Should().BeEmpty();
        result.Decision!.Request.Principal.Roles.Should().Contain("Operator", "policy still evaluates the signed OAuth role separately");
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("operation")]
    [InlineData("issuer")]
    [InlineData("instance")]
    public async Task Delegation_mismatches_fail_before_application_data_is_read(string mismatch)
    {
        await using var scenario = await Scenario.CreateAsync("netratel_flows", "list");
        var identity = new McpOperatorDelegationIdentity("external-1", null, null, [], ["Operator"], ["netratel.mcp.observe"])
            { ExternalIssuer = mismatch == "issuer" ? null : "https://id.example/realm" };
        var delegated = Delegation("netratel_flows", mismatch == "operation" ? "publish" : "list", mismatch == "tenant" ? 8 : 7, identity);
        if (mismatch == "instance") delegated = delegated with { Instance = "dev" };
        scenario.Http.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = delegated;
        (await scenario.Admission.EvaluateAsync(scenario.Descriptor, Request(), scenario.Http, default)).FailureCode.Should().Be("delegated_identity_invalid");
        scenario.Access.Observed.Should().BeEmpty();
    }

    [Fact]
    public async Task Tenant_operations_need_a_tenant_policy_and_current_policy_is_rechecked_at_acceptance()
    {
        await using var scenario = await Scenario.CreateAsync("netratel_flows", "list");
        var policy = await scenario.Db.McpOperatorPolicies.SingleAsync();
        policy.TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent; policy.AgentId = scenario.AgentId;
        await scenario.Db.SaveChangesAsync();
        (await scenario.Admission.EvaluateAsync(scenario.Descriptor, Request(), scenario.Http, default)).FailureCode.Should().NotBeNull();
        policy.TargetSelectorKind = McpOperatorTargetSelectorKind.Tenant; policy.AgentId = null;
        await scenario.Db.SaveChangesAsync();
        var admitted = await scenario.Admission.EvaluateAsync(scenario.Descriptor, Request(), scenario.Http, default);
        admitted.FailureCode.Should().BeNull();
        policy.DisabledAtUtc = DateTimeOffset.UtcNow;
        await scenario.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<McpOperatorAdmissionRejectedException>(() => scenario.Admission.RecordAcceptedAsync(admitted, default));
        (await scenario.Db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AI_invocation_requires_secret_use_in_addition_to_client_management()
    {
        await using var scenario = await Scenario.CreateAsync("netratel_ai", "send");
        scenario.Access.Permissions.Remove("secret.use");
        (await scenario.Admission.EvaluateAsync(scenario.Descriptor, Request(), scenario.Http, default)).FailureCode.Should().Be("permission_denied");
        scenario.Access.Observed.Should().Contain(item => item.Permission == "secret.use");
    }

    [Fact]
    public async Task Cached_service_inventory_is_readable_offline_under_current_policy()
    {
        await using var scenario = await Scenario.CreateAsync("netratel_services", "inventory");
        var request = JsonSerializer.SerializeToElement(new { tenantId = 7, agentId = scenario.AgentId });
        var admitted = await scenario.Admission.EvaluateAsync(scenario.Descriptor, request, scenario.Http, default);
        admitted.FailureCode.Should().BeNull();
        admitted.Decision!.Request.TargetOnline.Should().BeFalse();
    }

    private static JsonElement Request() => JsonSerializer.SerializeToElement(new { tenantId = 7 });
    private static McpOperatorDelegation Delegation(string tool, string operation, int tenant, McpOperatorDelegationIdentity identity) =>
        new(identity, "netratel-mcp-http", tool, operation, "request-1", DateTimeOffset.UtcNow.AddMinutes(1), "https://mcp.example/mcp", "prod", tenant, CorrelationId: "correlation-1");

    private sealed class Scenario : IAsyncDisposable
    {
        public Guid AgentId { get; } = Guid.NewGuid();
        public OrchestratorDbContext Db { get; } = new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        public CurrentAccess Access { get; } = new();
        public ExternalResolver Resolver { get; } = new();
        public required ManagedOperationDescriptor Descriptor { get; init; }
        public ManagedOperationAdmission Admission { get; private set; } = null!;
        public DefaultHttpContext Http { get; } = new() { TraceIdentifier = "request-1" };
        private ServiceProvider _provider = null!;

        public static async Task<Scenario> CreateAsync(string tool, string operation)
        {
            var scenario = new Scenario { Descriptor = ManagedOperationCatalog.Find(tool, operation)! };
            scenario.Access.Permissions.UnionWith(new[] { scenario.Descriptor.Permission }.Concat(scenario.Descriptor.AdditionalPermissions));
            scenario.Db.Tenants.Add(new Tenant { Id = 7, Name = "Authorized tenant" });
            scenario.Db.Agents.Add(new Agent { Id = scenario.AgentId, TenantId = 7, Name = "gateway", IsEnabled = true, Status = AgentStatus.Active, DeviceInfoJson = "{}" });
            scenario.Db.McpOperatorTargetProfiles.Add(new McpOperatorTargetProfileRecord { AgentId = scenario.AgentId, TenantId = 7,
                Classification = McpOperatorTargetClassification.ManagedStandard, TagsJson = "[]", UpdatedBy = "policy-admin", Version = 1 });
            scenario.Db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
            {
                Id = Guid.NewGuid(), Name = "Exact operation", Environment = McpOperatorEnvironment.Production, Effect = McpOperatorPolicyEffect.Allow,
                PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject, PrincipalSelectorValue = "owner-1",
                TargetSelectorKind = McpOperatorTargetSelectorKind.Tenant, TenantId = 7,
                OperationFamily = McpOperatorOperationCatalog.Find(tool, operation)!.OperationFamily, Operation = $"{tool}/{operation}",
                ConstraintsJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "policy-admin", Version = 1
            });
            await scenario.Db.SaveChangesAsync();
            var policy = new McpOperatorAuthorization(scenario.Db);
            scenario.Admission = new(scenario.Access, scenario.Resolver, policy, new McpOperatorRouteAdmissionService(scenario.Db, policy), scenario.Db,
                new OfflinePresence(), new HostingEnvironment { EnvironmentName = "Production" });
            scenario._provider = new ServiceCollection().AddSingleton<IEffectiveAccessService>(scenario.Access).AddSingleton(scenario.Db)
                .AddSingleton(TimeProvider.System).BuildServiceProvider();
            scenario.Http.RequestServices = scenario._provider;
            scenario.Http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("netratel_principal_id", "owner-1"), new Claim(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType, "credential-1"),
                new Claim("auth_mode", "integration_credential"), new Claim("integration_credential_purpose", "api")], IntegrationCredentialAuthenticationHandler.SchemeName));
            return scenario;
        }

        public async Task<IResult> InvokeAsync(JsonElement request, bool confirm)
        {
            Http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request, confirm })));
            return await ManagedOperationEndpoints.InvokeAsync(Descriptor.Tool, Descriptor.Operation, Http, Admission, default);
        }
        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    private sealed class CurrentAccess : IEffectiveAccessService
    {
        public bool Revoked { get; set; }
        public HashSet<string> Permissions { get; } = new(StringComparer.Ordinal);
        public List<(string? Actor, string Permission, int? Tenant)> Observed { get; } = [];
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default)
        {
            var actor = principal.FindFirstValue("netratel_principal_id");
            Observed.Add((actor, permission, tenantId));
            return Task.FromResult(!Revoked && actor == "owner-1" && tenantId == 7 && Permissions.Contains(permission));
        }
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSnapshot("owner-1", false, false, Permissions));
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ExternalResolver : IApplicationPrincipalResolver
    {
        public string? LastIssuer { get; private set; }
        public string? LastSubject { get; private set; }
        public Task<string?> ResolveExternalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            LastIssuer = principal.FindFirstValue("iss"); LastSubject = principal.FindFirstValue("sub");
            return Task.FromResult<string?>(LastIssuer == "https://id.example/realm" && LastSubject == "external-1" ? "owner-1" : null);
        }
    }

    private sealed class OfflinePresence : IClientPresenceReadModel
    {
        public Task<ClientPresenceReadModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new ClientPresenceReadModelSnapshot(0, []));
        public Task<ClientPresenceSnapshot?> GetClientSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => Task.FromResult<ClientPresenceSnapshot?>(null);
    }
}
