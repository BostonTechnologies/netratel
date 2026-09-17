using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddMcpOperatorCommandOwnership : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "McpOperatorCommands",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CommandId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                TenantId = table.Column<int>(type: "integer", nullable: false),
                AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                McpResource = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                McpInstance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                PolicyVersion = table.Column<long>(type: "bigint", nullable: false),
                AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: false),
                IdempotencyId = table.Column<Guid>(type: "uuid", nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ShellType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                WorkingDirectory = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                CommandHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CommandLength = table.Column<int>(type: "integer", nullable: false),
                EnvironmentReferencesJson = table.Column<string>(type: "jsonb", nullable: false),
                TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                MaximumOutputBytes = table.Column<int>(type: "integer", nullable: false),
                EffectiveConstraintsJson = table.Column<string>(type: "jsonb", nullable: false),
                State = table.Column<short>(type: "smallint", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastUpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_McpOperatorCommands", x => x.Id);
                table.ForeignKey(
                    name: "FK_McpOperatorCommands_Agents_AgentId",
                    column: x => x.AgentId,
                    principalTable: "Agents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorCommands_McpOperatorAcceptedAudits_AcceptedAuditId",
                    column: x => x.AcceptedAuditId,
                    principalTable: "McpOperatorAcceptedAudits",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorCommands_McpOperatorIdempotencyRecords_IdempotencyId",
                    column: x => x.IdempotencyId,
                    principalTable: "McpOperatorIdempotencyRecords",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_McpOperatorCommands_McpOperatorPolicies_PolicyId",
                    column: x => x.PolicyId,
                    principalTable: "McpOperatorPolicies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorCommands_AcceptedAuditId",
            table: "McpOperatorCommands",
            column: "AcceptedAuditId");

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorCommands_AgentId",
            table: "McpOperatorCommands",
            column: "AgentId");

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorCommands_CommandId",
            table: "McpOperatorCommands",
            column: "CommandId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorCommands_IdempotencyId",
            table: "McpOperatorCommands",
            column: "IdempotencyId",
            unique: true,
            filter: "\"IdempotencyId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorCommands_PolicyId",
            table: "McpOperatorCommands",
            column: "PolicyId");

        migrationBuilder.CreateIndex(
            name: "IX_McpOperatorCommands_TenantId_AgentId_Subject_ClientId_State",
            table: "McpOperatorCommands",
            columns: new[] { "TenantId", "AgentId", "Subject", "ClientId", "State" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "McpOperatorCommands");
    }
}
