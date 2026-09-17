using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds an explicit optimistic-concurrency revision for the typed operator
/// tenant lifecycle. Existing tenants begin at revision one.
/// </summary>
[DbContext(typeof(global::NetRatel.Infrastructure.Persistence.OrchestratorDbContext))]
[Migration("20260829190000_AddTenantOperatorConcurrency")]
public partial class AddTenantOperatorConcurrency : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "Version",
            table: "Tenants",
            type: "bigint",
            nullable: false,
            defaultValue: 1L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Version", table: "Tenants");
    }
}
