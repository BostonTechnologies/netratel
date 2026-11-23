using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoreJobProviderDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProviderDefaultConnectionId",
                table: "JobParameters",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderDefaultLocator",
                table: "JobParameters",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderDefaultReferenceJson",
                table: "JobParameters",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobParameters_ProviderDefaultConnectionId",
                table: "JobParameters",
                column: "ProviderDefaultConnectionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobParameters_ProviderDefault",
                table: "JobParameters",
                sql: "(\"ProviderDefaultReferenceJson\" IS NULL AND \"ProviderDefaultConnectionId\" IS NULL AND \"ProviderDefaultLocator\" IS NULL) OR\n(\"ProviderDefaultReferenceJson\" IS NOT NULL AND \"ProviderDefaultConnectionId\" IS NOT NULL AND \"ProviderDefaultLocator\" IS NOT NULL AND\n lower(\"Type\") = 'secret' AND \"DefaultValue\" IS NULL AND \"ProtectedDefaultValue\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "JobParameters" WHERE "ProviderDefaultReferenceJson" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove the provider default schema while jobs use saved provider references';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropIndex(
                name: "IX_JobParameters_ProviderDefaultConnectionId",
                table: "JobParameters");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobParameters_ProviderDefault",
                table: "JobParameters");

            migrationBuilder.DropColumn(
                name: "ProviderDefaultConnectionId",
                table: "JobParameters");

            migrationBuilder.DropColumn(
                name: "ProviderDefaultLocator",
                table: "JobParameters");

            migrationBuilder.DropColumn(
                name: "ProviderDefaultReferenceJson",
                table: "JobParameters");
        }
    }
}
