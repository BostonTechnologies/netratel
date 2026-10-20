using AwesomeAssertions;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorPolicyPhaseAuthorizationTests
{
    [Fact]
    public void Reviewed_phase_pair_admits_a_stable_canonical_decision_after_an_old_policy_expires()
    {
        var now = DateTimeOffset.UtcNow;
        var policies = new[]
        {
            Policy("netratel_policy/create", now.AddMinutes(-1)),
            Policy("netratel_policy/preview_create", now.AddDays(1), auditReference: "pairing:review-1:0"),
            Policy("netratel_policy/confirm_create", now.AddDays(1), auditReference: "pairing:review-1:1")
        };

        McpOperatorAuthorization.EvaluateSnapshot(Request(), policies, now).IsAllowed.Should().BeTrue();
        var incomplete = McpOperatorAuthorization.EvaluateSnapshot(Request(), policies[..2], now);
        incomplete.IsAllowed.Should().BeFalse();
        incomplete.FailureCode.Should().Be("target_policy_expired");
    }

    [Fact]
    public void Active_legacy_canonical_deny_still_overrides_an_exact_phase_allow()
    {
        var now = DateTimeOffset.UtcNow;
        var policies = new[]
        {
            Policy("netratel_policy/create", now.AddDays(1), McpOperatorPolicyEffect.Deny),
            Policy("netratel_policy/preview_create", now.AddDays(1), auditReference: "pairing:review-1:0"),
            Policy("netratel_policy/confirm_create", now.AddDays(1), auditReference: "pairing:review-1:1")
        };

        var decision = McpOperatorAuthorization.EvaluateSnapshot(Request(), policies, now);
        decision.IsAllowed.Should().BeFalse();
        decision.FailureCode.Should().Be("target_classification_denied");
    }

    private static McpOperatorPolicyRecord Policy(string operation, DateTimeOffset expiry,
        McpOperatorPolicyEffect effect = McpOperatorPolicyEffect.Allow, string? auditReference = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = operation,
        Environment = McpOperatorEnvironment.Production,
        Effect = effect,
        PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthClientId,
        PrincipalSelectorValue = "reviewed-client",
        TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent,
        TenantId = 1,
        AgentId = AgentId,
        TargetClassification = McpOperatorTargetClassification.DedicatedQa,
        OperationFamily = McpOperatorOperationFamily.PolicyAdministration,
        Operation = operation,
        ExpiresAtUtc = expiry,
        Version = 1,
        AuditReference = auditReference
    };

    private static readonly Guid AgentId = Guid.Parse("bfa4673f-600a-472d-a1df-64d0e75aa009");

    private static McpOperatorAccessRequest Request() => new(
        McpOperatorEnvironment.Production,
        new McpOperatorPrincipal("owner", "reviewed-client", null, new HashSet<string>(),
            new HashSet<string> { "PolicyAdministrator" }, new HashSet<string> { "netratel.mcp.admin" }),
        1,
        AgentId,
        McpOperatorTargetClassification.DedicatedQa,
        McpOperatorOperationFamily.PolicyAdministration,
        "netratel_policy/create",
        new HashSet<string> { "netratel.mcp.admin" },
        McpOperatorConfirmationClass.PolicyAdministration,
        "phase-correlation",
        "phase-request",
        Tool: "netratel_policy");
}
