using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20261105180000_AddScriptExecutionDispatch")]
public sealed class AddScriptExecutionDispatch : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>("DispatchStartedAtUtc", "ExecutionAdmissions", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("DispatchAcceptedAtUtc", "ExecutionAdmissions", type: "timestamp with time zone", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("DispatchStartedAtUtc", "ExecutionAdmissions");
        migrationBuilder.DropColumn("DispatchAcceptedAtUtc", "ExecutionAdmissions");
    }
}
