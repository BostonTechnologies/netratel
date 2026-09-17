using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobTaskLogsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobTaskLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequestId = table.Column<string>(type: "text", nullable: false),
                    JobTaskActivityId = table.Column<long>(type: "bigint", nullable: true),
                    ClientIdentity = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    Stream = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTaskLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobTaskLogs_JobTaskActivities_JobTaskActivityId",
                        column: x => x.JobTaskActivityId,
                        principalTable: "JobTaskActivities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskLogs_JobTaskActivityId",
                table: "JobTaskLogs",
                column: "JobTaskActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskLogs_RequestId",
                table: "JobTaskLogs",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskLogs_RequestId_Sequence_Stream",
                table: "JobTaskLogs",
                columns: new[] { "RequestId", "Sequence", "Stream" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobTaskLogs_TimestampUtc",
                table: "JobTaskLogs",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobTaskLogs");
        }
    }
}
