using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

public partial class ProtectJobRunInputs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "InputSensitivityJson", table: "JobRuns", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ProtectedExecutionJson", table: "JobRuns", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "InputSensitivityJson", table: "Requests", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ProtectedJobInputsJson", table: "Requests", type: "text", nullable: true);
        // Encryption needs the application's durable key ring. JobRunService upgrades retained
        // rows on first read/recovery, with legacy-unknown provenance and fail-closed projections.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Removing protection would destroy recoverability or restore plaintext. Keep rollback explicit.
        migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM \"JobRuns\" WHERE \"ProtectedExecutionJson\" IS NOT NULL) THEN RAISE EXCEPTION 'Protected run inputs require application-assisted export before rollback'; END IF; END $$;");
        migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM \"Requests\" WHERE \"ProtectedJobInputsJson\" IS NOT NULL) THEN RAISE EXCEPTION 'Protected request inputs require application-assisted export before rollback'; END IF; END $$;");
        migrationBuilder.DropColumn(name: "InputSensitivityJson", table: "Requests");
        migrationBuilder.DropColumn(name: "ProtectedJobInputsJson", table: "Requests");
        migrationBuilder.DropColumn(name: "InputSensitivityJson", table: "JobRuns");
        migrationBuilder.DropColumn(name: "ProtectedExecutionJson", table: "JobRuns");
    }
}
