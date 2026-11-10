using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpReviewedJobAdmissionsAndQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JobRequestHash",
                table: "McpOperatorConfirmationPlans",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "McpJobTargetReservations",
                columns: table => new
                {
                    IdempotencyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetCount = table.Column<int>(type: "integer", nullable: false),
                    TargetPlanDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpJobTargetReservations", x => new { x.IdempotencyId, x.PolicyId });
                    table.CheckConstraint("CK_McpJobTargetReservations_Count", "\"TargetCount\" > 0 AND \"TargetCount\" <= 10000");
                    table.ForeignKey(
                        name: "FK_McpJobTargetReservations_McpOperatorIdempotencyRecords_Idem~",
                        column: x => x.IdempotencyId,
                        principalTable: "McpOperatorIdempotencyRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpJobTargetReservations_McpOperatorPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "McpOperatorPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpReviewedJobAdmissions",
                columns: table => new
                {
                    IdempotencyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    JobId = table.Column<long>(type: "bigint", nullable: false),
                    ProtectedSubmissionJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpReviewedJobAdmissions", x => x.IdempotencyId);
                    table.ForeignKey(
                        name: "FK_McpReviewedJobAdmissions_McpOperatorAcceptedAudits_Accepted~",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpReviewedJobAdmissions_McpOperatorIdempotencyRecords_Idem~",
                        column: x => x.IdempotencyId,
                        principalTable: "McpOperatorIdempotencyRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpJobTargetReservations_PolicyId",
                table: "McpJobTargetReservations",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_McpReviewedJobAdmissions_AcceptedAuditId",
                table: "McpReviewedJobAdmissions",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpReviewedJobAdmissions_TenantId_JobId",
                table: "McpReviewedJobAdmissions",
                columns: new[] { "TenantId", "JobId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpJobTargetReservations");

            migrationBuilder.DropTable(
                name: "McpReviewedJobAdmissions");

            migrationBuilder.DropColumn(
                name: "JobRequestHash",
                table: "McpOperatorConfirmationPlans");
        }
    }
}
