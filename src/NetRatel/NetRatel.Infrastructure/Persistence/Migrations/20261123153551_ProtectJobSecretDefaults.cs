using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProtectJobSecretDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProtectedDefaultValue",
                table: "JobParameters",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "JobParameters" WHERE "ProtectedDefaultValue" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Protected job defaults require their current schema. Restore a compatible backup to roll back.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "ProtectedDefaultValue",
                table: "JobParameters");
        }
    }
}
