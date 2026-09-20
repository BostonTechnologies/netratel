using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrationCredentials",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicId = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    TokenPrefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    Resource = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastUsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByPrincipalId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationCredentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationCredentialGrants",
                columns: table => new
                {
                    CredentialId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Permission = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationCredentialGrants", x => new { x.CredentialId, x.TenantId, x.Permission });
                    table.ForeignKey(
                        name: "FK_IntegrationCredentialGrants_IntegrationCredentials_Credenti~",
                        column: x => x.CredentialId,
                        principalTable: "IntegrationCredentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCredentials_OwnerPrincipalId_CreatedAtUtc",
                table: "IntegrationCredentials",
                columns: new[] { "OwnerPrincipalId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCredentials_PublicId",
                table: "IntegrationCredentials",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCredentials_Purpose_ExpiresAtUtc_RevokedAtUtc",
                table: "IntegrationCredentials",
                columns: new[] { "Purpose", "ExpiresAtUtc", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCredentials_SecretHash",
                table: "IntegrationCredentials",
                column: "SecretHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationCredentialGrants");

            migrationBuilder.DropTable(
                name: "IntegrationCredentials");
        }
    }
}
