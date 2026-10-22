using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NetRatel.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20261021214259_StopTerminalCallbackRejectionRetries")]
public sealed class StopTerminalCallbackRejectionRetries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "RejectedAtUtc",
            table: "OrchestrationCallbackDeliveries",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("RejectedAtUtc", "OrchestrationCallbackDeliveries");
    }
}
