using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteSupportV2LifecycleContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteSupportSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    InitiatingOperatorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TargetKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TargetWindowsSessionId = table.Column<int>(type: "integer", nullable: true),
                    TargetUserSidHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TargetInventorySequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: true),
                    RequestedCapabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    GrantedCapabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LifecycleRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalReasonCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSupportSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemoteSupportAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RemoteSupportSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    AuditSequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    LifecycleRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ActorKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSupportAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemoteSupportAuditEvents_RemoteSupportSessions_RemoteSuppor~",
                        column: x => x.RemoteSupportSessionId,
                        principalTable: "RemoteSupportSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportAuditEvents_RemoteSupportSessionId_AuditSequen~",
                table: "RemoteSupportAuditEvents",
                columns: new[] { "RemoteSupportSessionId", "AuditSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportAuditEvents_RemoteSupportSessionId_RequestId",
                table: "RemoteSupportAuditEvents",
                columns: new[] { "RemoteSupportSessionId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportAuditEvents_TenantId_AgentId_OccurredAtUtc",
                table: "RemoteSupportAuditEvents",
                columns: new[] { "TenantId", "AgentId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportSessions_ExpiresAtUtc",
                table: "RemoteSupportSessions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportSessions_TenantId_AgentId_CreatedAtUtc",
                table: "RemoteSupportSessions",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportSessions_TenantId_AgentId_OpenRequestId",
                table: "RemoteSupportSessions",
                columns: new[] { "TenantId", "AgentId", "OpenRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportSessions_TenantId_InitiatingOperatorId_Updated~",
                table: "RemoteSupportSessions",
                columns: new[] { "TenantId", "InitiatingOperatorId", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteSupportAuditEvents");

            migrationBuilder.DropTable(
                name: "RemoteSupportSessions");
        }
    }
}
