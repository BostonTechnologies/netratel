using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

partial class AddDevelopmentMcpMarkerJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DevelopmentMcpMarkerJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<long>(type: "bigint", nullable: false),
                ScriptId = table.Column<long>(type: "bigint", nullable: false),
                TenantId = table.Column<int>(type: "integer", nullable: false),
                AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                TargetGrantId = table.Column<Guid>(type: "uuid", nullable: false),
                Marker = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DevelopmentMcpMarkerJobs", x => x.Id);
                table.ForeignKey(
                    name: "FK_DevelopmentMcpMarkerJobs_DevelopmentOperatorTargetGrants_TargetGrantId",
                    column: x => x.TargetGrantId,
                    principalTable: "DevelopmentOperatorTargetGrants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DevelopmentMcpMarkerJobs_JobId",
            table: "DevelopmentMcpMarkerJobs",
            column: "JobId",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_DevelopmentMcpMarkerJobs_ScriptId",
            table: "DevelopmentMcpMarkerJobs",
            column: "ScriptId",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_DevelopmentMcpMarkerJobs_TargetGrantId_CreatedAtUtc",
            table: "DevelopmentMcpMarkerJobs",
            columns: new[] { "TargetGrantId", "CreatedAtUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_DevelopmentMcpMarkerJobs_TenantId_AgentId_DeletedAtUtc_CreatedAtUtc",
            table: "DevelopmentMcpMarkerJobs",
            columns: new[] { "TenantId", "AgentId", "DeletedAtUtc", "CreatedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "DevelopmentMcpMarkerJobs");
}
