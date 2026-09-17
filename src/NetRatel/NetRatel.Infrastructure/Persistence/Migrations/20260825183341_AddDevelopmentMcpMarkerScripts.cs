using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDevelopmentMcpMarkerScripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DevelopmentMcpScripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScriptId = table.Column<long>(type: "bigint", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetGrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Marker = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Shell = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevelopmentMcpScripts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevelopmentMcpScripts_DevelopmentOperatorTargetGrants_Targe~",
                        column: x => x.TargetGrantId,
                        principalTable: "DevelopmentOperatorTargetGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpScripts_ScriptId",
                table: "DevelopmentMcpScripts",
                column: "ScriptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpScripts_TargetGrantId_CreatedAtUtc",
                table: "DevelopmentMcpScripts",
                columns: new[] { "TargetGrantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpScripts_TenantId_AgentId_DeletedAtUtc_Created~",
                table: "DevelopmentMcpScripts",
                columns: new[] { "TenantId", "AgentId", "DeletedAtUtc", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DevelopmentMcpScripts");
        }
    }
}
