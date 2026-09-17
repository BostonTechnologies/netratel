using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrimaryClientAgentBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrimaryClientAgentBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    EnrollmentCodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    PrimaryClientIdentity = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    BoundAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BoundBy = table.Column<string>(type: "text", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "text", nullable: true),
                    BindingSource = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrimaryClientAgentBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrimaryClientAgentBindings_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PrimaryClientAgentBindings_EnrollmentCodes_EnrollmentCodeId",
                        column: x => x.EnrollmentCodeId,
                        principalTable: "EnrollmentCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryClientAgentBindings_AgentId",
                table: "PrimaryClientAgentBindings",
                column: "AgentId",
                unique: true,
                filter: "\"AgentId\" IS NOT NULL AND \"Status\" <> 3");

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryClientAgentBindings_EnrollmentCodeId",
                table: "PrimaryClientAgentBindings",
                column: "EnrollmentCodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryClientAgentBindings_TenantId_PrimaryClientIdentity",
                table: "PrimaryClientAgentBindings",
                columns: new[] { "TenantId", "PrimaryClientIdentity" },
                unique: true,
                filter: "\"Status\" <> 3");

            migrationBuilder.CreateIndex(
                name: "IX_PrimaryClientAgentBindings_TenantId_Status_CreatedAtUtc",
                table: "PrimaryClientAgentBindings",
                columns: new[] { "TenantId", "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrimaryClientAgentBindings");
        }
    }
}
