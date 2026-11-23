using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantSecretProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Secrets",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Secrets",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Secrets",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<bool>(
                name: "VersionBoundProtection",
                table: "Secrets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CredentialProfiles",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    MappingRevision = table.Column<long>(type: "bigint", nullable: false),
                    MappingJson = table.Column<string>(type: "jsonb", nullable: false),
                    UsernameConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    UsernameLocator = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    PasswordConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PasswordLocator = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredentialProfiles", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_CredentialProfiles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecretProviderConnections",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ScopeRevision = table.Column<long>(type: "bigint", nullable: false),
                    AuthenticationRevision = table.Column<long>(type: "bigint", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false),
                    ProtectedAuthentication = table.Column<string>(type: "text", nullable: true),
                    LastTestJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretProviderConnections", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_SecretProviderConnections_DefaultEnabled", "NOT \"IsDefault\" OR \"Enabled\"");
                    table.ForeignKey(
                        name: "FK_SecretProviderConnections_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecretVersions",
                columns: table => new
                {
                    SecretId = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ProtectedValue = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretVersions", x => new { x.SecretId, x.Version });
                    table.ForeignKey(
                        name: "FK_SecretVersions_Secrets_SecretId",
                        column: x => x.SecretId,
                        principalTable: "Secrets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Secrets_TenantId_Id",
                table: "Secrets",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CredentialProfiles_TenantId_PasswordConnectionId_PasswordLo~",
                table: "CredentialProfiles",
                columns: new[] { "TenantId", "PasswordConnectionId", "PasswordLocator" });

            migrationBuilder.CreateIndex(
                name: "IX_CredentialProfiles_TenantId_UsernameConnectionId_UsernameLo~",
                table: "CredentialProfiles",
                columns: new[] { "TenantId", "UsernameConnectionId", "UsernameLocator" });

            migrationBuilder.CreateIndex(
                name: "IX_SecretProviderConnections_TenantId",
                table: "SecretProviderConnections",
                column: "TenantId",
                unique: true,
                filter: "\"IsDefault\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Secrets" WHERE "VersionBoundProtection")
                        OR EXISTS (SELECT 1 FROM "SecretVersions")
                        OR EXISTS (SELECT 1 FROM "SecretProviderConnections")
                        OR EXISTS (SELECT 1 FROM "CredentialProfiles") THEN
                        RAISE EXCEPTION 'Protected secret records or tenant vault configuration require the tenant-vault schema. Restore a compatible backup to roll back.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "CredentialProfiles");

            migrationBuilder.DropTable(
                name: "SecretProviderConnections");

            migrationBuilder.DropTable(
                name: "SecretVersions");

            migrationBuilder.DropIndex(
                name: "IX_Secrets_TenantId_Id",
                table: "Secrets");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Secrets");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Secrets");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Secrets");

            migrationBuilder.DropColumn(
                name: "VersionBoundProtection",
                table: "Secrets");
        }
    }
}
