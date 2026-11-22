using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Connectivity;
using NetRatel.Tests.API;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed partial class IntegrationSetupTransactionTests(PostgreSqlPersistenceFixture postgres)
{
    private const string Owner = "aabbccddeeff00112233445566778899";
    private const string Resource = "https://mcp.example.test/mcp";

    [Fact]
    public async Task Account_endpoint_creates_four_full_tenants_and_server_scope_with_one_protected_handoff()
    {
        var connection = await InitializeAsync();
        var protection = new EphemeralDataProtectionProvider();
        await using (var seed = Open(connection, protection))
        {
            seed.App.Tenants.AddRange(new Tenant { Id = 703, Name = "Customer C" }, new Tenant { Id = 704, Name = "Customer D" });
            await seed.App.SaveChangesAsync();
        }
        var permissions = IntegrationPermissionCatalog.Delegable.Select(permission => permission.Id).ToArray();
        var request = new IntegrationCredentialCreateRequest("Customer connection", IntegrationCredentialPurpose.HttpMcp, ExactExpiry(),
            Enumerable.Range(701, 4).SelectMany(id => permissions.Select(permission => new IntegrationCredentialGrantRequest(id, permission))).ToArray(), Resource, permissions);
        var requestId = Guid.NewGuid().ToString("N");
        var body = new IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest(request.Name, request.Purpose, request.ExpiresAtUtc,
            request.Grants, Resource, request.InstancePermissions, requestId, Plan(request), true);
        await using var host = await StartAccountApiAsync(connection, protection);
        using var client = IntegrationCredentialAccessEndpointTests.AccountClient(host, owner: Owner);
        const string path = "/api/v2/account/integration-credentials/";
        using var invalid = await client.PostAsJsonAsync(path, body with { SetupRequestId = "invalid-setup-id" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("integration_setup_invalid", (await invalid.Content.ReadFromJsonAsync<IntegrationPairingFailure>())!.Code);

        using var created = await client.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var credential = Assert.IsType<IntegrationCredentialSecret>(await created.Content.ReadFromJsonAsync<IntegrationCredentialSecret>());
        using var replay = await client.PostAsJsonAsync(path, body);
        Assert.Equal(credential, await replay.Content.ReadFromJsonAsync<IntegrationCredentialSecret>());
        using var acknowledged = await client.PostAsync(path + $"setup/{requestId}/acknowledge", null);
        Assert.Equal(HttpStatusCode.NoContent, acknowledged.StatusCode);

        await using var readback = Open(connection, protection);
        var persisted = await readback.Identity.IntegrationCredentials.Include(row => row.Grants).Include(row => row.InstanceGrants).SingleAsync();
        Assert.Equal(credential.CredentialId, persisted.Id);
        Assert.Equal(request.ExpiresAtUtc, persisted.ExpiresAtUtc);
        Assert.Equal(request.Grants.OrderBy(g => g.TenantId).ThenBy(g => g.Permission),
            persisted.Grants.OrderBy(g => g.TenantId).ThenBy(g => g.Permission).Select(g => new IntegrationCredentialGrantRequest(g.TenantId, g.Permission)));
        Assert.Equal(permissions.Order(), persisted.InstanceGrants.Select(g => g.Permission).Order());
        Assert.Equal(new[] { 0, 701, 702, 703, 704 }, await readback.App.McpOperatorPolicies
            .Where(policy => policy.IntegrationCredentialId == credential.CredentialId && policy.ParentPolicyId == null)
            .OrderBy(policy => policy.TenantId).Select(policy => policy.TenantId).ToArrayAsync());
        Assert.Null((await readback.Identity.IntegrationSetupAttempts.SingleAsync()).ProtectedDelivery);
    }

    [Fact]
    public async Task Reviewed_account_edits_expand_remove_and_readd_tenants_and_keeps_exact_custom_policy_after_restart()
    {
        var connection = await InitializeAsync();
        var protection = new EphemeralDataProtectionProvider();
        var permissions = IntegrationPermissionCatalog.Delegable.Select(permission => permission.Id).ToArray();
        var expiry = ExactExpiry();
        IntegrationCredentialSecret issued;
        await using (var seed = Open(connection, protection))
        {
            seed.App.Tenants.AddRange(Enumerable.Range(703, 3).Select(id => new Tenant { Id = id, Name = $"Customer {id}" }));
            await seed.App.SaveChangesAsync();
            var request = CreateRequest(1);
            var setupId = Guid.NewGuid().ToString("N");
            issued = await seed.Setup.CreateAsync(Principal(), setupId, request, Plan(request), McpOperatorEnvironment.Production, default);
            await seed.Setup.AcknowledgeAsync(Owner, setupId, default);
        }

        var storeFailure = new FailPolicyWriteOnce(704);
        await using var host = await StartAccountApiAsync(connection, protection, storeFailure);
        using var client = IntegrationCredentialAccessEndpointTests.AccountClient(host, owner: Owner);
        var path = $"/api/v2/account/integration-credentials/{issued.CredentialId}";
        var expanded = await PrepareAsync(CreateRequest(4));
        storeFailure.Armed = true;
        using (var failed = await ApplyAsync(expanded))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Equal("review_service_unavailable", (await failed.Content.ReadFromJsonAsync<IntegrationPairingFailure>())!.Code);
        }
        await using (var readback = Open(connection, protection))
        {
            Assert.Equal(permissions.Length, await readback.Identity.IntegrationCredentialGrants.CountAsync());
            Assert.Equal(2, await readback.App.McpOperatorPolicies.CountAsync(policy => policy.IntegrationCredentialId == issued.CredentialId));
            Assert.Empty(await readback.Identity.IntegrationCredentialLifecycleEvents.ToArrayAsync());
        }
        await SaveAsync(expanded, 4);
        // Treat the previous apply response as lost and retry its exact decision.
        await SaveAsync(expanded, 4);
        await SaveAsync(await PrepareAsync(CreateRequest(5)), 5);

        var policyOnly = await PrepareAsync(CreateRequest(5), policy => policy.TenantId != 701 ? policy : policy with
        {
            Name = "Customer A flow list",
            Effect = McpOperatorPolicyEffect.Deny,
            Families = McpOperatorOperationFamily.AutomationRead,
            Operations = ["netratel_flows/list"],
            Constraints = new(MaxOutputBytes: 4096),
            ExpiresAtUtc = expiry.AddDays(-1)
        });
        var stale = await PrepareAsync(CreateRequest(5));
        await SaveAsync(policyOnly, 5);
        using (var conflict = await ApplyAsync(stale))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("review_policy_changed", (await conflict.Content.ReadFromJsonAsync<IntegrationPairingFailure>())!.Code);
        }

        var reduced = CreateRequest(5) with { Grants = CreateRequest(5).Grants.Where(grant => grant.TenantId != 702).ToArray() };
        await SaveAsync(await PrepareAsync(reduced), 4);
        await SaveAsync(await PrepareAsync(CreateRequest(5)), 5);

        await using var restarted = Open(connection, protection);
        await new IntegrationAllAccessReconciliation(restarted.Identity, restarted.App).ReconcileAsync();
        var stored = await restarted.Identity.IntegrationCredentials.AsNoTracking().SingleAsync();
        Assert.Equal(issued.CredentialId, stored.Id);
        Assert.Equal(IntegrationCredentialService.Hash(issued.Secret), stored.SecretHash);
        Assert.Equal(Resource, stored.Resource);
        Assert.Equal(IntegrationCredentialPurpose.HttpMcp, stored.Purpose);
        Assert.Equal(expiry, stored.ExpiresAtUtc);
        var verified = await new IntegrationCredentialService(restarted.Identity).VerifyWithStatusAsync(issued.Secret, stored.Purpose, Resource);
        Assert.Equal(5 * permissions.Length, verified.Credential!.Grants.Count);
        var policies = await restarted.App.McpOperatorPolicies.AsNoTracking()
            .Where(policy => policy.IntegrationCredentialId == issued.CredentialId && policy.ParentPolicyId == null).ToArrayAsync();
        Assert.Equal(6, policies.Count(policy => policy.LifecycleState == McpOperatorPolicyLifecycleState.Active));
        Assert.Single(policies, policy => policy.TenantId == 702 && policy.LifecycleState == McpOperatorPolicyLifecycleState.Disabled);
        Assert.Single(policies, policy => policy.TenantId == 702 && policy.LifecycleState == McpOperatorPolicyLifecycleState.Active);
        var retained = Assert.Single(policies, policy => policy.TenantId == 701);
        Assert.Equal("Customer A flow list", retained.Name);
        Assert.Equal(McpOperatorPolicyEffect.Deny, retained.Effect);
        Assert.Equal(expiry.AddDays(-1), retained.ExpiresAtUtc);
        Assert.Equal(new[] { "netratel_flows/list" }, retained.Operations);
        Assert.Equal(4096, System.Text.Json.JsonSerializer.Deserialize(retained.ConstraintsJson, McpOperatorJsonContext.Default.McpOperatorConstraints)!.MaxOutputBytes);

        IntegrationCredentialCreateRequest CreateRequest(int tenantCount) => new("Customer connection", IntegrationCredentialPurpose.HttpMcp,
            expiry, Enumerable.Range(701, tenantCount).SelectMany(id => permissions.Select(permission => new IntegrationCredentialGrantRequest(id, permission))).ToArray(), Resource, permissions);

        async Task<ReviewedMcpAccessReceipt> PrepareAsync(IntegrationCredentialCreateRequest request, Func<McpIntegrationPolicy, McpIntegrationPolicy>? edit = null)
        {
            var current = (await client.GetFromJsonAsync<McpIntegrationPolicy[]>(path + "/base-policies"))!.ToDictionary(policy => policy.TenantId);
            var policiesToSave = Plan(request).Select(policy => current.GetValueOrDefault(policy.TenantId, policy)).ToArray();
            if (edit is not null) policiesToSave = policiesToSave.Select(edit).ToArray();
            using var response = await client.PostAsJsonAsync(path + "/access-requests",
                new IntegrationCredentialEndpoints.CredentialAccessRequest(Guid.NewGuid().ToString("N"),
                    request.Grants.Select(grant => new IntegrationPairingGrant(grant.TenantId, grant.Permission)).ToArray(), permissions, policiesToSave));
            Assert.True(response.IsSuccessStatusCode, $"Prepare returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return (await response.Content.ReadFromJsonAsync<ReviewedMcpAccessReceipt>())!;
        }

        Task<HttpResponseMessage> ApplyAsync(ReviewedMcpAccessReceipt pending) => client.PostAsJsonAsync(
            path + $"/access-requests/{pending.Request.RequestId}/apply",
            new IntegrationCredentialEndpoints.CredentialAccessDecision(pending.Request.ProposalHash, true));

        async Task SaveAsync(ReviewedMcpAccessReceipt pending, int tenantCount)
        {
            using var response = await ApplyAsync(pending);
            Assert.True(response.IsSuccessStatusCode, $"Apply returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            var receipt = (await response.Content.ReadFromJsonAsync<ReviewedMcpAccessReceipt>())!;
            Assert.Equal("consumed", receipt.Request.Status);
            Assert.Equal(tenantCount * permissions.Length, receipt.ConfiguredGrants.Count);
            Assert.Equal(expiry, receipt.CredentialExpiresAtUtc);
        }
    }

    private sealed class FailPolicyWriteOnce(int tenantId) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<McpOperatorPolicyRecord>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.TenantId == tenantId))
            {
                Armed = false;
                throw new TimeoutException("Synthetic policy store interruption.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private static async Task<WebApplication> StartAccountApiAsync(string connection, IDataProtectionProvider protection,
        SaveChangesInterceptor? interceptor = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration["Integration:HttpMcp:PublicResourceUri"] = Resource;
        builder.Services.Configure<DeploymentBrandingOptions>(options => options.SiteUrl = "https://mcp.example.test");
        IntegrationCredentialAccessEndpointTests.AddAccountAuthentication(builder.Services);
        builder.Services.AddDbContext<NetRatelIdentityDbContext>(options => options.UseNpgsql(connection));
        builder.Services.AddDbContext<OrchestratorDbContext>(options =>
        {
            options.UseNpgsql(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
        });
        builder.Services.AddSingleton(protection);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
        builder.Services.AddScoped<IIntegrationCredentialGrantValidator, IntegrationCredentialGrantValidator>();
        builder.Services.AddScoped<IIntegrationCredentialService, IntegrationCredentialService>();
        builder.Services.AddScoped<IMcpOperatorPolicyAdministration, McpOperatorPolicyAdministration>();
        builder.Services.AddScoped<IMcpOperatorAuthorization, McpOperatorAuthorization>();
        builder.Services.AddScoped<IDeploymentBrandingService, DeploymentBrandingService>();
        builder.Services.AddScoped<IntegrationSetupService>();
        builder.Services.AddScoped<IntegrationPairingService>();
        builder.Services.AddRateLimiter(options => options.AddPolicy("integration-pairing-account", _ => RateLimitPartition.GetNoLimiter("test")));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseMiddleware<PairingRequestBodyLimitMiddleware>();
        app.MapIntegrationCredentialEndpoints();
        await app.StartAsync();
        return app;
    }

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
        {
            const string ordinaryOwner = "synthetic-ordinary-account";
            failed.Identity.ApplicationPrincipals.Add(new() { Id = ordinaryOwner });
            await failed.Identity.SaveChangesAsync();
            var ordinary = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", ordinaryOwner)], "local"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                failed.Setup.CreateAsync(ordinary, id, request, valid, McpOperatorEnvironment.Production, default));
            Assert.Equal("integration_policy_invalid", (await Assert.ThrowsAsync<IntegrationCredentialGrantValidationException>(
                () => failed.Setup.CreateAsync(Principal(), id, request, invalid, McpOperatorEnvironment.Production, default))).ReviewFailure!.Code);
        }
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
