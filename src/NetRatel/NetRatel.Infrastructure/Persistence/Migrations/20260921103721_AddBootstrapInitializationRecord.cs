using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
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
                    Id = table.Column<int>(type: "integer", nullable: false),
                    BootstrapInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AdministratorUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
