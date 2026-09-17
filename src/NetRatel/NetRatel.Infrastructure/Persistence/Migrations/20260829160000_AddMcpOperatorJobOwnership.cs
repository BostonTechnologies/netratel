using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds the V2 ownership envelope around the existing job and job-run
/// projections. Historical generic jobs remain untouched and cannot appear
/// through the operator surface without one of these explicit records.
/// </summary>
[DbContext(typeof(global::NetRatel.Infrastructure.Persistence.OrchestratorDbContext))]
[Migration("20260829160000_AddMcpOperatorJobOwnership")]
public partial class AddMcpOperatorJobOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "McpOperatorJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
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
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorJobs", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorJobs_Jobs_JobId",
                    column: x => x.JobId,
                    principalTable: "Jobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorJobs_McpOperatorPolicies_PolicyId",
                    column: x => x.PolicyId,
                    principalTable: "McpOperatorPolicies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "McpOperatorJobAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<long>(type: "bigint", nullable: false),
                JobVersion = table.Column<long>(type: "bigint", nullable: false),
                Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorJobAudits", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorJobAudits_McpOperatorAcceptedAudits_AcceptedAuditId",
                    column: x => x.AcceptedAuditId,
                    principalTable: "McpOperatorAcceptedAudits",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorJobAudits_McpOperatorJobs_JobRecordId",
                    column: x => x.JobRecordId,
                    principalTable: "McpOperatorJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "McpOperatorJobRuns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobRunId = table.Column<long>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                JobRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<long>(type: "bigint", nullable: false),
                TenantId = table.Column<int>(type: "integer", nullable: false),
                AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: false),
                IdempotencyId = table.Column<Guid>(type: "uuid", nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                TargetSetDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CancellationRequested = table.Column<bool>(type: "boolean", nullable: false),
                CancellationRequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorJobRuns", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorJobRuns_McpOperatorAcceptedAudits_AcceptedAuditId",
                    column: x => x.AcceptedAuditId,
                    principalTable: "McpOperatorAcceptedAudits",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorJobRuns_McpOperatorIdempotencyRecords_IdempotencyId",
                    column: x => x.IdempotencyId,
                    principalTable: "McpOperatorIdempotencyRecords",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorJobRuns_McpOperatorJobs_JobRecordId",
                    column: x => x.JobRecordId,
                    principalTable: "McpOperatorJobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "McpOperatorJobRunAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobRunRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorJobRunAudits", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorJobRunAudits_McpOperatorAcceptedAudits_AcceptedAuditId",
                    column: x => x.AcceptedAuditId,
                    principalTable: "McpOperatorAcceptedAudits",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorJobRunAudits_McpOperatorJobRuns_JobRunRecordId",
                    column: x => x.JobRunRecordId,
                    principalTable: "McpOperatorJobRuns",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobs_JobId", table: "McpOperatorJobs", column: "JobId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobs_PolicyId", table: "McpOperatorJobs", column: "PolicyId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobs_TenantId_AgentId_Subject_ClientId_McpResource_McpInstance_DeletedAtUtc_UpdatedAtUtc", table: "McpOperatorJobs", columns: new[] { "TenantId", "AgentId", "Subject", "ClientId", "McpResource", "McpInstance", "DeletedAtUtc", "UpdatedAtUtc" });
        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobAudits_AcceptedAuditId", table: "McpOperatorJobAudits", column: "AcceptedAuditId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobAudits_JobId_OccurredAtUtc", table: "McpOperatorJobAudits", columns: new[] { "JobId", "OccurredAtUtc" });
        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobAudits_JobRecordId_JobVersion", table: "McpOperatorJobAudits", columns: new[] { "JobRecordId", "JobVersion" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobRuns_AcceptedAuditId", table: "McpOperatorJobRuns", column: "AcceptedAuditId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobRuns_IdempotencyId", table: "McpOperatorJobRuns", column: "IdempotencyId", unique: true, filter: "\"IdempotencyId\" IS NOT NULL");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobRuns_JobRecordId", table: "McpOperatorJobRuns", column: "JobRecordId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobRuns_JobRunId", table: "McpOperatorJobRuns", column: "JobRunId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobRuns_TenantId_AgentId_DeletedAtUtc_CreatedAtUtc", table: "McpOperatorJobRuns", columns: new[] { "TenantId", "AgentId", "DeletedAtUtc", "CreatedAtUtc" });
        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobRunAudits_AcceptedAuditId", table: "McpOperatorJobRunAudits", column: "AcceptedAuditId");
        migrationBuilder.CreateIndex(name: "IX_McpOperatorJobRunAudits_JobRunRecordId_OccurredAtUtc", table: "McpOperatorJobRunAudits", columns: new[] { "JobRunRecordId", "OccurredAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "McpOperatorJobAudits");
        migrationBuilder.DropTable(name: "McpOperatorJobRunAudits");
        migrationBuilder.DropTable(name: "McpOperatorJobRuns");
        migrationBuilder.DropTable(name: "McpOperatorJobs");
    }
}
