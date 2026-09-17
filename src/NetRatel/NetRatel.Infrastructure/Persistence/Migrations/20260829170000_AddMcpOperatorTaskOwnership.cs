using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds an explicit durable ownership envelope around one-shot Production task
/// activities. Historical task rows remain untouched and are not enumerable
/// through the operator surface without one of these caller-bound records.
/// </summary>
[DbContext(typeof(global::NetRatel.Infrastructure.Persistence.OrchestratorDbContext))]
[Migration("20260829170000_AddMcpOperatorTaskOwnership")]
public partial class AddMcpOperatorTaskOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "McpOperatorTasks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TaskActivityId = table.Column<long>(type: "bigint", nullable: false),
                CommandId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                TenantId = table.Column<int>(type: "integer", nullable: false),
                AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                McpResource = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                McpInstance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                PolicyVersion = table.Column<long>(type: "bigint", nullable: false),
                TargetSetDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: false),
                IdempotencyId = table.Column<Guid>(type: "uuid", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                TaskType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ShellType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                CommandHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CommandLength = table.Column<int>(type: "integer", nullable: false),
                ScriptId = table.Column<long>(type: "bigint", nullable: true),
                ScriptVersion = table.Column<long>(type: "bigint", nullable: true),
                ScriptContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                MaximumOutputBytes = table.Column<int>(type: "integer", nullable: false),
                State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ResultSummary = table.Column<string>(type: "character varying(49152)", maxLength: 49152, nullable: true),
                CancellationRequested = table.Column<bool>(type: "boolean", nullable: false),
                CancellationRequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorTasks", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorTasks_Agents_AgentId",
                    column: x => x.AgentId,
                    principalTable: "Agents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorTasks_JobTaskActivities_TaskActivityId",
                    column: x => x.TaskActivityId,
                    principalTable: "JobTaskActivities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorTasks_McpOperatorAcceptedAudits_AcceptedAuditId",
                    column: x => x.AcceptedAuditId,
                    principalTable: "McpOperatorAcceptedAudits",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorTasks_McpOperatorIdempotencyRecords_IdempotencyId",
                    column: x => x.IdempotencyId,
                    principalTable: "McpOperatorIdempotencyRecords",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorTasks_McpOperatorPolicies_PolicyId",
                    column: x => x.PolicyId,
                    principalTable: "McpOperatorPolicies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "McpOperatorTaskAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TaskRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorTaskAudits", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorTaskAudits_McpOperatorAcceptedAudits_AcceptedAuditId",
                    column: x => x.AcceptedAuditId,
                    principalTable: "McpOperatorAcceptedAudits",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorTaskAudits_McpOperatorTasks_TaskRecordId",
                    column: x => x.TaskRecordId,
                    principalTable: "McpOperatorTasks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(name: "IX_McpOperatorTasks_AcceptedAuditId", table: "McpOperatorTasks", column: "AcceptedAuditId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorTasks_AgentId", table: "McpOperatorTasks", column: "AgentId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorTasks_CommandId", table: "McpOperatorTasks", column: "CommandId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_McpOperatorTasks_IdempotencyId", table: "McpOperatorTasks", column: "IdempotencyId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_McpOperatorTasks_PolicyId", table: "McpOperatorTasks", column: "PolicyId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorTasks_TaskActivityId", table: "McpOperatorTasks", column: "TaskActivityId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_McpOperatorTasks_TenantId_AgentId_Subject_ClientId_McpResource_McpInstance_State_UpdatedAtUtc", table: "McpOperatorTasks", columns: new[] { "TenantId", "AgentId", "Subject", "ClientId", "McpResource", "McpInstance", "State", "UpdatedAtUtc" });
        migrationBuilder.CreateIndex(name: "IX_McpOperatorTaskAudits_AcceptedAuditId", table: "McpOperatorTaskAudits", column: "AcceptedAuditId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorTaskAudits_TaskRecordId_OccurredAtUtc", table: "McpOperatorTaskAudits", columns: new[] { "TaskRecordId", "OccurredAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "McpOperatorTaskAudits");
        migrationBuilder.DropTable(name: "McpOperatorTasks");
    }
}
