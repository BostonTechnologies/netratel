using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpOperatorDelegatedIdentityAuditFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorizedParty",
                table: "McpOperatorAcceptedAudits",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupsJson",
                table: "McpOperatorAcceptedAudits",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "McpInstance",
                table: "McpOperatorAcceptedAudits",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "McpResource",
                table: "McpOperatorAcceptedAudits",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RolesJson",
                table: "McpOperatorAcceptedAudits",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ScopesJson",
                table: "McpOperatorAcceptedAudits",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Tool",
                table: "McpOperatorAcceptedAudits",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthorizedParty",
                table: "McpOperatorAcceptedAudits");

            migrationBuilder.DropColumn(
                name: "GroupsJson",
                table: "McpOperatorAcceptedAudits");

            migrationBuilder.DropColumn(
                name: "McpInstance",
                table: "McpOperatorAcceptedAudits");

            migrationBuilder.DropColumn(
                name: "McpResource",
                table: "McpOperatorAcceptedAudits");

            migrationBuilder.DropColumn(
                name: "RolesJson",
                table: "McpOperatorAcceptedAudits");

            migrationBuilder.DropColumn(
                name: "ScopesJson",
                table: "McpOperatorAcceptedAudits");

            migrationBuilder.DropColumn(
                name: "Tool",
                table: "McpOperatorAcceptedAudits");
        }
    }
}
