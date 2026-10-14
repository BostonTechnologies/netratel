using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationCredentialPairing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(name: "PairingAcknowledgedAtUtc", table: "IntegrationCredentials",
                type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<string>(name: "PairingRequestId", table: "IntegrationCredentials",
                type: "character varying(32)", maxLength: 32, nullable: true);
            migrationBuilder.CreateTable(
                name: "IntegrationPairingRequests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserCodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeviceCapabilityHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PublicOrigin = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    Resource = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    RequestedPermissionsJson = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextPollAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ApprovedGrantsJson = table.Column<string>(type: "text", nullable: true),
                    ApprovedInstancePermissionsJson = table.Column<string>(type: "text", nullable: true),
                    CredentialExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CredentialId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ProtectedDelivery = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    DeliveryExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationPairingRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationPairingRequests_DeviceCapabilityHash",
                table: "IntegrationPairingRequests",
                column: "DeviceCapabilityHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationPairingRequests_State_DeliveryExpiresAtUtc",
                table: "IntegrationPairingRequests",
                columns: new[] { "State", "DeliveryExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationPairingRequests_State_ExpiresAtUtc",
                table: "IntegrationPairingRequests",
                columns: new[] { "State", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationPairingRequests_UserCodeHash",
                table: "IntegrationPairingRequests",
                column: "UserCodeHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "PairingAcknowledgedAtUtc", table: "IntegrationCredentials");
            migrationBuilder.DropColumn(name: "PairingRequestId", table: "IntegrationCredentials");
            migrationBuilder.DropTable(
                name: "IntegrationPairingRequests");
        }
    }
}
