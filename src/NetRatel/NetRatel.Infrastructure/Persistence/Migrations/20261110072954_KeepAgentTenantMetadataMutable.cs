using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KeepAgentTenantMetadataMutable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExecutionGroupMembers_Agents_TenantId_AgentId",
                table: "ExecutionGroupMembers");

            migrationBuilder.DropForeignKey(
                name: "FK_JobRunNodes_Agents_TenantId_AgentId",
                table: "JobRunNodes");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Agents_TenantId_Id",
                table: "Agents");

            migrationBuilder.CreateIndex(
                name: "IX_JobRunNodes_AgentId",
                table: "JobRunNodes",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionGroupMembers_AgentId",
                table: "ExecutionGroupMembers",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_TenantId_Id",
                table: "Agents",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ExecutionGroupMembers_Agents_AgentId",
                table: "ExecutionGroupMembers",
                column: "AgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_JobRunNodes_Agents_AgentId",
                table: "JobRunNodes",
                column: "AgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            // Keep tenant-bound membership/history as PostgreSQL constraints. EF's
            // identity is Agent.Id only, so an unreferenced Agent can retain its
            // existing tenant-change behavior without weakening these references.
            migrationBuilder.Sql("""
                ALTER TABLE "ExecutionGroupMembers" ADD CONSTRAINT "FK_ExecutionGroupMembers_Agents_TenantId_AgentId"
                    FOREIGN KEY ("TenantId", "AgentId") REFERENCES "Agents" ("TenantId", "Id") ON DELETE RESTRICT;
                ALTER TABLE "JobRunNodes" ADD CONSTRAINT "FK_JobRunNodes_Agents_TenantId_AgentId"
                    FOREIGN KEY ("TenantId", "AgentId") REFERENCES "Agents" ("TenantId", "Id") ON DELETE RESTRICT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // These additional database-only constraints reference the mutable
            // unique index and must be removed before restoring the old EF key.
            migrationBuilder.Sql("""
                ALTER TABLE "ExecutionGroupMembers" DROP CONSTRAINT "FK_ExecutionGroupMembers_Agents_TenantId_AgentId";
                ALTER TABLE "JobRunNodes" DROP CONSTRAINT "FK_JobRunNodes_Agents_TenantId_AgentId";
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_ExecutionGroupMembers_Agents_AgentId",
                table: "ExecutionGroupMembers");

            migrationBuilder.DropForeignKey(
                name: "FK_JobRunNodes_Agents_AgentId",
                table: "JobRunNodes");

            migrationBuilder.DropIndex(
                name: "IX_JobRunNodes_AgentId",
                table: "JobRunNodes");

            migrationBuilder.DropIndex(
                name: "IX_ExecutionGroupMembers_AgentId",
                table: "ExecutionGroupMembers");

            migrationBuilder.DropIndex(
                name: "IX_Agents_TenantId_Id",
                table: "Agents");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Agents_TenantId_Id",
                table: "Agents",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_ExecutionGroupMembers_Agents_TenantId_AgentId",
                table: "ExecutionGroupMembers",
                columns: new[] { "TenantId", "AgentId" },
                principalTable: "Agents",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_JobRunNodes_Agents_TenantId_AgentId",
                table: "JobRunNodes",
                columns: new[] { "TenantId", "AgentId" },
                principalTable: "Agents",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
