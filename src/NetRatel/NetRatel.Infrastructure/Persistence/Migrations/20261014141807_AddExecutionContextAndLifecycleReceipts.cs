using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionContextAndLifecycleReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommandLabel",
                table: "JobTaskActivities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Environment",
                table: "JobTaskActivities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExitCode",
                table: "JobTaskActivities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScriptContentHash",
                table: "JobTaskActivities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScriptName",
                table: "JobTaskActivities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScriptType",
                table: "JobTaskActivities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAtUtc",
                table: "JobTaskActivities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDisplayName",
                table: "JobTaskActivities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetHostName",
                table: "JobTaskActivities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetName",
                table: "JobTaskActivities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Environment",
                table: "JobRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDisplayName",
                table: "JobRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetHostName",
                table: "JobRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetName",
                table: "JobRuns",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JobGatewayLifecycleReceipts",
                columns: table => new
                {
                    JobRunId = table.Column<long>(type: "bigint", nullable: false),
                    JobStepRunId = table.Column<long>(type: "bigint", nullable: false),
                    LifecycleSequence = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClientReportedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClientStartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LifecycleStatus = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobGatewayLifecycleReceipts", x => new { x.JobRunId, x.JobStepRunId, x.LifecycleSequence });
                    table.ForeignKey(
                        name: "FK_JobGatewayLifecycleReceipts_JobRuns_JobRunId",
                        column: x => x.JobRunId,
                        principalTable: "JobRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobGatewayLifecycleReceipts");

            migrationBuilder.DropColumn(
                name: "CommandLabel",
                table: "JobTaskActivities");

            migrationBuilder.DropColumn(
                name: "Environment",
                table: "JobTaskActivities");

            migrationBuilder.DropColumn(
                name: "ExitCode",
                table: "JobTaskActivities");

            migrationBuilder.DropColumn(
                name: "ScriptContentHash",
                table: "JobTaskActivities");

            migrationBuilder.DropColumn(
                name: "ScriptName",
                table: "JobTaskActivities");

            migrationBuilder.DropColumn(
                name: "ScriptType",
                table: "JobTaskActivities");

            migrationBuilder.DropColumn(
                name: "StartedAtUtc",
                table: "JobTaskActivities");

            migrationBuilder.DropColumn(
                name: "TargetDisplayName",
                table: "JobTaskActivities");

            migrationBuilder.DropColumn(
                name: "TargetHostName",
                table: "JobTaskActivities");

            migrationBuilder.DropColumn(
                name: "TargetName",
                table: "JobTaskActivities");

            migrationBuilder.DropColumn(
                name: "Environment",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "TargetDisplayName",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "TargetHostName",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "TargetName",
                table: "JobRuns");
        }
    }
}
