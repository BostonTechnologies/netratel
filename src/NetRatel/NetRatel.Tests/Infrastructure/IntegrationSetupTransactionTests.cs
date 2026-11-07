using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class IntegrationSetupTransactionTests(PostgreSqlPersistenceFixture postgres)
{
    private const string Owner = "aabbccddeeff00112233445566778899";
    private const string Resource = "https://mcp.example.test/mcp";

    [Theory]
    [InlineData(true, true, 3)]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 1)]
    public async Task Setup_commits_exact_base_count_and_replays_one_protected_handoff(bool tenants, bool server, int expected)
    {
        var connection = await InitializeAsync();
        var protection = new EphemeralDataProtectionProvider();
        var expiry = ExactExpiry();
        var request = new IntegrationCredentialCreateRequest("Customer connection", IntegrationCredentialPurpose.HttpMcp, expiry,
            tenants ? [new(701, NetRatelPermissions.FlowRead), new(702, NetRatelPermissions.FlowRead)] : [], Resource,
            server ? [NetRatelPermissions.McpDiscoveryRead] : []);
        var policies = Plan(request);
        var id = Guid.NewGuid().ToString("N");
        IntegrationCredentialSecret first;
        await using (var session = Open(connection, protection)) first = await session.Setup.CreateAsync(Principal(), id, request, policies, McpOperatorEnvironment.Production, default);
        await using var restarted = Open(connection, protection);
        var replay = await restarted.Setup.CreateAsync(Principal(), id, request, policies, McpOperatorEnvironment.Production, default);
        Assert.Equal(first, replay);
        Assert.Equal(1, await restarted.Identity.IntegrationCredentials.CountAsync());
        var rows = await restarted.App.McpOperatorPolicies.Where(p => p.IntegrationCredentialId == first.CredentialId).ToArrayAsync();
        Assert.Equal(expected, rows.Length); Assert.All(rows, row => { Assert.Null(row.ParentPolicyId); Assert.Null(row.Operation); Assert.Equal(expiry, row.ExpiresAtUtc); Assert.Equal(Resource, row.Resource); Assert.NotEmpty(row.Operations!); });
        Assert.DoesNotContain(first.Secret, (await restarted.Identity.IntegrationSetupAttempts.SingleAsync()).ProtectedDelivery!);
        await restarted.Setup.AcknowledgeAsync(Owner, id, default);
        Assert.Null((await restarted.Identity.IntegrationSetupAttempts.AsNoTracking().SingleAsync()).ProtectedDelivery);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.Setup.CreateAsync(Principal(), id, request, policies, McpOperatorEnvironment.Production, default));
    }

    [Fact]
    public async Task Policy_failure_rolls_back_credential_all_policies_and_handoff_then_the_same_request_can_retry()
    {
        var connection = await InitializeAsync(); var protection = new EphemeralDataProtectionProvider();
        var request = new IntegrationCredentialCreateRequest("Atomic setup", IntegrationCredentialPurpose.HttpMcp, ExactExpiry(),
            [new(701, NetRatelPermissions.FlowRead), new(702, NetRatelPermissions.FlowRead)], Resource, [NetRatelPermissions.McpDiscoveryRead]);
        var id = Guid.NewGuid().ToString("N"); var valid = Plan(request); var invalid = valid.Select((p, i) => i == 1 ? p with { Name = new string('x', 161) } : p).ToArray();
        await using (var failed = Open(connection, protection))
            await Assert.ThrowsAsync<ArgumentException>(() => failed.Setup.CreateAsync(Principal(), id, request, invalid, McpOperatorEnvironment.Production, default));
        await using var retry = Open(connection, protection);
        Assert.Empty(await retry.Identity.IntegrationCredentials.ToArrayAsync()); Assert.Empty(await retry.App.McpOperatorPolicies.Where(p => p.IntegrationCredentialId != null).ToArrayAsync()); Assert.Empty(await retry.Identity.IntegrationSetupAttempts.ToArrayAsync());
        await retry.Setup.CreateAsync(Principal(), id, request, valid, McpOperatorEnvironment.Production, default);
        Assert.Equal(3, await retry.App.McpOperatorPolicies.CountAsync(p => p.IntegrationCredentialId != null));
    }

    private async Task<string> InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var session = Open(connection, new EphemeralDataProtectionProvider());
        await session.App.Database.MigrateAsync(); await session.Identity.Database.MigrateAsync();
        session.App.Tenants.AddRange(new Tenant { Id = 701, Name = "Customer A" }, new Tenant { Id = 702, Name = "Customer B" });
        session.Identity.ApplicationPrincipals.Add(new() { Id = Owner });
        session.Identity.Users.Add(new() { Id = Owner, PrincipalId = Owner, UserName = "owner", IsEnabled = true, IsInstanceAdministrator = true });
        await session.App.SaveChangesAsync(); await session.Identity.SaveChangesAsync();
        return connection;
    }

    [Fact]
    public async Task Legacy_consolidation_preserves_operation_limits_denies_policy_ids_and_unrelated_principals()
    {
        var connection = await InitializeAsync(); var protection = new EphemeralDataProtectionProvider();
        await using var session = Open(connection, protection);
        var credential = await new IntegrationCredentialService(session.Identity).CreateAsync(Owner,
            new("Legacy connection", IntegrationCredentialPurpose.HttpMcp, ExactExpiry(), [new(701, NetRatelPermissions.FlowRead)], Resource));
        var admin = new McpOperatorPolicyAdministration(session.App);
        var draft = new McpOperatorPolicyDraft("Legacy list", McpOperatorEnvironment.Production, McpOperatorPolicyEffect.Allow, 0,
            new(McpOperatorPrincipalSelectorKind.OAuthClientId, credential.CredentialId), new(McpOperatorTargetSelectorKind.Tenant, 701),
            null, McpOperatorOperationFamily.AutomationRead, "netratel_flows/list", new(MaxOutputBytes: 4096), ExactExpiry(), null);
        var list = await admin.CreateAsync(draft, Owner, default);
        var denied = await admin.CreateAsync(draft with { Name = "Deny get", Effect = McpOperatorPolicyEffect.Deny, Operation = "netratel_flows/get" }, Owner, default);
        var unrelated = await admin.CreateAsync(draft with { Name = "Unrelated", PrincipalSelector = new(McpOperatorPrincipalSelectorKind.OAuthClientId, "another-client") }, Owner, default);
        var auth = new McpOperatorAuthorization(session.App);
        McpOperatorAccessRequest Request(string operation) => new(McpOperatorEnvironment.Production,
            new("owner", credential.CredentialId, credential.CredentialId, new HashSet<string>(), new HashSet<string>(["Operator"]), new HashSet<string>(["netratel.mcp.read"])),
            701, null, null, McpOperatorOperationFamily.AutomationRead, "netratel_flows/" + operation, new HashSet<string>(["netratel.mcp.read"]),
            McpOperatorConfirmationClass.None, "consolidation-test", "test-request", McpResource: Resource, Tool: "netratel_flows");
        Assert.True((await auth.EvaluateAsync(Request("list"), default)).IsAllowed);
        Assert.False((await auth.EvaluateAsync(Request("get"), default)).IsAllowed);
        // Use a fresh context for the administrative transaction, as each HTTP request does.
        await using var mutation = Open(connection, protection);
        var consolidation = new IntegrationPolicyConsolidation(mutation.App, mutation.Identity, protection);
        var plan = await consolidation.PreviewAsync(Owner, credential.CredentialId, McpOperatorEnvironment.Production, default);
        Assert.Empty(plan.Conflicts); Assert.Single(plan.Scopes);
        await consolidation.ApplyAsync(Owner, credential.CredentialId, McpOperatorEnvironment.Production, plan.PlanToken, default);
        var result = await auth.EvaluateAsync(Request("list"), default);
        Assert.True(result.IsAllowed); Assert.Equal(4096, result.EffectiveConstraints!.MaxOutputBytes);
        Assert.False((await auth.EvaluateAsync(Request("get"), default)).IsAllowed);
        var rows = await session.App.McpOperatorPolicies.AsNoTracking().Where(p => p.IntegrationCredentialId == credential.CredentialId).ToArrayAsync();
        Assert.Single(rows.Where(p => p.ParentPolicyId is null));
        Assert.Contains(rows, p => p.Id == list.PolicyId && p.ParentPolicyId is not null);
        Assert.Contains(rows, p => p.Id == denied.PolicyId && p.Effect == McpOperatorPolicyEffect.Deny && p.ParentPolicyId is not null);
        Assert.Equal(unrelated.Version, (await admin.GetAsync(unrelated.PolicyId, default))!.Version);
    }
    [Fact]
    public async Task Full_wizard_setup_admits_unprofiled_clients_in_both_tenants_and_selected_server_operations()
    {
        var connection = await InitializeAsync(); var protection = new EphemeralDataProtectionProvider();
        var permissions = NetRatelPermissions.All.Where(p => p != NetRatelPermissions.IntegrationManagement).ToArray();
        var request = new IntegrationCredentialCreateRequest("All access connection", IntegrationCredentialPurpose.HttpMcp, ExactExpiry(),
            new[] { 701, 702 }.SelectMany(t => permissions.Select(p => new IntegrationCredentialGrantRequest(t, p))).ToArray(), Resource, permissions);
        await using var session = Open(connection, protection);
        var credential = await session.Setup.CreateAsync(Principal(), Guid.NewGuid().ToString("N"), request, Plan(request), McpOperatorEnvironment.Production, default);
        var stored = await session.Identity.IntegrationCredentials.AsNoTracking().Include(row => row.Grants)
            .Include(row => row.InstanceGrants).SingleAsync(row => row.Id == credential.CredentialId);
        Assert.Equal(request.ExpiresAtUtc, stored.ExpiresAtUtc);
        Assert.Equal(2, stored.Grants.Count(grant => grant.Permission == NetRatelPermissions.FlowDelete));
        Assert.Contains(stored.InstanceGrants, grant => grant.Permission == NetRatelPermissions.FlowDelete);
        var authority = new McpOperatorAuthorization(session.App);
        var routes = new McpOperatorRouteAdmissionService(session.App, authority);
        var principal = new McpOperatorPrincipal(Owner, credential.CredentialId, null, new HashSet<string>(), new HashSet<string>(["Operator", "PolicyAdministrator"]),
            new HashSet<string>(["netratel.mcp.observe", "netratel.mcp.admin", "netratel.mcp.read"]));
        foreach (var tenant in new[] { 701, 702 })
        {
            var agent = Guid.NewGuid();
            session.App.Agents.Add(new Agent { Id = agent, TenantId = tenant, IsEnabled = true, Status = AgentStatus.Active });
            await session.App.SaveChangesAsync();
            var read = await routes.EvaluateAsync(new(McpOperatorEnvironment.Production, principal, "netratel-local-http-mcp", Resource, "prod",
                "netratel_monitoring", "client_series", tenant, agent, new HashSet<string>(["netratel.mcp.observe"]), "setup-test", "monitoring-test", false, true), default);
            Assert.True(read.Decision.IsAllowed); Assert.Null(read.Decision.Request.TargetClassification);
            Assert.Null(read.Decision.Request.TargetTags);
        }
        foreach (var operation in new[] { ("netratel_health", "get"), ("netratel_policy", "policies"), ("netratel_policy", "create") })
        {
            var descriptor = McpOperatorOperationCatalog.Find(operation.Item1, operation.Item2 == "create" ? "confirm_create" : operation.Item2)!;
            var scope = NetRatel.Shared.Operations.McpOperationAccessScopeNames.Canonical(descriptor.RequiredScope);
            var result = await authority.EvaluateAsync(new(McpOperatorEnvironment.Production, principal, 0, null, null, descriptor.OperationFamily,
                operation.Item1 + "/" + operation.Item2, new HashSet<string>([scope]), descriptor.ConfirmationClass, "setup-test", "server-test",
                McpResource: Resource, Tool: operation.Item1), default);
            Assert.True(result.IsAllowed, result.FailureCode);
        }
        Assert.Empty(await session.App.McpOperatorTargetProfiles.ToArrayAsync());
        Assert.Equal(3, await session.App.McpOperatorPolicies.CountAsync(p => p.IntegrationCredentialId == credential.CredentialId));
    }

    private static McpIntegrationPolicy[] Plan(IntegrationCredentialCreateRequest request) => request.Grants.Select(g => g.TenantId).Concat(request.InstancePermissions!.Count > 0 ? [0] : []).Distinct().Select(id => new McpIntegrationPolicy(id, $"Connection · Scope {id} · All access", McpOperatorPolicyEffect.Allow,
        McpIntegrationPolicyCatalog.Supported(id, id == 0 ? request.InstancePermissions : request.Grants.Where(g => g.TenantId == id).Select(g => g.Permission)).Aggregate(McpOperatorOperationFamily.None, (f, op) => f | op.OperationFamily), McpIntegrationPolicyCatalog.AllAccessLimits, request.ExpiresAtUtc)).ToArray();
    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity([new Claim("netratel_principal_id", Owner)], "local"));
    private static DateTimeOffset ExactExpiry() { var value = DateTimeOffset.UtcNow.AddDays(7); return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond)); }
    private static Session Open(string connection, IDataProtectionProvider protection) => new(connection, protection);
    private sealed class Session : IAsyncDisposable
    {
        public NetRatelIdentityDbContext Identity { get; }
        public OrchestratorDbContext App { get; }
        public IntegrationSetupService Setup { get; }
        public Session(string connection, IDataProtectionProvider protection)
        {
            Identity = new(new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connection).Options);
            App = new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);
            var access = new EffectiveAccessService(Identity, new ConfigurationBuilder().Build());
            Setup = new(Identity, App, new IntegrationCredentialService(Identity), new IntegrationCredentialGrantValidator(Identity, App, access, TimeProvider.System), access, new McpOperatorPolicyAdministration(App), protection);
        }
        public async ValueTask DisposeAsync() { await App.DisposeAsync(); await Identity.DisposeAsync(); }
    }
}
