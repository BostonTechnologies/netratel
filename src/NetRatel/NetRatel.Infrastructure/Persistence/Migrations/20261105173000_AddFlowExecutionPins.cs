using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20261105173000_AddFlowExecutionPins")]
public sealed class AddFlowExecutionPins : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("ProtectedExecutionPinsJson", "FlowVersions", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("ExecutionAuthorityJson", "FlowVersions", type: "text", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("EnabledSinceUtc", "FlowDefinitions", type: "timestamp with time zone", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("ProtectedExecutionPinsJson", "FlowVersions");
        migrationBuilder.DropColumn("ExecutionAuthorityJson", "FlowVersions");
        migrationBuilder.DropColumn("EnabledSinceUtc", "FlowDefinitions");
    }
}
