using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    public partial class AddAgentLifecycleManagement : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "Agents",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "DisabledReason",
                table: "Agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastTokenIssuedAtUtc",
                table: "Agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAtUtc",
                table: "Agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("UPDATE \"Agents\" SET \"IsEnabled\" = CASE WHEN \"Status\" = 0 THEN TRUE ELSE FALSE END;");

            migrationBuilder.CreateTable(
                name: "AgentTokenEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Ip = table.Column<string>(type: "text", nullable: true),
                    UserAgent = table.Column<string>(type: "text", nullable: true),
                    DetailsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTokenEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_TenantId_CreatedAtUtc",
                table: "Agents",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_TenantId_IsEnabled",
                table: "Agents",
                columns: new[] { "TenantId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTokenEvents_TenantId_AgentId_CreatedAtUtc",
                table: "AgentTokenEvents",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AgentTokenEvents");

            migrationBuilder.DropIndex(name: "IX_Agents_TenantId_CreatedAtUtc", table: "Agents");
            migrationBuilder.DropIndex(name: "IX_Agents_TenantId_IsEnabled", table: "Agents");

            migrationBuilder.DropColumn(name: "IsEnabled", table: "Agents");
            migrationBuilder.DropColumn(name: "DisabledReason", table: "Agents");
            migrationBuilder.DropColumn(name: "LastTokenIssuedAtUtc", table: "Agents");
            migrationBuilder.DropColumn(name: "RevokedAtUtc", table: "Agents");
        }
    }
}
