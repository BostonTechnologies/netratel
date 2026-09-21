using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.SqliteMigrations.Migrations.Orchestrator
{
    /// <inheritdoc />
    public partial class AddBootstrapInitializationRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BootstrapInitializations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    BootstrapInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AdministratorUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    CompletedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BootstrapInitializations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BootstrapInitializations_BootstrapInstanceId",
                table: "BootstrapInitializations",
                column: "BootstrapInstanceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BootstrapInitializations");
        }
    }
}
