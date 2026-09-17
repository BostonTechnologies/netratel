using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpOperatorTargetProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpOperatorTargetProfiles",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Classification = table.Column<short>(type: "smallint", nullable: false),
                    TagsJson = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorTargetProfiles", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_McpOperatorTargetProfiles_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTargetProfiles_TenantId_Classification",
                table: "McpOperatorTargetProfiles",
                columns: new[] { "TenantId", "Classification" });

            // Preserve the target classification that was approved with each
            // active legacy Development grant. The corresponding compatibility
            // policy was migrated by AddMcpOperatorPolicyFoundation; this row
            // lets the policy evaluator continue to enforce the same target
            // class after the legacy route is retired.
            migrationBuilder.Sql("""
                INSERT INTO "McpOperatorTargetProfiles" (
                    "AgentId", "TenantId", "Classification", "TagsJson",
                    "UpdatedAtUtc", "UpdatedBy", "Version")
                SELECT
                    legacy_grant."AgentId",
                    legacy_grant."TenantId",
                    CASE legacy_grant."Classification"
                        WHEN 1 THEN 1
                        WHEN 2 THEN 2
                        ELSE 3
                    END,
                    '[]'::jsonb,
                    legacy_grant."GrantedAtUtc",
                    legacy_grant."GrantedBy",
                    1
                FROM "DevelopmentOperatorTargetGrants" AS legacy_grant
                WHERE legacy_grant."RevokedAtUtc" IS NULL
                ON CONFLICT ("AgentId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpOperatorTargetProfiles");
        }
    }
}
