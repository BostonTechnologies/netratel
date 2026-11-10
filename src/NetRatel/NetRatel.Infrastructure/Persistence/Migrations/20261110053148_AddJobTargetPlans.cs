using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobTargetPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionPolicyJson",
                table: "Jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetJson",
                table: "Jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefinitionDigest",
                table: "JobRuns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAggregate",
                table: "JobRuns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ParentRunId",
                table: "JobRuns",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetPlanDigest",
                table: "JobRuns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetPlanJson",
                table: "JobRuns",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JobAggregateControls",
                columns: table => new
                {
                    ParentRunId = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    DispatchClosed = table.Column<bool>(type: "boolean", nullable: false),
                    AdmissionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PrincipalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IntegrationCredentialId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SourceKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    GateReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CancellationRequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActionDeadlineAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalReadyAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalResultHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobAggregateControls", x => x.ParentRunId);
                    table.ForeignKey(
                        name: "FK_JobAggregateControls_JobRuns_ParentRunId",
                        column: x => x.ParentRunId,
                        principalTable: "JobRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobRunNodes",
                columns: table => new
                {
                    ParentRunId = table.Column<long>(type: "bigint", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    NativeRunId = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    TargetJson = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ClaimedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRunNodes", x => new { x.ParentRunId, x.AgentId });
                    table.CheckConstraint("CK_JobRunNodes_Identity", "\"TenantId\" > 0 AND \"AgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"Ordinal\" > 0");
                    table.CheckConstraint("CK_JobRunNodes_State", "\"State\" BETWEEN 0 AND 8");
                    table.ForeignKey(
                        name: "FK_JobRunNodes_JobRuns_NativeRunId",
                        column: x => x.NativeRunId,
                        principalTable: "JobRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobRunNodes_JobRuns_ParentRunId",
                        column: x => x.ParentRunId,
                        principalTable: "JobRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_Id_TenantId",
                table: "JobRuns",
                columns: new[] { "Id", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_ParentRunId",
                table: "JobRuns",
                column: "ParentRunId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRuns_AggregateTarget",
                table: "JobRuns",
                sql: "NOT \"IsAggregate\" OR (\"AgentId\" IS NULL AND \"ParentRunId\" IS NULL AND \"TenantId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_JobAggregateControls_AdmissionKey",
                table: "JobAggregateControls",
                column: "AdmissionKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRunNodes_NativeRunId",
                table: "JobRunNodes",
                column: "NativeRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRunNodes_ParentRunId_Ordinal",
                table: "JobRunNodes",
                columns: new[] { "ParentRunId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRunNodes_ParentRunId_State",
                table: "JobRunNodes",
                columns: new[] { "ParentRunId", "State" });

            migrationBuilder.AddForeignKey(
                name: "FK_JobRuns_JobRuns_ParentRunId",
                table: "JobRuns",
                column: "ParentRunId",
                principalTable: "JobRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // EF alternate keys make their properties required. These PostgreSQL
            // constraints enforce new node tenant/identity integrity while retaining
            // nullable tenant data in historical singleton JobRuns.
            migrationBuilder.Sql("""
                ALTER TABLE "JobRunNodes" ADD CONSTRAINT "FK_JobRunNodes_ParentTenant"
                    FOREIGN KEY ("ParentRunId", "TenantId") REFERENCES "JobRuns" ("Id", "TenantId") ON DELETE CASCADE;
                CREATE UNIQUE INDEX "IX_JobRuns_NativeNodeBinding" ON "JobRuns" ("Id", "TenantId", "AgentId", "ParentRunId");
                ALTER TABLE "JobRunNodes" ADD CONSTRAINT "FK_JobRunNodes_NativeBinding"
                    FOREIGN KEY ("NativeRunId", "TenantId", "AgentId", "ParentRunId")
                    REFERENCES "JobRuns" ("Id", "TenantId", "AgentId", "ParentRunId") ON DELETE RESTRICT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "JobRuns" WHERE "IsAggregate" OR "ParentRunId" IS NOT NULL)
                    THEN RAISE EXCEPTION 'Aggregate job history prevents downgrade; retain its target and native identity records'; END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_JobRuns_JobRuns_ParentRunId",
                table: "JobRuns");

            migrationBuilder.DropTable(
                name: "JobAggregateControls");

            migrationBuilder.DropTable(
                name: "JobRunNodes");

            migrationBuilder.Sql("DROP INDEX \"IX_JobRuns_NativeNodeBinding\"");

            migrationBuilder.DropIndex(
                name: "IX_JobRuns_Id_TenantId",
                table: "JobRuns");

            migrationBuilder.DropIndex(
                name: "IX_JobRuns_ParentRunId",
                table: "JobRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRuns_AggregateTarget",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ExecutionPolicyJson",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "TargetJson",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "DefinitionDigest",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "IsAggregate",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "ParentRunId",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "TargetPlanDigest",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "TargetPlanJson",
                table: "JobRuns");
        }
    }
}
