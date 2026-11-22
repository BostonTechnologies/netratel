using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NetRatel.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20261122134000_ProtectSecretValues")]
public sealed class ProtectSecretValues : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>(
            name: "ValueProtected",
            table: "Secrets",
            type: "boolean",
            nullable: false,
            defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            LOCK TABLE "Secrets" IN ACCESS EXCLUSIVE MODE;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM "Secrets" WHERE "ValueProtected") THEN
                    RAISE EXCEPTION 'Protected secret records require a compatible NetRatel API.';
                END IF;
            END $$;
            """);
        migrationBuilder.DropColumn(name: "ValueProtected", table: "Secrets");
    }
}
