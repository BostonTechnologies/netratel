using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the bounded Dev tenant provisioning admission needed for the
    /// separate normal policy administrator to create the reviewed tenant and
    /// canary policies through the MCP preview/confirm contract.
    /// </summary>
    public partial class SeedDevMcpPolicyAdminTenantProvisioning : Migration
    {
        private const string ProvisioningAdmissionId = "dev-mcp-policy-bootstrap:netratel-mcp-dev-policy-administrator:tenant-provisioning:v1";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The existing normal admission is intentionally exact-QA-bound.
            // A tenant selector has no single agent/classification to carry to
            // the policy-mutation admission route, so it cannot be created by
            // that record. This Dev-only policy grants PolicyAdministration
            // (not workload authority) to the separate normal admin for tenant
            // 1, preserves the existing review/expiry ceiling, and is seeded
            // only after that exact normal admission has been established.
            migrationBuilder.Sql($"""
                INSERT INTO "McpOperatorPolicies" (
                    "Id", "Name", "Environment", "Effect", "Priority",
                    "PrincipalSelectorKind", "PrincipalSelectorValue",
                    "TargetSelectorKind", "TenantId", "AgentId", "ClientTag",
                    "TargetClassification", "OperationFamily", "Operation",
                    "ConstraintsJson", "CreatedAtUtc", "CreatedBy", "ExpiresAtUtc",
                    "ReviewByUtc", "DisabledAtUtc", "DisabledBy", "Version", "AuditReference",
                    "LifecycleState")
                SELECT
                    md5('{ProvisioningAdmissionId}')::uuid,
                    'Dev tenant PolicyAdministration provisioning admission',
                    1, 2, 80,
                    3, 'netratel-mcp-dev-policy-administrator',
                    2, normal_admission."TenantId", NULL, NULL,
                    NULL, 262144, NULL,
                    jsonb_build_object(
                        'maxFanOut', 1,
                        'destructiveOperationsAllowed', false),
                    clock_timestamp(), 'dev-mcp-policy-provisioning-migration',
                    LEAST(legacy_grant."ExpiresAtUtc", normal_admission."ExpiresAtUtc"),
                    LEAST(legacy_grant."ExpiresAtUtc", normal_admission."ReviewByUtc"),
                    NULL, NULL, 1,
                    'dev-policy-bootstrap:netratel-mcp-dev-policy-administrator:tenant-provisioning', 1
                FROM "DevelopmentOperatorTargetGrants" AS legacy_grant
                JOIN "McpOperatorPolicies" AS normal_admission
                  ON normal_admission."Environment" = 1
                 AND normal_admission."Effect" = 2
                 AND normal_admission."PrincipalSelectorKind" = 3
                 AND normal_admission."PrincipalSelectorValue" = 'netratel-mcp-dev-policy-administrator'
                 AND normal_admission."TargetSelectorKind" = 1
                 AND normal_admission."TenantId" = 1
                 AND normal_admission."AgentId" = '1641ed2c-26f7-4277-abfb-81a56c26cee9'::uuid
                 AND normal_admission."OperationFamily" = 262144
                 AND normal_admission."Operation" IS NULL
                 AND normal_admission."ExpiresAtUtc" > clock_timestamp()
                 AND normal_admission."ReviewByUtc" IS NOT NULL
                 AND normal_admission."LifecycleState" = 1
                WHERE legacy_grant."TenantId" = 1
                  AND legacy_grant."AgentId" = '1641ed2c-26f7-4277-abfb-81a56c26cee9'::uuid
                  AND legacy_grant."Classification" = 1
                  AND legacy_grant."RevokedAtUtc" IS NULL
                  AND legacy_grant."ExpiresAtUtc" > clock_timestamp()
                ON CONFLICT ("Id") DO NOTHING;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                DELETE FROM "McpOperatorPolicies" AS policy
                WHERE policy."Id" = md5('{ProvisioningAdmissionId}')::uuid
                  AND NOT EXISTS (
                    SELECT 1
                    FROM "McpOperatorAcceptedAudits" AS audit
                    WHERE audit."PolicyId" = policy."Id");
                """);
        }
    }
}
