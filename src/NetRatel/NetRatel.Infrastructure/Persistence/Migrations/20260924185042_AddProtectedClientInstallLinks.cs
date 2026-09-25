using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProtectedClientInstallLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CodeHash",
                table: "EnrollmentCodes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientInstallGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProtectedToken = table.Column<string>(type: "text", nullable: false),
                    ProtectedScript = table.Column<string>(type: "text", nullable: false),
                    RequestKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EnrollmentCodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    RuntimeId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ArtifactVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InstallAsService = table.Column<bool>(type: "boolean", nullable: false),
                    SilentInstall = table.Column<bool>(type: "boolean", nullable: false),
                    PublicWebBaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    PublicApiBaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MaxUses = table.Column<int>(type: "integer", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientInstallGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientInstallGrants_EnrollmentCodes_EnrollmentCodeId",
                        column: x => x.EnrollmentCodeId,
                        principalTable: "EnrollmentCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentCodes_CodeHash",
                table: "EnrollmentCodes",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientInstallGrants_EnrollmentCodeId",
                table: "ClientInstallGrants",
                column: "EnrollmentCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientInstallGrants_RequestKey",
                table: "ClientInstallGrants",
                column: "RequestKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientInstallGrants_TenantId_CreatedAtUtc",
                table: "ClientInstallGrants",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientInstallGrants_TokenHash",
                table: "ClientInstallGrants",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientInstallGrants");

            migrationBuilder.DropIndex(
                name: "IX_EnrollmentCodes_CodeHash",
                table: "EnrollmentCodes");

            migrationBuilder.DropColumn(
                name: "CodeHash",
                table: "EnrollmentCodes");
        }
    }
}
