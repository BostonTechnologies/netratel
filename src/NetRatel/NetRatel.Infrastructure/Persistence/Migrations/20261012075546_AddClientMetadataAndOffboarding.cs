using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClientMetadataAndOffboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FriendlyName",
                table: "Agents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "Tags",
                table: "Agents",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateTable(
                name: "ClientOffboardingOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionEpoch = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExpectedKeyFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CompletionTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProtectedCompletionToken = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartDeadlineUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceiptExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ReceiptHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ServiceRemoved = table.Column<bool>(type: "boolean", nullable: false),
                    FilesRemoved = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientOffboardingOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientOffboardingOperations_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientOffboardingOperations_AgentId",
                table: "ClientOffboardingOperations",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientOffboardingOperations_TenantId_AgentId",
                table: "ClientOffboardingOperations",
                columns: new[] { "TenantId", "AgentId" },
                unique: true,
                filter: "\"FinishedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClientOffboardingOperations_TenantId_AgentId_CreatedAtUtc",
                table: "ClientOffboardingOperations",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientOffboardingOperations");

            migrationBuilder.DropColumn(
                name: "FriendlyName",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "Agents");
        }
    }
}
