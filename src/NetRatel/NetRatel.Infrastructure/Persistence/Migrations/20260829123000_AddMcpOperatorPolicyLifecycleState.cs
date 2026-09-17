using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddMcpOperatorPolicyLifecycleState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<short>(
            name: "LifecycleState",
            table: "McpOperatorPolicies",
            type: "smallint",
            nullable: false,
            defaultValue: (short)1);

        migrationBuilder.Sql("UPDATE \"McpOperatorPolicies\" SET \"LifecycleState\" = 2 WHERE \"DisabledAtUtc\" IS NOT NULL;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "LifecycleState", table: "McpOperatorPolicies");
}
