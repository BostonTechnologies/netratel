using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowAdHocTaskActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "JobStepId",
                table: "JobTaskActivities",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "JobRunId",
                table: "JobTaskActivities",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "JobTaskLogs"
                WHERE "JobTaskActivityId" IN (
                    SELECT "Id"
                    FROM "JobTaskActivities"
                    WHERE "JobRunId" IS NULL OR "JobStepId" IS NULL
                );
                """);

            migrationBuilder.Sql("""
                DELETE FROM "JobTaskActivities"
                WHERE "JobRunId" IS NULL OR "JobStepId" IS NULL;
                """);

            migrationBuilder.AlterColumn<long>(
                name: "JobStepId",
                table: "JobTaskActivities",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "JobRunId",
                table: "JobTaskActivities",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}
