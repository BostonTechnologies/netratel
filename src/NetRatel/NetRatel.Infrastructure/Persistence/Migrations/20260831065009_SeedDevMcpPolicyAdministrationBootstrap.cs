using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedDevMcpPolicyAdministrationBootstrap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This is tied to one persisted Dev QA grant. It creates no row if
            // that grant is absent, revoked, expired, or reclassified. The
            // group-bound admission is held by a separate bootstrap identity.
            // It can grant PolicyAdministration to the normal policy-admin
            // group without self-grant, then that normal group can revoke this
            // bootstrap policy during Dev cleanup.
            migrationBuilder.Sql("""
                INSERT INTO "McpOperatorPolicies" (
                    "Id", "Name", "Environment", "Effect", "Priority",
                    "PrincipalSelectorKind", "PrincipalSelectorValue",
                    "TargetSelectorKind", "TenantId", "AgentId", "ClientTag",
                    "TargetClassification", "OperationFamily", "Operation",
                    "ConstraintsJson", "CreatedAtUtc", "CreatedBy", "ExpiresAtUtc",
                    "ReviewByUtc", "DisabledAtUtc", "DisabledBy", "Version", "AuditReference",
                    "LifecycleState")
                SELECT
                    md5('dev-mcp-policy-bootstrap:netratel-mcp-dev-policy-bootstrap-administrator:v1')::uuid,
                    'Dev QA PolicyAdministration bootstrap',
                    1, 2, 100,
                    3, 'netratel-mcp-dev-policy-bootstrap-administrator',
                    1, legacy_grant."TenantId", legacy_grant."AgentId", NULL,
                    1, 262144, NULL,
                    jsonb_build_object(
                        'maxFanOut', 1,
                        'allowedTargetClassifications', jsonb_build_array(1),
                        'destructiveOperationsAllowed', false),
                    clock_timestamp(), 'dev-mcp-policy-bootstrap-migration', legacy_grant."ExpiresAtUtc",
                    LEAST(legacy_grant."ExpiresAtUtc", clock_timestamp() + INTERVAL '7 days'),
                    NULL, NULL, 1,
                    'dev-policy-bootstrap:netratel-mcp-dev-policy-bootstrap-administrator', 1
                FROM "DevelopmentOperatorTargetGrants" AS legacy_grant
                WHERE legacy_grant."TenantId" = 1
                  AND legacy_grant."AgentId" = '1641ed2c-26f7-4277-abfb-81a56c26cee9'::uuid
                  AND legacy_grant."Classification" = 1
                  AND legacy_grant."RevokedAtUtc" IS NULL
                  AND legacy_grant."ExpiresAtUtc" > clock_timestamp()
                ON CONFLICT ("Id") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "McpOperatorPolicies" AS policy
                WHERE policy."Id" = md5('dev-mcp-policy-bootstrap:netratel-mcp-dev-policy-bootstrap-administrator:v1')::uuid
                  AND NOT EXISTS (
                    SELECT 1
                    FROM "McpOperatorAcceptedAudits" AS audit
                    WHERE audit."PolicyId" = policy."Id");
                """);
        }
    }
}
