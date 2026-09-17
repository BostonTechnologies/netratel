using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

/// <summary>
/// Keeps the Dev bootstrap admission bound to the exact reviewed QA target
/// while removing classification predicates that the policy-administration
/// mutation contract intentionally does not carry at evaluation time.
/// </summary>
[DbContext(typeof(global::NetRatel.Infrastructure.Persistence.OrchestratorDbContext))]
[Migration("20260831083000_RemoveUnresolvableDevBootstrapClassificationPredicate")]
public partial class RemoveUnresolvableDevBootstrapClassificationPredicate : Migration
{
    private const string BootstrapPolicyId = "dev-mcp-policy-bootstrap:netratel-mcp-dev-policy-bootstrap-administrator:v1";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Policy-administration mutations bind an exact tenant and agent, but
        // deliberately do not accept a caller-supplied target classification.
        // Retaining a classification predicate here would therefore make this
        // otherwise exact, non-destructive bootstrap admission uncallable.
        // The original migration still verifies DedicatedQa before inserting
        // the row; this follow-up preserves the exact selector, expiry/review,
        // PolicyAdministration-only family, and maxFanOut=1 constraints.
        migrationBuilder.Sql($"""
            UPDATE "McpOperatorPolicies"
            SET "TargetClassification" = NULL,
                "ConstraintsJson" = jsonb_build_object(
                    'maxFanOut', 1,
                    'destructiveOperationsAllowed', false),
                "Version" = "Version" + 1
            WHERE "Id" = md5('{BootstrapPolicyId}')::uuid
              AND "Environment" = 1
              AND "Effect" = 2
              AND "Priority" = 100
              AND "PrincipalSelectorKind" = 3
              AND "PrincipalSelectorValue" = 'netratel-mcp-dev-policy-bootstrap-administrator'
              AND "TargetSelectorKind" = 1
              AND "TenantId" = 1
              AND "AgentId" = '1641ed2c-26f7-4277-abfb-81a56c26cee9'::uuid
              AND "TargetClassification" = 1
              AND "OperationFamily" = 262144
              AND "Operation" IS NULL
              AND "LifecycleState" = 1
              AND "ConstraintsJson" @> jsonb_build_object(
                    'maxFanOut', 1,
                    'allowedTargetClassifications', jsonb_build_array(1),
                    'destructiveOperationsAllowed', false)
              AND NOT EXISTS (
                    SELECT 1
                    FROM "McpOperatorAcceptedAudits" AS audit
                    WHERE audit."PolicyId" = "McpOperatorPolicies"."Id");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            UPDATE "McpOperatorPolicies"
            SET "TargetClassification" = 1,
                "ConstraintsJson" = jsonb_build_object(
                    'maxFanOut', 1,
                    'allowedTargetClassifications', jsonb_build_array(1),
                    'destructiveOperationsAllowed', false),
                "Version" = GREATEST(1, "Version" - 1)
            WHERE "Id" = md5('{BootstrapPolicyId}')::uuid
              AND "Environment" = 1
              AND "Effect" = 2
              AND "Priority" = 100
              AND "PrincipalSelectorKind" = 3
              AND "PrincipalSelectorValue" = 'netratel-mcp-dev-policy-bootstrap-administrator'
              AND "TargetSelectorKind" = 1
              AND "TenantId" = 1
              AND "AgentId" = '1641ed2c-26f7-4277-abfb-81a56c26cee9'::uuid
              AND "TargetClassification" IS NULL
              AND "OperationFamily" = 262144
              AND "Operation" IS NULL
              AND "LifecycleState" = 1
              AND "ConstraintsJson" = jsonb_build_object(
                    'maxFanOut', 1,
                    'destructiveOperationsAllowed', false)
              AND NOT EXISTS (
                    SELECT 1
                    FROM "McpOperatorAcceptedAudits" AS audit
                    WHERE audit."PolicyId" = "McpOperatorPolicies"."Id");
            """);
    }
}
