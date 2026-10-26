using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetainJobDefinitionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "Jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Jobs" AS job
                SET "DeletedAtUtc" = owner."DeletedAtUtc"
                FROM "McpOperatorJobs" AS owner
                WHERE owner."JobId" = job."Id" AND owner."DeletedAtUtc" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Jobs" WHERE "DeletedAtUtc" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Retired job definitions prevent downgrade because dropping retirement state would restore active jobs.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Jobs");
        }
    }
}
