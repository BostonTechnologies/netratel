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

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Protected secret records require a compatible NetRatel API.");
}
