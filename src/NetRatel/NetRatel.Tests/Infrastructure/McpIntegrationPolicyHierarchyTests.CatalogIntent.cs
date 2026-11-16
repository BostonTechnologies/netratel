using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed partial class McpIntegrationPolicyHierarchyTests
{
    [Fact]
    public async Task Public_policy_administration_cannot_forge_credential_review_or_legacy_setup_provenance()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var draft = new McpOperatorPolicyDraft("Custom full operations", McpOperatorEnvironment.Production, McpOperatorPolicyEffect.Allow, 0,
            new(McpOperatorPrincipalSelectorKind.OAuthClientId, Credential), new(McpOperatorTargetSelectorKind.Tenant, 42), null,
            McpOperatorOperationFamily.TerminalExecute, null, McpIntegrationPolicyCatalog.AllAccessLimits with
            { FullPermissionCatalog = true, PermissionCatalogSnapshot = new string('A', 64) }, DateTimeOffset.UtcNow.AddDays(1), null)
        { IntegrationCredentialId = Credential, Resource = Resource };
        var wire = JsonSerializer.SerializeToNode(draft)!.AsObject();
        wire[nameof(McpOperatorPolicyDraft.IntegrationReviewApproved)] = true;
        var external = wire.Deserialize<McpOperatorPolicyDraft>()!;
        Assert.False(external.IntegrationReviewApproved);
        var administration = new McpOperatorPolicyAdministration(db);
        var created = await administration.CreateAsync(external, "operator", default);
        Assert.False(created.Constraints.FullPermissionCatalog);
        Assert.Null(created.Constraints.PermissionCatalogSnapshot);
        await Assert.ThrowsAsync<ArgumentException>(() => administration.CreateAsync(external with
            { AuditReference = "integration-setup:" + Guid.NewGuid().ToString("N") }, "operator", default));
    }
}
