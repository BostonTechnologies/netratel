using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAkkaOrchestrationTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TargetAgentId",
                table: "Requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetTenantId",
                table: "Requests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "JobTaskActivities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "Jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentId",
                table: "JobRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requests_TargetTenantId_TargetAgentId",
                table: "Requests",
                columns: new[] { "TargetTenantId", "TargetAgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskActivities_TenantId_AgentId_CreatedAtUtc",
                table: "JobTaskActivities",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_TenantId_AgentId",
                table: "Jobs",
                columns: new[] { "TenantId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_TenantId_AgentId_CreatedAtUtc",
                table: "JobRuns",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Requests_TargetTenantId_TargetAgentId",
                table: "Requests");

            migrationBuilder.DropIndex(
                name: "IX_JobTaskActivities_TenantId_AgentId_CreatedAtUtc",
                table: "JobTaskActivities");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_TenantId_AgentId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_JobRuns_TenantId_AgentId_CreatedAtUtc",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "TargetAgentId",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "TargetTenantId",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "JobTaskActivities");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "JobRuns");
        }
    }
}
