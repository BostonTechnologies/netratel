using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddMcpOperatorTerminalSessionOwnership : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "McpOperatorTerminalSessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                TenantId = table.Column<int>(type: "integer", nullable: false),
                AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                Generation = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                McpResource = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                McpInstance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                PolicyVersion = table.Column<long>(type: "bigint", nullable: false),
                AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: false),
                IdempotencyId = table.Column<Guid>(type: "uuid", nullable: true),
                ShellType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                WorkingDirectory = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                Columns = table.Column<int>(type: "integer", nullable: false),
                Rows = table.Column<int>(type: "integer", nullable: false),
                EffectiveConstraintsJson = table.Column<string>(type: "jsonb", nullable: false),
                State = table.Column<short>(type: "smallint", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastActivityAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                IdleExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CloseRequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CloseReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorTerminalSessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorTerminalSessions_Agents_AgentId",
                    column: x => x.AgentId,
                    principalTable: "Agents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorTerminalSessions_McpOperatorAcceptedAudits_AcceptedAuditId",
                    column: x => x.AcceptedAuditId,
                    principalTable: "McpOperatorAcceptedAudits",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorTerminalSessions_McpOperatorIdempotencyRecords_IdempotencyId",
                    column: x => x.IdempotencyId,
                    principalTable: "McpOperatorIdempotencyRecords",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorTerminalSessions_McpOperatorPolicies_PolicyId",
                    column: x => x.PolicyId,
                    principalTable: "McpOperatorPolicies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "McpOperatorTerminalSessionAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SessionRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                State = table.Column<short>(type: "smallint", nullable: false),
                Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorTerminalSessionAudits", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorTerminalSessionAudits_McpOperatorTerminalSessions_SessionRecordId",
                    column: x => x.SessionRecordId,
                    principalTable: "McpOperatorTerminalSessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorTerminalSessionAudits_SessionRecordId_OccurredAtUtc",
            table: "McpOperatorTerminalSessionAudits",
            columns: new[] { "SessionRecordId", "OccurredAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorTerminalSessions_AcceptedAuditId",
            table: "McpOperatorTerminalSessions",
            column: "AcceptedAuditId");

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorTerminalSessions_AgentId",
            table: "McpOperatorTerminalSessions",
            column: "AgentId");

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorTerminalSessions_IdempotencyId",
            table: "McpOperatorTerminalSessions",
            column: "IdempotencyId",
            unique: true,
            filter: "\"IdempotencyId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorTerminalSessions_PolicyId",
            table: "McpOperatorTerminalSessions",
            column: "PolicyId");

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorTerminalSessions_SessionId",
            table: "McpOperatorTerminalSessions",
            column: "SessionId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorTerminalSessions_TenantId_AgentId_State_ExpiresAtUtc",
            table: "McpOperatorTerminalSessions",
            columns: new[] { "TenantId", "AgentId", "State", "ExpiresAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorTerminalSessions_TenantId_AgentId_Subject_ClientId_State",
            table: "McpOperatorTerminalSessions",
            columns: new[] { "TenantId", "AgentId", "Subject", "ClientId", "State" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "McpOperatorTerminalSessionAudits");
        migrationBuilder.DropTable(name: "McpOperatorTerminalSessions");
    }
}
