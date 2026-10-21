using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddValidatedExternalAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalAuthorityClaimsJson",
                table: "ApplicationPrincipals",
                type: "character varying(131072)",
                maxLength: 131072,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExternalAuthorityExpiresAtUtc",
                table: "ApplicationPrincipals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExternalAuthorityIssuedAtUtc",
                table: "ApplicationPrincipals",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalAuthorityClaimsJson",
                table: "ApplicationPrincipals");

            migrationBuilder.DropColumn(
                name: "ExternalAuthorityExpiresAtUtc",
                table: "ApplicationPrincipals");

            migrationBuilder.DropColumn(
                name: "ExternalAuthorityIssuedAtUtc",
                table: "ApplicationPrincipals");
        }
    }
}
