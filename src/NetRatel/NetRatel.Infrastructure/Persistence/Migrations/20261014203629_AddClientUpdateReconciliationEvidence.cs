using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClientUpdateReconciliationEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientUpdateAttempts_AgentId_ReleaseId",
                table: "ClientUpdateAttempts");

            migrationBuilder.AddColumn<string>(
                name: "ReconciliationState",
                table: "ClientUpdateAttempts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReportedAtUtc",
                table: "ClientUpdateAttempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportedFailureCode",
                table: "ClientUpdateAttempts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportedMessage",
                table: "ClientUpdateAttempts",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "ReportedState",
                table: "ClientUpdateAttempts",
                type: "smallint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_AgentId_ReleaseId",
                table: "ClientUpdateAttempts",
                columns: new[] { "AgentId", "ReleaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_AgentId_ReleaseId_AdmissionNonceHash",
                table: "ClientUpdateAttempts",
                columns: new[] { "AgentId", "ReleaseId", "AdmissionNonceHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientUpdateAttempts_AgentId_ReleaseId",
                table: "ClientUpdateAttempts");

            migrationBuilder.DropIndex(
                name: "IX_ClientUpdateAttempts_AgentId_ReleaseId_AdmissionNonceHash",
                table: "ClientUpdateAttempts");

            migrationBuilder.DropColumn(
                name: "ReconciliationState",
                table: "ClientUpdateAttempts");

            migrationBuilder.DropColumn(
                name: "ReportedAtUtc",
                table: "ClientUpdateAttempts");

            migrationBuilder.DropColumn(
                name: "ReportedFailureCode",
                table: "ClientUpdateAttempts");

            migrationBuilder.DropColumn(
                name: "ReportedMessage",
                table: "ClientUpdateAttempts");

            migrationBuilder.DropColumn(
                name: "ReportedState",
                table: "ClientUpdateAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_AgentId_ReleaseId",
                table: "ClientUpdateAttempts",
                columns: new[] { "AgentId", "ReleaseId" },
                unique: true);
        }
    }
}
