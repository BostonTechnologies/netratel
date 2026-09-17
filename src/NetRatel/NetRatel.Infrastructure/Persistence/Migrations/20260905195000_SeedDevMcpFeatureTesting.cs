using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NetRatel.Infrastructure.Persistence.Migrations;

/// <summary>Reversible, group-bound full Dev admission for application feature certification.</summary>
[DbContext(typeof(OrchestratorDbContext))]
[Migration("20260905195000_SeedDevMcpFeatureTesting")]
public sealed class SeedDevMcpFeatureTesting : Migration
{
    private const string PolicyKey = "dev-mcp-feature-testing:netratel-mcp-dev-feature-testers:v1";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // This selector is rejected by the evaluator in Production, even if a
        // database copy contains this row. Membership is issued only by the Dev IdP.
        migrationBuilder.Sql($"""
            INSERT INTO "McpOperatorPolicies" (
                "Id", "Name", "Environment", "Effect", "Priority",
                "PrincipalSelectorKind", "PrincipalSelectorValue",
                "TargetSelectorKind", "TenantId", "AgentId", "ClientTag",
                "TargetClassification", "OperationFamily", "Operation", "ConstraintsJson",
                "CreatedAtUtc", "CreatedBy", "Version", "AuditReference", "LifecycleState")
            VALUES (
                md5('{PolicyKey}')::uuid, 'Full Dev MCP feature testing', 1, 2, 1000,
                3, 'netratel-mcp-dev-feature-testers', 5, 0, NULL, NULL, NULL, 524287, NULL,
                jsonb_build_object(
                    'readRoots', jsonb_build_array('/', 'A:/', 'B:/', 'C:/', 'D:/', 'E:/', 'F:/', 'G:/', 'H:/', 'I:/', 'J:/', 'K:/', 'L:/', 'M:/', 'N:/', 'O:/', 'P:/', 'Q:/', 'R:/', 'S:/', 'T:/', 'U:/', 'V:/', 'W:/', 'X:/', 'Y:/', 'Z:/'),
                    'writeRoots', jsonb_build_array('/', 'A:/', 'B:/', 'C:/', 'D:/', 'E:/', 'F:/', 'G:/', 'H:/', 'I:/', 'J:/', 'K:/', 'L:/', 'M:/', 'N:/', 'O:/', 'P:/', 'Q:/', 'R:/', 'S:/', 'T:/', 'U:/', 'V:/', 'W:/', 'X:/', 'Y:/', 'Z:/'),
                    'allowedShells', jsonb_build_array('bash', 'sh', 'pwsh', 'powershell', 'cmd', 'zsh', 'fish'),
                    'workingDirectories', jsonb_build_array('*'),
                    'maxCommandDurationSeconds', 3600,
                    'maxTerminalIdleSeconds', 3600,
                    'maxTerminalLifetimeSeconds', 86400,
                    'maxConcurrentTerminalSessions', 100,
                    'maxConcurrentCommands', 100,
                    'maxOutputBytes', 16777216,
                    'maxArtifactBytes', 268435456,
                    'maxScriptBytes', 1048576,
                    'maxJobTargetCount', 1000,
                    'maxTaskTargetCount', 1000,
                    'maxFanOut', 1000,
                    'maxOnboardingCodeLifetimeSeconds', 2592000,
                    'maxOnboardingCodeUses', 10000,
                    'destructiveOperationsAllowed', true,
                    'allowActiveTenantDeletion', true),
                clock_timestamp(), 'dev-mcp-feature-testing-migration', 1,
                'public-source-migration', 1)
            ON CONFLICT ("Id") DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve accepted audit foreign keys while withdrawing the grant.
        migrationBuilder.Sql($"""
            UPDATE "McpOperatorPolicies"
            SET "LifecycleState" = 3, "DisabledAtUtc" = clock_timestamp(),
                "DisabledBy" = 'dev-mcp-feature-testing-rollback', "Version" = "Version" + 1
            WHERE "Id" = md5('{PolicyKey}')::uuid;
            """);
    }
}
