using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorAccessEvaluationServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task EffectiveAndExactEvaluation_ReturnServerResolvedPolicyFactsWithoutDispatching()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        await SeedAsync(db, agentId);
        var service = new McpOperatorAccessEvaluationService(db, new McpOperatorAuthorization(db));

        var effective = await service.EffectiveAsync(
            McpOperatorEnvironment.Development,
            Principal(),
            42,
            agentId,
            Ct);
        var evaluation = await service.EvaluateAsync(
            McpOperatorEnvironment.Development,
            Principal(),
            42,
            agentId,
            "netratel_terminal",
            "availability",
            "correlation-42",
            "request-42",
            Ct);

        effective.Should().NotBeNull();
        effective!.Caller.Subject.Should().Be("ope…st");
        effective.Caller.ClientId.Should().Be("aut…nt");
        effective.Caller.VisibleTenantIds.Should().Equal(42);
        effective.Target.Classification.Should().Be(McpOperatorTargetClassification.DevelopmentSafe);
        effective.Target.Tags.Should().Equal("development", "qa");
        effective.MatchingPolicies.Should().ContainSingle().Which.Name.Should().Be("terminal observation");
        effective.AllowedOperationFamilies.Should().Contain(McpOperatorOperationFamily.TerminalRead);

        evaluation.Should().NotBeNull();
        evaluation!.Allowed.Should().BeTrue();
        evaluation.RequiredScope.Should().Be("netratel.mcp.observe");
        evaluation.CapabilityReadiness.Should().Be("not_checked");
        evaluation.Interpretation.Should().NotBeNull();
        evaluation.Interpretation!.InspectionTool.Should().Be("netratel_access");
        evaluation.Interpretation.Assessment.Should().Be("current_assertion_only");
        evaluation.Interpretation.Policy.Should().Be("evaluated");
        evaluation.Interpretation.Runtime.Should().Be("not_checked");
        evaluation.Interpretation.AuthoritativeAdmissionReceipt.Should().BeFalse();
        evaluation.MissingPrerequisites.Should().ContainSingle().Which.Should().Contain("rechecked by the dispatch route");
        (await db.McpOperatorAcceptedAudits.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ResolveTarget_DoesNotRevealTargetFactsBeforeTenantVisibility()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        await SeedAsync(db, agentId);
        var service = new McpOperatorAccessEvaluationService(db, new McpOperatorAuthorization(db));
        var untrusted = Principal() with { Subject = "different-operator" };

        var target = await service.ResolveTargetAsync(McpOperatorEnvironment.Development, untrusted, 42, agentId, Ct);
        var evaluation = await service.EvaluateAsync(
            McpOperatorEnvironment.Development,
            untrusted,
            42,
            agentId,
            "netratel_terminal",
            "availability",
            "correlation-42",
            "request-42",
            Ct);

        target.TenantVisible.Should().BeFalse();
        target.Target.Should().BeNull();
        target.RelevantPolicies.Should().BeEmpty();
        evaluation.Should().BeNull();
    }

    [Theory]
    [InlineData(false, "oauth_scope_missing", "not_checked")]
    [InlineData(true, "target_policy_missing", "evaluated")]
    public async Task PolicyAdministratorEvaluation_ExplainsItsOwnExactAccessWithoutPriorTenantVisibility(
        bool includeObserveScope, string failureCode, string policyAssessment)
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        await SeedAsync(db, agentId);
        var service = new McpOperatorAccessEvaluationService(db, new McpOperatorAuthorization(db));
        var administrator = Principal() with
        {
            Subject = "policy.admin@example.test",
            Roles = includeObserveScope ? Set("PolicyAdministrator", "Operator") : Set("PolicyAdministrator"),
            Scopes = includeObserveScope ? Set("netratel.mcp.admin", "netratel.mcp.observe") : Set("netratel.mcp.admin")
        };

        var ordinary = await service.EvaluateAsync(
            McpOperatorEnvironment.Development,
            administrator,
            42,
            agentId,
            "netratel_terminal",
            "availability",
            "correlation-42",
            "request-42",
            Ct);
        var diagnostic = await service.EvaluateForPolicyAdministratorAsync(
            McpOperatorEnvironment.Development,
            administrator,
            42,
            agentId,
            "netratel_terminal",
            "availability",
            "correlation-42",
            "request-42",
            Ct);

        ordinary.Should().BeNull();
        diagnostic.Should().NotBeNull();
        diagnostic!.Target!.AgentId.Should().Be(agentId);
        diagnostic.Allowed.Should().BeFalse();
        diagnostic.FailureCode.Should().Be(failureCode);
        var interpretation = diagnostic.Interpretation!;
        interpretation.InspectionTool.Should().Be("netratel_policy");
        interpretation.InspectionOperation.Should().Be("evaluate");
        interpretation.Assessment.Should().Be("current_assertion_only");
        interpretation.EvaluatedTool.Should().Be("netratel_terminal");
        interpretation.EvaluatedOperation.Should().Be("availability");
        interpretation.RequiredScopes.Should().Equal("netratel.mcp.observe");
        interpretation.RequiredRolesAnyOf.Should().BeEquivalentTo("Observer", "Operator", "AutomationOperator");
        interpretation.DelegatedScopes.Should().BeEquivalentTo(administrator.Scopes);
        interpretation.DelegatedRoles.Should().BeEquivalentTo(administrator.Roles);
        interpretation.ApplicationGrants.Should().Be("not_checked");
        interpretation.TenantAdmission.Should().Be("not_checked");
        interpretation.Policy.Should().Be(policyAssessment);
        interpretation.Capability.Should().Be("not_checked");
        interpretation.Runtime.Should().Be("not_checked");
        interpretation.AuthoritativeAdmissionReceipt.Should().BeFalse();
        interpretation.Guidance.Should().Contain("inspection-context scope denial").And.Contain("netratel_access/receipt");
        if (!includeObserveScope)
            diagnostic.NextAction.Should().Contain("current inspection assertion").And.Contain("using its own delegation");
        (await db.McpOperatorAcceptedAudits.CountAsync(Ct)).Should().Be(0);
    }

    private static async Task SeedAsync(OrchestratorDbContext db, Guid agentId)
    {
        db.Agents.Add(new Agent
        {
            Id = agentId,
            TenantId = 42,
            IsEnabled = true,
            Status = AgentStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        db.McpOperatorTargetProfiles.Add(new McpOperatorTargetProfileRecord
        {
            AgentId = agentId,
            TenantId = 42,
            Classification = McpOperatorTargetClassification.DevelopmentSafe,
            TagsJson = "[\"qa\",\"development\"]",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedBy = "policy-admin@example.test",
            Version = 3
        });
        db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
        {
            Id = Guid.NewGuid(),
            Name = "terminal observation",
            Environment = McpOperatorEnvironment.Development,
            Effect = McpOperatorPolicyEffect.Allow,
            Priority = 10,
            PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
            PrincipalSelectorValue = "operator@example.test",
            TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent,
            TenantId = 42,
            AgentId = agentId,
            TargetClassification = McpOperatorTargetClassification.DevelopmentSafe,
            OperationFamily = McpOperatorOperationFamily.TerminalRead,
            ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(), McpOperatorJsonContext.Default.McpOperatorConstraints),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = "policy-admin@example.test",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            Version = 1
        });
        await db.SaveChangesAsync(Ct);
    }

    private static McpOperatorPrincipal Principal() => new(
        "operator@example.test",
        "automation-client",
        "automation-client",
        Set("netratel-operators"),
        Set("Operator"),
        Set("netratel.mcp.observe"),
        "netratel-mcp-dev");

    private static OrchestratorDbContext CreateDb() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}
