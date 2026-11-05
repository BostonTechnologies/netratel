using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20261105190000_AddScriptExecutionReplay")]
public sealed class AddScriptExecutionReplay : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("ScriptExecutionReplays", columns: table => new
        {
            RequestId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
            TenantId = table.Column<int>(type: "integer", nullable: false),
            AgentId = table.Column<Guid>(type: "uuid", nullable: false),
            ScriptId = table.Column<long>(type: "bigint", nullable: false),
            ScriptName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
            ScriptRevision = table.Column<long>(type: "bigint", nullable: false),
            ScriptContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            ProtectedSnapshotJson = table.Column<string>(type: "text", nullable: false),
            CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_ScriptExecutionReplays", row => row.RequestId));
        migrationBuilder.CreateIndex("IX_ScriptExecutionReplays_TenantId_AgentId_CreatedAtUtc", "ScriptExecutionReplays",
            new[] { "TenantId", "AgentId", "CreatedAtUtc" });
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("ScriptExecutionReplays");
}
