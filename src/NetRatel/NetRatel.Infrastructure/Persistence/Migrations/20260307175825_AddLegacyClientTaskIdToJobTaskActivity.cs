using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegacyClientTaskIdToJobTaskActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LegacyClientTaskId",
                table: "JobTaskActivities",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskActivities_LegacyClientTaskId",
                table: "JobTaskActivities",
                column: "LegacyClientTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobTaskActivities_LegacyClientTaskId",
                table: "JobTaskActivities");

            migrationBuilder.DropColumn(
                name: "LegacyClientTaskId",
                table: "JobTaskActivities");
        }
    }
}
