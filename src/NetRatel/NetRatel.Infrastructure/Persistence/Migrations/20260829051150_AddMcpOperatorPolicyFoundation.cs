using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpOperatorPolicyFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpOperatorPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Environment = table.Column<short>(type: "smallint", nullable: false),
                    Effect = table.Column<short>(type: "smallint", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    PrincipalSelectorKind = table.Column<short>(type: "smallint", nullable: false),
                    PrincipalSelectorValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TargetSelectorKind = table.Column<short>(type: "smallint", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientTag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TargetClassification = table.Column<short>(type: "smallint", nullable: true),
                    OperationFamily = table.Column<int>(type: "integer", nullable: false),
                    Operation = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConstraintsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewByUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisabledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisabledBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    AuditReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorAcceptedAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<short>(type: "smallint", nullable: false),
                    ServicePrincipal = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    OperationFamily = table.Column<int>(type: "integer", nullable: false),
                    Operation = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorAcceptedAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorAcceptedAudits_McpOperatorPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "McpOperatorPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorAcceptedAudits_PolicyId_OccurredAtUtc",
                table: "McpOperatorAcceptedAudits",
                columns: new[] { "PolicyId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorAcceptedAudits_RequestId_OccurredAtUtc",
                table: "McpOperatorAcceptedAudits",
                columns: new[] { "RequestId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorAcceptedAudits_TenantId_AgentId_OccurredAtUtc",
                table: "McpOperatorAcceptedAudits",
                columns: new[] { "TenantId", "AgentId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicies_Environment_PrincipalSelectorKind_Princ~",
                table: "McpOperatorPolicies",
                columns: new[] { "Environment", "PrincipalSelectorKind", "PrincipalSelectorValue" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicies_Environment_TargetSelectorKind_TenantId~",
                table: "McpOperatorPolicies",
                columns: new[] { "Environment", "TargetSelectorKind", "TenantId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicies_Environment_TenantId_DisabledAtUtc_Expi~",
                table: "McpOperatorPolicies",
                columns: new[] { "Environment", "TenantId", "DisabledAtUtc", "ExpiresAtUtc", "Effect", "Priority" });

            // Preserve the active Development QA grants as explicit temporary
            // compatibility policies. The legacy grant and audit tables remain
            // immutable evidence; no Production policy is created here.
            migrationBuilder.Sql("""
                INSERT INTO "McpOperatorPolicies" (
                    "Id", "Name", "Environment", "Effect", "Priority",
                    "PrincipalSelectorKind", "PrincipalSelectorValue",
                    "TargetSelectorKind", "TenantId", "AgentId", "ClientTag",
                    "TargetClassification", "OperationFamily", "Operation",
                    "ConstraintsJson", "CreatedAtUtc", "CreatedBy", "ExpiresAtUtc",
                    "ReviewByUtc", "DisabledAtUtc", "DisabledBy", "Version", "AuditReference")
                SELECT
                    md5('legacy-dev-mcp-policy:' || legacy_grant."Id"::text)::uuid,
                    left('Legacy Dev target grant ' || legacy_grant."Id"::text, 160),
                    1, 2, 0,
                    5, 'development-compatibility',
                    1, legacy_grant."TenantId", legacy_grant."AgentId", NULL,
                    CASE legacy_grant."Classification" WHEN 1 THEN 1 WHEN 2 THEN 2 ELSE NULL END,
                    (CASE WHEN (legacy_grant."AllowedOperations" & 1) <> 0 THEN 2816 ELSE 0 END) |
                    (CASE WHEN (legacy_grant."AllowedOperations" & 2) <> 0 THEN 1024 ELSE 0 END) |
                    (CASE WHEN (legacy_grant."AllowedOperations" & 4) <> 0 THEN 16 ELSE 0 END) |
                    (CASE WHEN (legacy_grant."AllowedOperations" & 8) <> 0 THEN 6 ELSE 0 END) |
                    (CASE WHEN (legacy_grant."AllowedOperations" & 16) <> 0 THEN 24 ELSE 0 END) |
                    (CASE WHEN (legacy_grant."AllowedOperations" & 32) <> 0 THEN 96 ELSE 0 END) |
                    (CASE WHEN (legacy_grant."AllowedOperations" & 64) <> 0 THEN 4096 ELSE 0 END) |
                    (CASE WHEN (legacy_grant."AllowedOperations" & 128) <> 0 THEN 65536 ELSE 0 END) |
                    (CASE WHEN (legacy_grant."AllowedOperations" & 256) <> 0 THEN 131072 ELSE 0 END) |
                    (CASE WHEN (legacy_grant."AllowedOperations" & 512) <> 0 THEN 1 ELSE 0 END),
                    NULL,
                    jsonb_strip_nulls(jsonb_build_object(
                        'readRoots', CASE WHEN legacy_grant."FileFixtureRoot" IS NULL THEN NULL ELSE jsonb_build_array(legacy_grant."FileFixtureRoot") END,
                        'writeRoots', CASE WHEN legacy_grant."FileFixtureRoot" IS NULL THEN NULL ELSE jsonb_build_array(legacy_grant."FileFixtureRoot") END)),
                    legacy_grant."GrantedAtUtc", legacy_grant."GrantedBy", legacy_grant."ExpiresAtUtc",
                    NULL, NULL, NULL, 1, 'legacy-dev-grant:' || legacy_grant."Id"::text
                FROM "DevelopmentOperatorTargetGrants" AS legacy_grant
                WHERE legacy_grant."RevokedAtUtc" IS NULL
                ON CONFLICT ("Id") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpOperatorAcceptedAudits");

            migrationBuilder.DropTable(
                name: "McpOperatorPolicies");
        }
    }
}
