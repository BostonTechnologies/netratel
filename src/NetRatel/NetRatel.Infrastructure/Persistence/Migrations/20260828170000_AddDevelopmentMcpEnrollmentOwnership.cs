using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetRatel.Infrastructure.Persistence;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20260828170000_AddDevelopmentMcpEnrollmentOwnership")]
public sealed class AddDevelopmentMcpEnrollmentOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "DevelopmentMcpTargetAgentId",
            table: "EnrollmentCodes",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DevelopmentMcpMarker",
            table: "EnrollmentCodes",
            type: "text",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_EnrollmentCodes_TenantId_DevelopmentMcpTargetAgentId_DevelopmentMcpMarker",
            table: "EnrollmentCodes",
            columns: new[] { "TenantId", "DevelopmentMcpTargetAgentId", "DevelopmentMcpMarker" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_EnrollmentCodes_TenantId_DevelopmentMcpTargetAgentId_DevelopmentMcpMarker",
            table: "EnrollmentCodes");

        migrationBuilder.DropColumn(
            name: "DevelopmentMcpTargetAgentId",
            table: "EnrollmentCodes");

        migrationBuilder.DropColumn(
            name: "DevelopmentMcpMarker",
            table: "EnrollmentCodes");
    }
}
