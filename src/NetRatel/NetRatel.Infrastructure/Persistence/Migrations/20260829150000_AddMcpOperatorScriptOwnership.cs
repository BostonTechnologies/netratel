using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds the explicit tenant/caller ownership and immutable metadata history
/// required before the existing global script-content table can be used by a
/// Production MCP operator route.
/// </summary>
[DbContext(typeof(global::NetRatel.Infrastructure.Persistence.OrchestratorDbContext))]
[Migration("20260829150000_AddMcpOperatorScriptOwnership")]
public partial class AddMcpOperatorScriptOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "McpOperatorScripts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ScriptId = table.Column<long>(type: "bigint", nullable: false),
                TenantId = table.Column<int>(type: "integer", nullable: false),
                Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                McpResource = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                McpInstance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ShellType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                PolicyVersion = table.Column<long>(type: "bigint", nullable: false),
                ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                WorkingDirectory = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                DeclaredSideEffectsJson = table.Column<string>(type: "jsonb", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorScripts", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorScripts_McpOperatorPolicies_PolicyId",
                    column: x => x.PolicyId,
                    principalTable: "McpOperatorPolicies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorScripts_Scripts_ScriptId",
                    column: x => x.ScriptId,
                    principalTable: "Scripts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "McpOperatorScriptVersions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ScriptRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                ScriptId = table.Column<long>(type: "bigint", nullable: false),
                ScriptVersion = table.Column<long>(type: "bigint", nullable: false),
                Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ShellType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                WorkingDirectory = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                DeclaredSideEffectsJson = table.Column<string>(type: "jsonb", nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorScriptVersions", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorScriptVersions_McpOperatorAcceptedAudits_AcceptedAuditId",
                    column: x => x.AcceptedAuditId,
                    principalTable: "McpOperatorAcceptedAudits",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorScriptVersions_McpOperatorScripts_ScriptRecordId",
                    column: x => x.ScriptRecordId,
                    principalTable: "McpOperatorScripts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(name: "IX_McpOperatorScripts_PolicyId", table: "McpOperatorScripts", column: "PolicyId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorScripts_ScriptId", table: "McpOperatorScripts", column: "ScriptId", unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorScripts_TenantId_Subject_ClientId_McpResource_McpInstance_DeletedAtUtc_UpdatedAtUtc",
            table: "McpOperatorScripts",
            columns: new[] { "TenantId", "Subject", "ClientId", "McpResource", "McpInstance", "DeletedAtUtc", "UpdatedAtUtc" });
        migrationBuilder.CreateIndex(name: "IX_McpOperatorScriptVersions_AcceptedAuditId", table: "McpOperatorScriptVersions", column: "AcceptedAuditId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorScriptVersions_ScriptId_OccurredAtUtc", table: "McpOperatorScriptVersions", columns: new[] { "ScriptId", "OccurredAtUtc" });
        migrationBuilder.CreateIndex(name: "IX_McpOperatorScriptVersions_ScriptRecordId_ScriptVersion", table: "McpOperatorScriptVersions", columns: new[] { "ScriptRecordId", "ScriptVersion" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "McpOperatorScriptVersions");
        migrationBuilder.DropTable(name: "McpOperatorScripts");
    }
}
