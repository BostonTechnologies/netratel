using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpOperatorPolicyChangeAudits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpOperatorPolicyChangeAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorPolicyChangeAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicyChangeAudits_AgentId_OccurredAtUtc",
                table: "McpOperatorPolicyChangeAudits",
                columns: new[] { "AgentId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicyChangeAudits_PolicyId_OccurredAtUtc",
                table: "McpOperatorPolicyChangeAudits",
                columns: new[] { "PolicyId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicyChangeAudits_TenantId_OccurredAtUtc",
                table: "McpOperatorPolicyChangeAudits",
                columns: new[] { "TenantId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpOperatorPolicyChangeAudits");
        }
    }
}
