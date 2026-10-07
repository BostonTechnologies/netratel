using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoringRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitoringAudits",
                columns: table => new
                {
                    AuditId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    EntityKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfigurationRevision = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringAudits", x => x.AuditId);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringBypasses",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    BypassId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringBypasses", x => new { x.TenantId, x.BypassId });
                });

            migrationBuilder.CreateTable(
                name: "MonitoringEvidenceStreams",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionEpoch = table.Column<long>(type: "bigint", nullable: false),
                    EvidenceStreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<decimal>(type: "numeric(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringEvidenceStreams", x => new { x.TenantId, x.AgentId });
                });

            migrationBuilder.CreateTable(
                name: "MonitoringGroups",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringGroups", x => new { x.TenantId, x.GroupId });
                });

            migrationBuilder.CreateTable(
                name: "MonitoringOccurrences",
                columns: table => new
                {
                    OccurrenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    RaisedEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    RaisedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OccurrenceJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringOccurrences", x => x.OccurrenceId);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringRules",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringRules", x => new { x.TenantId, x.RuleId });
                });

            migrationBuilder.CreateTable(
                name: "MonitoringSeries",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    StateRevision = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Phase = table.Column<short>(type: "smallint", nullable: false),
                    EvidenceQuality = table.Column<short>(type: "smallint", nullable: false),
                    ActiveOccurrenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    LatestOccurrenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Acknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    Suppressed = table.Column<bool>(type: "boolean", nullable: false),
                    StateJson = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringSeries", x => new { x.TenantId, x.RuleId, x.AgentId, x.ResourceKey });
                });

            migrationBuilder.CreateTable(
                name: "MonitoringTenantConfigurations",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<decimal>(type: "numeric(20,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringTenantConfigurations", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringEvents",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EventJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringEvents", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_MonitoringEvents_MonitoringOccurrences_OccurrenceId",
                        column: x => x.OccurrenceId,
                        principalTable: "MonitoringOccurrences",
                        principalColumn: "OccurrenceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringFlowOutbox",
                columns: table => new
                {
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    StableFlowDispatchKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IntentJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseFence = table.Column<long>(type: "bigint", nullable: false),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OutcomeJson = table.Column<string>(type: "jsonb", nullable: true),
                    FlowRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    HandedOffAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringFlowOutbox", x => x.OutboxId);
                    table.ForeignKey(
                        name: "FK_MonitoringFlowOutbox_MonitoringEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "MonitoringEvents",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringAudits_TenantId_AtUtc",
                table: "MonitoringAudits",
                columns: new[] { "TenantId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringBypasses_TenantId_ExpiresAtUtc",
                table: "MonitoringBypasses",
                columns: new[] { "TenantId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringEvents_OccurrenceId",
                table: "MonitoringEvents",
                column: "OccurrenceId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringEvents_TenantId_AtUtc_EventId",
                table: "MonitoringEvents",
                columns: new[] { "TenantId", "AtUtc", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringFlowOutbox_EventId",
                table: "MonitoringFlowOutbox",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringFlowOutbox_FlowRunId",
                table: "MonitoringFlowOutbox",
                column: "FlowRunId",
                unique: true,
                filter: "\"FlowRunId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringFlowOutbox_StableFlowDispatchKey",
                table: "MonitoringFlowOutbox",
                column: "StableFlowDispatchKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringFlowOutbox_TenantId_Status_NextAttemptAtUtc_Lease~",
                table: "MonitoringFlowOutbox",
                columns: new[] { "TenantId", "Status", "NextAttemptAtUtc", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringOccurrences_RaisedEventId",
                table: "MonitoringOccurrences",
                column: "RaisedEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringOccurrences_TenantId_EndedAtUtc",
                table: "MonitoringOccurrences",
                columns: new[] { "TenantId", "EndedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_MonitoringOccurrence_OpenSeries",
                table: "MonitoringOccurrences",
                columns: new[] { "TenantId", "RuleId", "AgentId", "ResourceKey" },
                unique: true,
                filter: "\"EndedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringSeries_TenantId_AgentId",
                table: "MonitoringSeries",
                columns: new[] { "TenantId", "AgentId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitoringAudits");

            migrationBuilder.DropTable(
                name: "MonitoringBypasses");

            migrationBuilder.DropTable(
                name: "MonitoringEvidenceStreams");

            migrationBuilder.DropTable(
                name: "MonitoringFlowOutbox");

            migrationBuilder.DropTable(
                name: "MonitoringGroups");

            migrationBuilder.DropTable(
                name: "MonitoringRules");

            migrationBuilder.DropTable(
                name: "MonitoringSeries");

            migrationBuilder.DropTable(
                name: "MonitoringTenantConfigurations");

            migrationBuilder.DropTable(
                name: "MonitoringEvents");

            migrationBuilder.DropTable(
                name: "MonitoringOccurrences");
        }
    }
}
