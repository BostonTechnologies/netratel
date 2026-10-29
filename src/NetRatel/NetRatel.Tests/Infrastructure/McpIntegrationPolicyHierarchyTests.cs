using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpIntegrationPolicyHierarchyTests
{
    private const string Resource = "https://mcp.example.test/mcp";
    private const string Credential = "aabbccddeeff00112233445566778899";

    [Fact]
    public async Task Scoped_deny_wins_without_affecting_other_clients_operations_or_integrations_and_parent_deletion_keeps_audit()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        foreach (var id in new[] { a, b, c }) db.Agents.Add(new() { Id = id, TenantId = 42, IsEnabled = true, Status = AgentStatus.Active });
        await db.SaveChangesAsync();
        var admin = new McpOperatorPolicyAdministration(db);
        var auth = new McpOperatorAuthorization(db);
        var draft = new McpOperatorPolicyDraft("Integration · Customer · All access", McpOperatorEnvironment.Production, McpOperatorPolicyEffect.Allow, 0,
            new(McpOperatorPrincipalSelectorKind.OAuthClientId, Credential), new(McpOperatorTargetSelectorKind.Tenant, 42), null,
            McpOperatorOperationFamily.TerminalExecute | McpOperatorOperationFamily.Observability, null,
            McpIntegrationPolicyCatalog.AllAccessLimits, DateTimeOffset.UtcNow.AddDays(7), null)
        { IntegrationCredentialId = Credential, Resource = Resource, Operations = ["netratel_terminal/open", "netratel_clients/get"] };
        var root = await admin.CreateAsync(draft, "policy-admin", default);
        var deny = await admin.CreateAsync(draft with { Name = "Deny terminals on selected clients", Effect = McpOperatorPolicyEffect.Deny,
            ParentPolicyId = root.PolicyId, TargetSelector = new(McpOperatorTargetSelectorKind.Tenant, 42) { AgentIds = [a, b] },
            OperationFamily = McpOperatorOperationFamily.TerminalExecute, Operations = ["netratel_terminal/open"] }, "policy-admin", default);

        Assert.False((await auth.EvaluateAsync(Request(a), default)).IsAllowed);
        Assert.False((await auth.EvaluateAsync(Request(b), default)).IsAllowed);
        Assert.True((await auth.EvaluateAsync(Request(c), default)).IsAllowed);
        Assert.True((await auth.EvaluateAsync(Request(a) with { Tool = "netratel_clients", Operation = "netratel_clients/get", OperationFamily = McpOperatorOperationFamily.Observability, ConfirmationClass = McpOperatorConfirmationClass.None }, default)).IsAllowed);
        Assert.False((await auth.EvaluateAsync(Request(c) with { TenantId = 43 }, default)).IsAllowed);
        Assert.False((await auth.EvaluateAsync(Request(c) with { McpResource = "https://another.example.test/mcp" }, default)).IsAllowed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.CreateAsync(draft with { ParentPolicyId = root.PolicyId,
            TargetSelector = new(McpOperatorTargetSelectorKind.Tenant, 43) }, "policy-admin", default));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => admin.DeleteAsync(root.PolicyId, root.Version, "policy-admin", default));
        root = (await admin.GetAsync(root.PolicyId, default))!;
        await admin.DeleteAsync(root.PolicyId, root.Version, "policy-admin", default);
        Assert.False((await auth.EvaluateAsync(Request(c), default)).IsAllowed);
        Assert.Equal(McpOperatorPolicyLifecycleState.Deleted, (await admin.GetAsync(deny.PolicyId, default))!.LifecycleState);
        Assert.Contains(await admin.ListChangeAuditsAsync(42, root.PolicyId, null, default), audit => audit.Action == "policy_deleted");
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.ReplaceAsync(root.PolicyId, root.Version + 1, draft, "policy-admin", default));
    }

    [Fact]
    public async Task Parent_expiry_disable_and_changed_limits_invalidate_child_admission_and_versions()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var admin = new McpOperatorPolicyAdministration(db);
        var draft = new McpOperatorPolicyDraft("Base", McpOperatorEnvironment.Production, McpOperatorPolicyEffect.Allow, 0,
            new(McpOperatorPrincipalSelectorKind.OAuthClientId, Credential), new(McpOperatorTargetSelectorKind.Tenant, 42), null,
            McpOperatorOperationFamily.TerminalExecute, null, McpIntegrationPolicyCatalog.AllAccessLimits, DateTimeOffset.UtcNow.AddHours(1), null)
        { IntegrationCredentialId = Credential, Resource = Resource, Operations = ["netratel_terminal/open"] };
        var root = await admin.CreateAsync(draft, "admin", default);
        var child = await admin.CreateAsync(draft with { ParentPolicyId = root.PolicyId, Name = "Exception", Priority = 100 }, "admin", default);
        root = (await admin.GetAsync(root.PolicyId, default))!;
        await admin.ReplaceAsync(root.PolicyId, root.Version, draft with { Constraints = draft.Constraints with { MaxOutputBytes = 4096 } }, "admin", default);
        Assert.Equal(child.Version + 1, (await admin.GetAsync(child.PolicyId, default))!.Version);
        var auth = new McpOperatorAuthorization(db);
        Assert.Equal(4096, (await auth.EvaluateAsync(Request(Guid.NewGuid()), default)).EffectiveConstraints!.MaxOutputBytes);
        var parent = await db.McpOperatorPolicies.SingleAsync(p => p.Id == root.PolicyId);
        parent.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        Assert.False((await auth.EvaluateAsync(Request(Guid.NewGuid()), default)).IsAllowed);
        parent.ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1); await db.SaveChangesAsync();
        await admin.DisableAsync(root.PolicyId, parent.Version, "admin", default);
        Assert.Equal(McpOperatorPolicyLifecycleState.Disabled, (await admin.GetAsync(child.PolicyId, default))!.LifecycleState);
    }

    private static McpOperatorAccessRequest Request(Guid client) => new(McpOperatorEnvironment.Production,
        new("operator", Credential, Credential, new HashSet<string>(), new HashSet<string>(["AutomationOperator", "Operator", "Administrator"]), new HashSet<string>(["netratel.mcp.execute"])),
        42, client, McpOperatorTargetClassification.ManagedStandard, McpOperatorOperationFamily.TerminalExecute, "netratel_terminal/open",
        new HashSet<string>(["netratel.mcp.execute"]), McpOperatorConfirmationClass.RemoteExecution, "test-correlation", "test-request", McpResource: Resource, Tool: "netratel_terminal");
}
