using System.Text.Json;
using FluentAssertions;
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

    [Fact]
    public async Task PolicyAdministratorEvaluation_ExplainsItsOwnExactAccessWithoutPriorTenantVisibility()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        await SeedAsync(db, agentId);
        var service = new McpOperatorAccessEvaluationService(db, new McpOperatorAuthorization(db));
        var administrator = Principal() with
        {
            Subject = "policy.admin@example.test",
            Roles = Set("PolicyAdministrator"),
            Scopes = Set("netratel.mcp.admin", "netratel.mcp.observe")
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
        diagnostic.FailureCode.Should().Be("target_policy_missing");
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
