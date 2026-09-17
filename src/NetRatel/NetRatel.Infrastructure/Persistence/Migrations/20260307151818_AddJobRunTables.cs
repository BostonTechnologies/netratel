using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobRunTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    JobId = table.Column<long>(type: "bigint", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    ClientIdentity = table.Column<string>(type: "text", nullable: false),
                    StartedBy = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentStepOrdinal = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    InputsJson = table.Column<string>(type: "text", nullable: true),
                    OptionsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobRuns_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobStepRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    JobRunId = table.Column<long>(type: "bigint", nullable: false),
                    JobStepId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    TaskRequestId = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobStepRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobStepRuns_JobRuns_JobRunId",
                        column: x => x.JobRunId,
                        principalTable: "JobRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobStepRuns_JobSteps_JobStepId",
                        column: x => x.JobStepId,
                        principalTable: "JobSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobTaskActivities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<string>(type: "text", nullable: false),
                    JobRunId = table.Column<long>(type: "bigint", nullable: false),
                    JobStepId = table.Column<long>(type: "bigint", nullable: false),
                    ClientIdentity = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    TaskType = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTaskActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobTaskActivities_JobRuns_JobRunId",
                        column: x => x.JobRunId,
                        principalTable: "JobRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JobTaskActivities_JobSteps_JobStepId",
                        column: x => x.JobStepId,
                        principalTable: "JobSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_ClientIdentity",
                table: "JobRuns",
                column: "ClientIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_CreatedAtUtc",
                table: "JobRuns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_JobId",
                table: "JobRuns",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_TenantId",
                table: "JobRuns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_JobStepRuns_JobRunId",
                table: "JobStepRuns",
                column: "JobRunId");

            migrationBuilder.CreateIndex(
                name: "IX_JobStepRuns_JobRunId_Ordinal",
                table: "JobStepRuns",
                columns: new[] { "JobRunId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_JobStepRuns_JobStepId",
                table: "JobStepRuns",
                column: "JobStepId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskActivities_CreatedAtUtc",
                table: "JobTaskActivities",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskActivities_JobRunId",
                table: "JobTaskActivities",
                column: "JobRunId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskActivities_JobStepId",
                table: "JobTaskActivities",
                column: "JobStepId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskActivities_RequestId",
                table: "JobTaskActivities",
                column: "RequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobStepRuns");

            migrationBuilder.DropTable(
                name: "JobTaskActivities");

            migrationBuilder.DropTable(
                name: "JobRuns");
        }
    }
}
