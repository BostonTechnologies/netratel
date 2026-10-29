using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationSetupHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrationSetupAttempts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CredentialId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProtectedDelivery = table.Column<string>(type: "text", nullable: true),
                    DeliveryExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Acknowledged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationSetupAttempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationSetupAttempts_Acknowledged_DeliveryExpiresAtUtc",
                table: "IntegrationSetupAttempts",
                columns: new[] { "Acknowledged", "DeliveryExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationSetupAttempts");
        }
    }
}
