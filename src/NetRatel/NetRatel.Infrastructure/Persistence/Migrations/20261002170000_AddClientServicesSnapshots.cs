using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20261002170000_AddClientServicesSnapshots")]
public sealed class AddClientServicesSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ClientConnectionEpochs",
            columns: table => new
            {
                TenantId = table.Column<int>(type: "integer", nullable: false),
                AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                LastIssuedEpoch = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ClientConnectionEpochs", record => new { record.TenantId, record.AgentId }));
        migrationBuilder.CreateTable(
            name: "ClientServicesSnapshots",
            columns: table => new
            {
                TenantId = table.Column<int>(type: "integer", nullable: false),
                AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                ConnectionEpoch = table.Column<long>(type: "bigint", nullable: false),
                LastAcceptedSequence = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                StateJson = table.Column<string>(type: "jsonb", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ClientServicesSnapshots", record => new { record.TenantId, record.AgentId }));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ClientServicesSnapshots");
        migrationBuilder.DropTable("ClientConnectionEpochs");
    }
}
