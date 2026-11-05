using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20261105170000_AddExecutionAdmissions")]
public sealed class AddExecutionAdmissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ExecutionAdmissions",
            columns: table => new
            {
                ActionKey = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<int>(type: "integer", nullable: false),
                PrincipalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                IntegrationCredentialId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                FlowVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                FlowRunId = table.Column<Guid>(type: "uuid", nullable: false),
                NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                NodeAttempt = table.Column<int>(type: "integer", nullable: false),
                CausationJson = table.Column<string>(type: "text", nullable: true),
                AuthorityDeniedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Kind = table.Column<int>(type: "integer", nullable: false),
                ExecutionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ProtectedPayloadJson = table.Column<string>(type: "text", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ExecutionAdmissions", record => record.ActionKey));
        migrationBuilder.CreateIndex("IX_ExecutionAdmissions_Kind_ExecutionId", "ExecutionAdmissions", new[] { "Kind", "ExecutionId" }, unique: true);
        migrationBuilder.CreateIndex("IX_ExecutionAdmissions_TenantId_FlowRunId_NodeId_NodeAttempt", "ExecutionAdmissions",
            new[] { "TenantId", "FlowRunId", "NodeId", "NodeAttempt" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable("ExecutionAdmissions");
}
