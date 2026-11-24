using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedExecutionAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionAccountBindingsJson",
                table: "JobAggregateControls",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultJobCredentialProfileId",
                table: "Agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultTerminalCredentialProfileId",
                table: "Agents",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionAccountBindingsJson",
                table: "JobAggregateControls");

            migrationBuilder.DropColumn(
                name: "DefaultJobCredentialProfileId",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "DefaultTerminalCredentialProfileId",
                table: "Agents");
        }
    }
}
