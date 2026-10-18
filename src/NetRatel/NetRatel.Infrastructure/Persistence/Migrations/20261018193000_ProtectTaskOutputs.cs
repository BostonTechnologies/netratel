using Microsoft.EntityFrameworkCore.Migrations;

namespace NetRatel.Infrastructure.Persistence.Migrations;

public partial class ProtectTaskOutputs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "OutputProtectionJson", table: "JobTaskActivities", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ProtectedLegacyOutputJson", table: "JobTaskActivities", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "OutputProtectionBinding", table: "JobTaskLogs", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "OutputProtectionJson", table: "McpOperatorCommands", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "OutputProtectionBinding", table: "McpOperatorCommands", type: "text", nullable: true);
        // Historical output remains classified as unknown. Application reads mask it
        // immediately and archive original bytes using the durable Data Protection key ring.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM \"JobTaskActivities\" WHERE \"ProtectedLegacyOutputJson\" IS NOT NULL) THEN RAISE EXCEPTION 'Protected task history requires application-assisted export before rollback'; END IF; END $$;");
        migrationBuilder.DropColumn(name: "OutputProtectionBinding", table: "JobTaskLogs");
        migrationBuilder.DropColumn(name: "OutputProtectionBinding", table: "McpOperatorCommands");
        migrationBuilder.DropColumn(name: "OutputProtectionJson", table: "McpOperatorCommands");
        migrationBuilder.DropColumn(name: "ProtectedLegacyOutputJson", table: "JobTaskActivities");
        migrationBuilder.DropColumn(name: "OutputProtectionJson", table: "JobTaskActivities");
    }
}
