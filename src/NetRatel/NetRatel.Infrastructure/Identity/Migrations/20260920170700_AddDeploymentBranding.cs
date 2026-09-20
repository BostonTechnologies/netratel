using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeploymentBrandingAssets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentBrandingAssets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentBrandingOverrides",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ApplicationName = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    OrganizationName = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    Tagline = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    LogoLightAssetId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LogoDarkAssetId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CompactLogoAssetId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FaviconAssetId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SupportUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SiteUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByPrincipalId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentBrandingOverrides", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentBrandingAssets_Sha256",
                table: "DeploymentBrandingAssets",
                column: "Sha256",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentBrandingAssets");

            migrationBuilder.DropTable(
                name: "DeploymentBrandingOverrides");
        }
    }
}
