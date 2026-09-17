using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowNullJobStepRunJobStepId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobStepRuns_JobSteps_JobStepId",
                table: "JobStepRuns");

            migrationBuilder.AlterColumn<long>(
                name: "JobStepId",
                table: "JobStepRuns",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddForeignKey(
                name: "FK_JobStepRuns_JobSteps_JobStepId",
                table: "JobStepRuns",
                column: "JobStepId",
                principalTable: "JobSteps",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobStepRuns_JobSteps_JobStepId",
                table: "JobStepRuns");

            migrationBuilder.AlterColumn<long>(
                name: "JobStepId",
                table: "JobStepRuns",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_JobStepRuns_JobSteps_JobStepId",
                table: "JobStepRuns",
                column: "JobStepId",
                principalTable: "JobSteps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
