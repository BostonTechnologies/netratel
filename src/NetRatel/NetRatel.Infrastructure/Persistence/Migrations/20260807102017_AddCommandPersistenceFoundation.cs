using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommandPersistenceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommandInboxReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Version = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    Sequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    FirstReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DuplicateCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandInboxReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommandIntentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StatusTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    Sequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsAuthoritative = table.Column<bool>(type: "boolean", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandIntentEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommandOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestTimestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TerminalAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAcceptedVersion = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    LastAcceptedSequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    CurrentStatus = table.Column<short>(type: "smallint", nullable: false),
                    ObservedDispatchCount = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsAuthoritative = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandOutbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommandInboxReceipts_FirstReceivedAtUtc",
                table: "CommandInboxReceipts",
                column: "FirstReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CommandInboxReceipts_TenantId_CorrelationId",
                table: "CommandInboxReceipts",
                columns: new[] { "TenantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "UX_CommandInbox_Idempotency",
                table: "CommandInboxReceipts",
                columns: new[] { "TenantId", "CommandId", "Version", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommandIntentEvents_RecordedAtUtc",
                table: "CommandIntentEvents",
                column: "RecordedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CommandIntentEvents_TenantId_CorrelationId",
                table: "CommandIntentEvents",
                columns: new[] { "TenantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "UX_CommandIntentHistory_Order",
                table: "CommandIntentEvents",
                columns: new[] { "TenantId", "CommandId", "Version", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommandOutbox_TenantId_CorrelationId",
                table: "CommandOutbox",
                columns: new[] { "TenantId", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "IX_CommandOutbox_TerminalAtUtc_CreatedAtUtc",
                table: "CommandOutbox",
                columns: new[] { "TerminalAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_CommandOutbox_Command",
                table: "CommandOutbox",
                columns: new[] { "TenantId", "CommandId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommandInboxReceipts");

            migrationBuilder.DropTable(
                name: "CommandIntentEvents");

            migrationBuilder.DropTable(
                name: "CommandOutbox");
        }
    }
}
