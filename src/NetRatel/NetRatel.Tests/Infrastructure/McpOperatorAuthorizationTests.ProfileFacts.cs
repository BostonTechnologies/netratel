using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed partial class McpOperatorAuthorizationTests
{
    [Fact]
    public async Task Unknown_profile_cannot_bypass_a_complete_legacy_canonical_policy_deny()
    {
        await using var db = CreateDb();
        var request = Request() with { Tool = "netratel_policy", Operation = "netratel_policy/create", TargetClassification = null,
            OperationFamily = McpOperatorOperationFamily.PolicyAdministration, RequiredScopes = Set("netratel.mcp.admin"),
            ConfirmationClass = McpOperatorConfirmationClass.PolicyAdministration,
            Principal = Request().Principal with { Roles = Set("PolicyAdministrator"), Scopes = Set("netratel.mcp.admin") } };
        AddPolicy(db, request, applyTargetClassification: false);
        foreach (var phase in new[] { "preview_create", "confirm_create" })
        {
            AddPolicy(db, request with { Operation = "netratel_policy/" + phase }, McpOperatorPolicyEffect.Deny, applyTargetClassification: false);
            var deny = db.McpOperatorPolicies.Local.Last();
            deny.TargetClassification = McpOperatorTargetClassification.ManagedStandard;
            deny.AuditReference = "pairing:legacy-review:" + phase;
        }
        await db.SaveChangesAsync(Ct);
        var decision = await Authority(db).EvaluateAsync(request, Ct);
        decision.IsAllowed.Should().BeFalse(); decision.FailureCode.Should().Be("target_profile_required");
        decision.MatchingPolicyIds.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("classification")]
    [InlineData("tag")]
    [InlineData("tags")]
    [InlineData("constraint")]
    [InlineData("parent")]
    public async Task Unprofiled_client_cannot_bypass_a_potentially_applicable_deny(string restriction)
    {
        await using var db = CreateDb();
        var request = Request() with { Environment = McpOperatorEnvironment.Production, TargetClassification = null, TargetTags = null };
        AddPolicy(db, request, applyTargetClassification: false, targetSelectorKind: McpOperatorTargetSelectorKind.Tenant);
        AddPolicy(db, request, McpOperatorPolicyEffect.Deny, applyTargetClassification: false);
        var deny = db.McpOperatorPolicies.Local.Single(p => p.Effect == McpOperatorPolicyEffect.Deny);
        switch (restriction)
        {
            case "classification": deny.TargetClassification = McpOperatorTargetClassification.ManagedStandard; break;
            case "tag": deny.TargetSelectorKind = McpOperatorTargetSelectorKind.ClientTag; deny.ClientTag = "restricted"; break;
            case "tags": deny.ClientTags = ["restricted"]; break;
            case "constraint": deny.ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(AllowedTargetClassifications: [McpOperatorTargetClassification.ManagedStandard]), McpOperatorJsonContext.Default.McpOperatorConstraints); break;
            case "parent":
                AddPolicy(db, request, applyTargetClassification: false, targetSelectorKind: McpOperatorTargetSelectorKind.Tenant);
                var parent = db.McpOperatorPolicies.Local.Last(); parent.ClientTags = ["restricted"]; parent.IsContainer = true;
                deny.ParentPolicyId = parent.Id;
                break;
        }
        await db.SaveChangesAsync(Ct);
        var decision = await Authority(db).EvaluateAsync(request, Ct);
        decision.IsAllowed.Should().BeFalse();
        decision.FailureCode.Should().Be("target_profile_required");
        decision.MatchingPolicyIds.Should().Contain(deny.Id);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("operation")]
    [InlineData("principal")]
    [InlineData("resource")]
    [InlineData("agent")]
    [InlineData("family")]
    [InlineData("parent_operation")]
    [InlineData("constraint")]
    public async Task Unrelated_deny_does_not_require_profile_facts(string mismatch)
    {
        await using var db = CreateDb();
        var request = Request() with { Environment = McpOperatorEnvironment.Production, TargetClassification = null, TargetTags = null };
        AddPolicy(db, request, applyTargetClassification: false, targetSelectorKind: McpOperatorTargetSelectorKind.Tenant);
        AddPolicy(db, request, McpOperatorPolicyEffect.Deny, applyTargetClassification: false);
        var deny = db.McpOperatorPolicies.Local.Single(p => p.Effect == McpOperatorPolicyEffect.Deny);
        deny.ClientTags = ["restricted"];
        switch (mismatch)
        {
            case "tenant": deny.TenantId++; break;
            case "operation": deny.Operation = "netratel_terminal/close"; break;
            case "principal": deny.PrincipalSelectorValue = "another-operator"; break;
            case "resource": deny.Resource = "https://other.example.test/mcp"; break;
            case "agent": deny.AgentId = Guid.NewGuid(); break;
            case "family": deny.OperationFamily = McpOperatorOperationFamily.FileRead; break;
            case "constraint":
                request = request with { TargetClassification = McpOperatorTargetClassification.DevelopmentSafe };
                deny.ClientTags = null;
                deny.ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(AllowedTargetClassifications: [McpOperatorTargetClassification.ManagedStandard]), McpOperatorJsonContext.Default.McpOperatorConstraints);
                break;
            case "parent_operation":
                AddPolicy(db, request, applyTargetClassification: false, targetSelectorKind: McpOperatorTargetSelectorKind.Tenant);
                var parent = db.McpOperatorPolicies.Local.Last(); parent.IsContainer = true; parent.Operation = "netratel_terminal/close";
                deny.ParentPolicyId = parent.Id;
                break;
        }
        await db.SaveChangesAsync(Ct);
        (await Authority(db).EvaluateAsync(request, Ct)).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Validated_empty_tags_are_known_and_definite_deny_wins_over_unknown_deny()
    {
        await using var db = CreateDb();
        var request = Request() with { TargetClassification = null, TargetTags = Set() };
        AddPolicy(db, request, applyTargetClassification: false);
        AddPolicy(db, request, McpOperatorPolicyEffect.Deny, applyTargetClassification: false);
        var tagDeny = db.McpOperatorPolicies.Local.Single(p => p.Effect == McpOperatorPolicyEffect.Deny);
        tagDeny.ClientTags = ["restricted"];
        await db.SaveChangesAsync(Ct);
        (await Authority(db).EvaluateAsync(request, Ct)).IsAllowed.Should().BeTrue();
        (await Authority(db).EvaluateAsync(request with { TargetTags = null }, Ct)).FailureCode.Should().Be("target_profile_required");
        AddPolicy(db, request, McpOperatorPolicyEffect.Deny, applyTargetClassification: false);
        await db.SaveChangesAsync(Ct);
        (await Authority(db).EvaluateAsync(request with { TargetTags = null }, Ct)).FailureCode.Should().Be("target_policy_denied");
    }
}
