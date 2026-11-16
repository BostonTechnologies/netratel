using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed partial class IntegrationSetupTransactionTests
{
    [Fact]
    public async Task Original_three_scope_preset_is_repaired_without_replacing_credentials_or_custom_policies()
    {
        var connection = await InitializeAsync();
        var protection = new EphemeralDataProtectionProvider();
        var baseline = OriginalCatalog();
        var request = new IntegrationCredentialCreateRequest("Original full access", IntegrationCredentialPurpose.HttpMcp, ExactExpiry(),
            new[] { 701, 702 }.SelectMany(t => baseline.Permissions.Select(p => new IntegrationCredentialGrantRequest(t, p))).ToArray(), Resource, baseline.Permissions);
        await using var session = Open(connection, protection);
        var originalPolicies = Plan(request).Select(p => p with
        {
            Constraints = McpIntegrationPolicyCatalog.BoundedDefaults,
            Operations = baseline.Scopes[p.TenantId == 0 ? 0 : 1].Operations,
            Families = (McpOperatorOperationFamily)baseline.Scopes[p.TenantId == 0 ? 0 : 1].Families
        }).ToArray();
        var setupId = Guid.NewGuid().ToString("N");
        var issued = await session.Setup.CreateAsync(Principal(), setupId, request, originalPolicies, McpOperatorEnvironment.Production, default);
        await session.Setup.AcknowledgeAsync(Owner, setupId, default);
        var customId = Guid.NewGuid().ToString("N");
        var custom = await session.Setup.CreateAsync(Principal(), customId, request with { Name = "Custom connection" },
            originalPolicies.Select(p => p with { Constraints = McpIntegrationPolicyCatalog.BoundedDefaults with { MaxCommandDurationSeconds = 42 } }).ToArray(),
            McpOperatorEnvironment.Production, default);
        await session.Setup.AcknowledgeAsync(Owner, customId, default);
        var credential = await session.Identity.IntegrationCredentials.AsNoTracking().SingleAsync(c => c.Id == issued.CredentialId);
        var hash = credential.SecretHash;
        var beforeIds = await session.App.McpOperatorPolicies.Where(p => p.IntegrationCredentialId == issued.CredentialId).Select(p => p.Id).ToArrayAsync();
        session.Identity.ChangeTracker.Clear(); session.App.ChangeTracker.Clear();
        var repair = new IntegrationAllAccessReconciliation(session.Identity, session.App);
        await repair.ReconcileAsync();
        var after = await session.App.McpOperatorPolicies.AsNoTracking().Where(p => p.IntegrationCredentialId == issued.CredentialId).ToArrayAsync();
        Assert.Equal(beforeIds.Order(), after.Select(p => p.Id).Order());
        Assert.All(after, p => { Assert.Equal(2, p.Version); Assert.True(JsonSerializer.Deserialize(p.ConstraintsJson, McpOperatorJsonContext.Default.McpOperatorConstraints)!.Unrestricted); });
        var current = await session.Identity.IntegrationCredentials.AsNoTracking().Include(c => c.Grants).Include(c => c.InstanceGrants).SingleAsync(c => c.Id == issued.CredentialId);
        Assert.Equal(hash, current.SecretHash); Assert.Equal(credential.ExpiresAtUtc, current.ExpiresAtUtc); Assert.Equal(credential.Resource, current.Resource);
        Assert.Equal(IntegrationPermissionCatalog.Delegable.Count, current.InstanceGrants.Count);
        Assert.All(new[] { 701, 702 }, tenant => Assert.Equal(IntegrationPermissionCatalog.Delegable.Count, current.Grants.Count(g => g.TenantId == tenant)));
        Assert.All(await session.App.McpOperatorPolicies.AsNoTracking().Where(p => p.IntegrationCredentialId == custom.CredentialId).ToArrayAsync(), p =>
        { Assert.Equal(1, p.Version); Assert.Equal(42, JsonSerializer.Deserialize(p.ConstraintsJson, McpOperatorJsonContext.Default.McpOperatorConstraints)!.MaxCommandDurationSeconds); });
        await AssertCompleteAdmissionAsync(session, issued.CredentialId);
        var audits = await session.App.McpOperatorPolicyChangeAudits.CountAsync();
        await repair.ReconcileAsync();
        Assert.Equal(audits, await session.App.McpOperatorPolicyChangeAudits.CountAsync());
        // A later deliberate grant reduction must not be silently undone on restart.
        session.Identity.IntegrationCredentialGrants.Remove(await session.Identity.IntegrationCredentialGrants.SingleAsync(g =>
            g.CredentialId == issued.CredentialId && g.TenantId == 701 && g.Permission == NetRatelPermissions.FlowRead));
        await session.Identity.SaveChangesAsync();
        await repair.ReconcileAsync();
        Assert.False(await session.Identity.IntegrationCredentialGrants.AnyAsync(g => g.CredentialId == issued.CredentialId && g.TenantId == 701 && g.Permission == NetRatelPermissions.FlowRead));
    }

    private static async Task AssertCompleteAdmissionAsync(Session session, string credentialId)
    {
        var all = IntegrationPermissionCatalog.Delegable.Select(p => p.Id).ToArray();
        var scopes = new[] { 0, 701 }.ToDictionary(t => t, t => McpIntegrationPolicyCatalog.Supported(t, all));
        var policies = await session.App.McpOperatorPolicies.AsNoTracking().Where(p => p.IntegrationCredentialId == credentialId).ToArrayAsync();
        var authority = new McpOperatorAuthorization(session.App);
        foreach (var operation in McpOperationAccessCatalog.Operations)
        {
            var key = operation.ToolName + "/" + operation.OperationName;
            var target = McpOperationTargetCatalog.Find(operation.ToolName, operation.OperationName);
            Assert.NotNull(target);
            var scope = scopes.FirstOrDefault(s => s.Value.Any(op => op.ToolName == operation.ToolName && op.OperationName == operation.OperationName));
            Assert.NotNull(scope.Value);
            var descriptor = McpOperatorOperationCatalog.Find(operation.ToolName, operation.OperationName)!;
            var policy = Assert.Single(policies.Where(p => p.TenantId == scope.Key));
            Assert.Contains(key, policy.Operations!); Assert.True(policy.OperationFamily.HasFlag(descriptor.OperationFamily));
            Assert.Contains(McpLocalOperationAuthorizationCatalog.RequiredPermission(operation.ToolName, operation.OperationName)!, all);
            Assert.All(McpLocalOperationAuthorizationCatalog.AdditionalPermissions(operation.ToolName, operation.OperationName), p => Assert.Contains(p, all));
            var required = new HashSet<string>([McpOperationAccessScopeNames.Canonical(operation.RequiredScope)]);
            var principal = new McpOperatorPrincipal(Owner, credentialId, null, new HashSet<string>(),
                new HashSet<string>(["Operator", "AutomationOperator", "PolicyAdministrator"]), required);
            var result = await authority.EvaluateAsync(new(McpOperatorEnvironment.Production, principal, scope.Key,
                target!.Model == McpOperationTargetModel.Agent ? Guid.NewGuid() : null, null,
                descriptor.OperationFamily, key, required, descriptor.ConfirmationClass, "all-access-test", "catalog",
                McpResource: Resource, Tool: operation.ToolName), default);
            Assert.True(result.IsAllowed, key + ": " + result.FailureCode);
            Assert.True(result.EffectiveConstraints!.AllowActiveTenantDeletion);
        }
    }

    private static LegacyCatalog OriginalCatalog()
    {
        using var stream = typeof(IntegrationSetupTransactionTests).Assembly.GetManifestResourceStream(
            "NetRatel.Tests.Infrastructure.PolicyBaselines.OriginalAllAccess.json")!;
        return JsonSerializer.Deserialize<LegacyCatalog>(stream)!;
    }
    private sealed record LegacyCatalog(string[] Permissions, LegacyScope[] Scopes);
    private sealed record LegacyScope(int Tenant, int Families, string[] Operations);
}
