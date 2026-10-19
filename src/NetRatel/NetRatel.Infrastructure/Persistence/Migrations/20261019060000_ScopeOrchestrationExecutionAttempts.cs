using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NetRatel.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20261019060000_ScopeOrchestrationExecutionAttempts")]
public sealed class ScopeOrchestrationExecutionAttempts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_ManagedOrchestrationRequestBindings_ServicePrincipalId_Pare~", "ManagedOrchestrationRequestBindings");
        migrationBuilder.CreateIndex("IX_ManagedOrchestration_Attempt", "ManagedOrchestrationRequestBindings",
            ["ServicePrincipalId", "ParentRequestId", "RequestTaskId", "CorrelationId"], unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The original unique key cannot represent retained retries. Never delete
        // attempt history merely to make a downgrade possible.
        migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM \"ManagedOrchestrationRequestBindings\" GROUP BY \"ServicePrincipalId\", \"ParentRequestId\", \"RequestTaskId\" HAVING count(*) > 1) THEN RAISE EXCEPTION 'Retained execution attempts prevent downgrade'; END IF; END $$;");
        migrationBuilder.DropIndex("IX_ManagedOrchestration_Attempt", "ManagedOrchestrationRequestBindings");
        migrationBuilder.CreateIndex("IX_ManagedOrchestrationRequestBindings_ServicePrincipalId_Pare~", "ManagedOrchestrationRequestBindings",
            ["ServicePrincipalId", "ParentRequestId", "RequestTaskId"], unique: true);
    }
}
