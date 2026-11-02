using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetireTenantAndAbandonedUpdateAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.DropIndex(name: "IX_Tenants_Name", table: "Tenants");
            migrationBuilder.CreateIndex(name: "IX_Tenants_Name", table: "Tenants", column: "Name", unique: true, filter: "\"DeletedAtUtc\" IS NULL");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetiredAtUtc",
                table: "ClientUpdateAttempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetiredBy",
                table: "ClientUpdateAttempts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetirementReason",
                table: "ClientUpdateAttempts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Tenants_Name", table: "Tenants");
            migrationBuilder.CreateIndex(name: "IX_Tenants_Name", table: "Tenants", column: "Name", unique: true);
            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "RetiredAtUtc",
                table: "ClientUpdateAttempts");

            migrationBuilder.DropColumn(
                name: "RetiredBy",
                table: "ClientUpdateAttempts");

            migrationBuilder.DropColumn(
                name: "RetirementReason",
                table: "ClientUpdateAttempts");
        }
    }
}
