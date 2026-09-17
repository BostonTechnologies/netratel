using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds caller-owned, policy-frozen V2 envelopes around request-domain rows.
/// Existing ExternalService requests are intentionally left unowned and cannot be
/// read through the Production operator routes.
/// </summary>
[DbContext(typeof(global::NetRatel.Infrastructure.Persistence.OrchestratorDbContext))]
[Migration("20260829180000_AddMcpOperatorRequestOwnership")]
public partial class AddMcpOperatorRequestOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "McpOperatorRequests",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RequestId = table.Column<int>(type: "integer", nullable: false),
                JobId = table.Column<long>(type: "bigint", nullable: false),
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
                State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Summary = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                ResultSummary = table.Column<string>(type: "character varying(49152)", maxLength: 49152, nullable: true),
                ClaimReferenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorRequests", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorRequests_Agents_AgentId",
                    column: x => x.AgentId,
                    principalTable: "Agents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorRequests_Jobs_JobId",
                    column: x => x.JobId,
                    principalTable: "Jobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorRequests_McpOperatorAcceptedAudits_AcceptedAuditId",
                    column: x => x.AcceptedAuditId,
                    principalTable: "McpOperatorAcceptedAudits",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorRequests_McpOperatorIdempotencyRecords_IdempotencyId",
                    column: x => x.IdempotencyId,
                    principalTable: "McpOperatorIdempotencyRecords",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorRequests_McpOperatorPolicies_PolicyId",
                    column: x => x.PolicyId,
                    principalTable: "McpOperatorPolicies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorRequests_Requests_RequestId",
                    column: x => x.RequestId,
                    principalTable: "Requests",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "McpOperatorRequestAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RequestRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorRequestAudits", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorRequestAudits_McpOperatorAcceptedAudits_AcceptedAuditId",
                    column: x => x.AcceptedAuditId,
                    principalTable: "McpOperatorAcceptedAudits",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorRequestAudits_McpOperatorRequests_RequestRecordId",
                    column: x => x.RequestRecordId,
                    principalTable: "McpOperatorRequests",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(name: "IX_McpOperatorRequests_AcceptedAuditId", table: "McpOperatorRequests", column: "AcceptedAuditId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorRequests_AgentId", table: "McpOperatorRequests", column: "AgentId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorRequests_IdempotencyId", table: "McpOperatorRequests", column: "IdempotencyId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_McpOperatorRequests_JobId_UpdatedAtUtc", table: "McpOperatorRequests", columns: new[] { "JobId", "UpdatedAtUtc" });
        migrationBuilder.CreateIndex(name: "IX_McpOperatorRequests_PolicyId", table: "McpOperatorRequests", column: "PolicyId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorRequests_RequestId", table: "McpOperatorRequests", column: "RequestId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_McpOperatorRequests_TenantId_AgentId_Subject_ClientId_McpResource_McpInstance_State_UpdatedAtUtc", table: "McpOperatorRequests", columns: new[] { "TenantId", "AgentId", "Subject", "ClientId", "McpResource", "McpInstance", "State", "UpdatedAtUtc" });
        migrationBuilder.CreateIndex(name: "IX_McpOperatorRequestAudits_AcceptedAuditId", table: "McpOperatorRequestAudits", column: "AcceptedAuditId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorRequestAudits_RequestRecordId_OccurredAtUtc", table: "McpOperatorRequestAudits", columns: new[] { "RequestRecordId", "OccurredAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "McpOperatorRequestAudits");
        migrationBuilder.DropTable(name: "McpOperatorRequests");
    }
}
