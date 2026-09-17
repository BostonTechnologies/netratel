using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpOperatorTerminalActionIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpOperatorTerminalActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DelegationRequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<short>(type: "smallint", nullable: false),
                    ResultReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorTerminalActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorTerminalActions_McpOperatorAcceptedAudits_Accept~",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorTerminalActions_McpOperatorTerminalSessions_Sess~",
                        column: x => x.SessionRecordId,
                        principalTable: "McpOperatorTerminalSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalActions_AcceptedAuditId",
                table: "McpOperatorTerminalActions",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalActions_SessionRecordId_CreatedAtUtc",
                table: "McpOperatorTerminalActions",
                columns: new[] { "SessionRecordId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorTerminalActions_SessionRecordId_Operation_Delega~",
                table: "McpOperatorTerminalActions",
                columns: new[] { "SessionRecordId", "Operation", "DelegationRequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpOperatorTerminalActions");
        }
    }
}
