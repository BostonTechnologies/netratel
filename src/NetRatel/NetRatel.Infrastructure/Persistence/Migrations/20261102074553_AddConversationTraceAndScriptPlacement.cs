using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationTraceAndScriptPlacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FolderPath",
                table: "McpOperatorScriptVersions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceRevision",
                table: "McpOperatorScriptVersions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRecoveryAttemptAtUtc",
                table: "AiInvestigations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoverySummary",
                table: "AiInvestigations",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RemoteTurnCountBeforeSend",
                table: "AiInvestigations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceJson",
                table: "AiInvestigationEvents",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FolderPath",
                table: "McpOperatorScriptVersions");

            migrationBuilder.DropColumn(
                name: "SourceRevision",
                table: "McpOperatorScriptVersions");

            migrationBuilder.DropColumn(
                name: "LastRecoveryAttemptAtUtc",
                table: "AiInvestigations");

            migrationBuilder.DropColumn(
                name: "RecoverySummary",
                table: "AiInvestigations");

            migrationBuilder.DropColumn(
                name: "RemoteTurnCountBeforeSend",
                table: "AiInvestigations");

            migrationBuilder.DropColumn(
                name: "TraceJson",
                table: "AiInvestigationEvents");
        }
    }
}
