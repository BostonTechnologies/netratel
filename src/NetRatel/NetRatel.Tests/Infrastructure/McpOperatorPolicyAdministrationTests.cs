using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorPolicyAdministrationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task CreateAsync_RejectsOAuthSubjectSelfEscalation()
    {
        await using var db = CreateDb();
        var administration = new McpOperatorPolicyAdministration(db);
        var actor = "operator@example.test";
        var draft = Draft(new McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind.OAuthSubject, actor));

        var action = () => administration.CreateAsync(draft, actor, Ct);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot grant themselves*");
    }

    [Fact]
    public async Task ValidateDraftAsync_UsesTheCreationRulesWithoutPersistingPolicyOrAuditState()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        db.Agents.Add(new Agent
        {
            Id = agentId,
            TenantId = 42,
            IsEnabled = true,
            Status = AgentStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Ct);
        var administration = new McpOperatorPolicyAdministration(db);
        var draft = Draft(new McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind.OidcGroup, "incident-responders")) with
        {
            TargetSelector = new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.ExactAgent, 42, agentId)
        };

        await administration.ValidateDraftAsync(draft, "policy-admin@example.test", Ct);

        (await db.McpOperatorPolicies.CountAsync(Ct)).Should().Be(0);
        (await db.McpOperatorPolicyChangeAudits.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task PolicyLifecycle_UsesExplicitVersionsAndRetainsDisabledAndRevokedPolicyForAudit()
    {
        await using var db = CreateDb();
        var administration = new McpOperatorPolicyAdministration(db);
        var actor = "policy-admin@example.test";
        var draft = Draft(new McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind.OidcGroup, "netratel-operators"));

        var created = await administration.CreateAsync(draft, actor, Ct);
        var replaced = await administration.ReplaceAsync(created.PolicyId, created.Version, draft with { Name = "approved terminal execution" }, actor, Ct);
        var disabled = await administration.DisableAsync(replaced.PolicyId, replaced.Version, actor, Ct);
        var revoked = await ((IMcpOperatorPolicyRevocation)administration).RevokeAsync(disabled!.PolicyId, disabled.Version, actor, Ct);
        var listed = await administration.ListAsync(McpOperatorEnvironment.Development, 42, Ct);
        var audit = await administration.ListChangeAuditsAsync(42, created.PolicyId, null, Ct);

        created.Version.Should().Be(1);
        replaced.Version.Should().Be(2);
        disabled.Should().NotBeNull();
        disabled!.Version.Should().Be(3);
        disabled.DisabledAtUtc.Should().NotBeNull();
        disabled.DisabledBy.Should().Be(actor);
        disabled.LifecycleState.Should().Be(McpOperatorPolicyLifecycleState.Disabled);
        revoked.Should().NotBeNull();
        revoked!.Version.Should().Be(4);
        revoked.LifecycleState.Should().Be(McpOperatorPolicyLifecycleState.Revoked);
        listed.Items.Should().ContainSingle().Which.PolicyId.Should().Be(created.PolicyId);
        (await administration.GetAsync(created.PolicyId, Ct))!.Name.Should().Be("approved terminal execution");
        audit.Select(entry => entry.Action).Should().Equal("policy_revoked", "policy_disabled", "policy_replaced", "policy_created");
        audit.Select(entry => entry.Version).Should().Equal(4, 3, 2, 1);
        audit.Should().OnlyContain(entry => entry.ActorId == actor && entry.PolicyId == created.PolicyId);
        var action = () => administration.ReplaceAsync(created.PolicyId, created.Version, draft, actor, Ct);
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revoked operator policy cannot be replaced*");
    }

    [Fact]
    public async Task UpsertTargetProfile_RequiresThePersistedTargetAndAnExpectedCurrentVersion()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        db.Agents.Add(new Agent
        {
            Id = agentId,
            TenantId = 42,
            IsEnabled = true,
            Status = AgentStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Ct);
        var administration = new McpOperatorPolicyAdministration(db);

        var created = await administration.UpsertTargetProfileAsync(
            42,
            agentId,
            McpOperatorTargetClassification.DedicatedQa,
            ["qa", "development"],
            null,
            "policy-admin@example.test",
            Ct);
        var replaced = await administration.UpsertTargetProfileAsync(
            42,
            agentId,
            McpOperatorTargetClassification.DevelopmentSafe,
            ["development", "qa", "qa"],
            created.Version,
            "policy-admin@example.test",
            Ct);
        var audit = await administration.ListChangeAuditsAsync(42, null, agentId, Ct);

        created.Version.Should().Be(1);
        replaced.Version.Should().Be(2);
        replaced.Tags.Should().Equal("development", "qa");
        (await administration.GetTargetProfileAsync(42, agentId, Ct))!.Classification
            .Should().Be(McpOperatorTargetClassification.DevelopmentSafe);
        audit.Select(entry => entry.Action).Should().Equal("target_profile_upserted", "target_profile_upserted");
        audit.Select(entry => entry.Version).Should().Equal(2, 1);
        var staleUpdate = () => administration.UpsertTargetProfileAsync(
            42,
            agentId,
            McpOperatorTargetClassification.Restricted,
            [],
            created.Version,
            "policy-admin@example.test",
            Ct);
        await staleUpdate.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task AcceptedAuditSearch_IsBoundedAndReturnsOnlyContentFreeAdmissionEvidence()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        db.McpOperatorAcceptedAudits.Add(new McpOperatorAcceptedAuditRecord
        {
            Id = Guid.NewGuid(),
            PolicyId = Guid.NewGuid(),
            Environment = McpOperatorEnvironment.Development,
            ServicePrincipal = "netratel-mcp-dev",
            Subject = "operator@example.test",
            ClientId = "automation-client",
            GroupsJson = "[\"netratel-operators\"]",
            RolesJson = "[\"Operator\"]",
            ScopesJson = "[\"netratel.mcp.execute\"]",
            McpResource = "https://mcp.dev.example/mcp",
            McpInstance = "dev",
            Tool = "netratel_terminal",
            TenantId = 42,
            AgentId = agentId,
            OperationFamily = McpOperatorOperationFamily.TerminalExecute,
            Operation = "terminal/open",
            CorrelationId = "corr-42",
            RequestId = "request-42",
            OccurredAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Ct);
        var administration = new McpOperatorPolicyAdministration(db);

        var page = await administration.ListAcceptedAuditsAsync(
            new McpOperatorAcceptedAuditFilter(42, agentId, "operator@example.test", 1), Ct);

        page.Items.Should().ContainSingle();
        page.Items[0].Should().BeEquivalentTo(new McpOperatorAcceptedAudit(
            page.Items[0].AuditId,
            page.Items[0].PolicyId,
            McpOperatorEnvironment.Development,
            "netratel-mcp-dev",
            "operator@example.test",
            "automation-client",
            null,
            ["netratel-operators"],
            ["Operator"],
            ["netratel.mcp.execute"],
            "https://mcp.dev.example/mcp",
            "dev",
            "netratel_terminal",
            42,
            agentId,
            McpOperatorOperationFamily.TerminalExecute,
            "terminal/open",
            "corr-42",
            "request-42",
            page.Items[0].OccurredAtUtc));
        var invalid = () => administration.ListAcceptedAuditsAsync(new McpOperatorAcceptedAuditFilter(Limit: 251), Ct);
        await invalid.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CreateAsync_AllowsAnExplicitControlPlanePolicyButNotAnImplicitAgentClassification()
    {
        await using var db = CreateDb();
        var administration = new McpOperatorPolicyAdministration(db);
        var draft = Draft(new McpOperatorPrincipalSelector(McpOperatorPrincipalSelectorKind.OidcGroup, "tenant-administrators")) with
        {
            Name = "tenant control-plane administration",
            TargetSelector = new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.ControlPlane, 0),
            TargetClassification = null,
            OperationFamily = McpOperatorOperationFamily.TenantAdministration,
            Operation = "netratel_tenants/create",
            Constraints = new McpOperatorConstraints(RequiredConfirmationClass: McpOperatorConfirmationClass.FleetWide)
        };

        var created = await administration.CreateAsync(draft, "policy-admin@example.test", Ct);
        var listed = await administration.ListAsync(McpOperatorEnvironment.Development, 0, Ct);

        created.TargetSelector.Kind.Should().Be(McpOperatorTargetSelectorKind.ControlPlane);
        created.TargetSelector.TenantId.Should().Be(0);
        listed.Items.Should().ContainSingle().Which.PolicyId.Should().Be(created.PolicyId);
        var invalid = () => administration.ValidateDraftAsync(draft with { TargetClassification = McpOperatorTargetClassification.DevelopmentSafe }, "policy-admin@example.test", Ct);
        await invalid.Should().ThrowAsync<ArgumentException>().WithMessage("*control-plane policy cannot select*");
    }

    private static McpOperatorPolicyDraft Draft(McpOperatorPrincipalSelector principal) => new(
        "approved terminal observation",
        McpOperatorEnvironment.Development,
        McpOperatorPolicyEffect.Allow,
        20,
        principal,
        new McpOperatorTargetSelector(McpOperatorTargetSelectorKind.Tenant, 42),
        McpOperatorTargetClassification.DevelopmentSafe,
        McpOperatorOperationFamily.TerminalExecute,
        "terminal/open",
        new McpOperatorConstraints(MaxCommandDurationSeconds: 60),
        DateTimeOffset.UtcNow.AddDays(7),
        DateTimeOffset.UtcNow.AddDays(1),
        "issue-943");

    private static OrchestratorDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new OrchestratorDbContext(options);
    }
}
