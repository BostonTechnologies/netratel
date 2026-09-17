using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobShadowObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobShadowObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSystem = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceEventId = table.Column<long>(type: "bigint", nullable: false),
                    JobRunId = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    JobId = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    ClientIdentity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    StartedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RunStatus = table.Column<short>(type: "smallint", nullable: true),
                    CurrentStepOrdinal = table.Column<int>(type: "integer", nullable: true),
                    RunCreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    JobStepRunId = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: true),
                    JobStepId = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: true),
                    StepStatus = table.Column<short>(type: "smallint", nullable: true),
                    StepOrdinal = table.Column<int>(type: "integer", nullable: true),
                    TaskRequestId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CommandCorrelationStatus = table.Column<short>(type: "smallint", nullable: false),
                    CorrelatedCommandStatus = table.Column<short>(type: "smallint", nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsAuthoritative = table.Column<bool>(type: "boolean", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobShadowObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobShadowObservations_JobRunId_SourceEventId",
                table: "JobShadowObservations",
                columns: new[] { "JobRunId", "SourceEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_JobShadowObservations_RecordedAtUtc",
                table: "JobShadowObservations",
                column: "RecordedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobShadowObservations_TenantId_TaskRequestId",
                table: "JobShadowObservations",
                columns: new[] { "TenantId", "TaskRequestId" });

            migrationBuilder.CreateIndex(
                name: "UX_JobShadowObservation_Source",
                table: "JobShadowObservations",
                columns: new[] { "SourceSystem", "SourceEventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobShadowObservations");
        }
    }
}
