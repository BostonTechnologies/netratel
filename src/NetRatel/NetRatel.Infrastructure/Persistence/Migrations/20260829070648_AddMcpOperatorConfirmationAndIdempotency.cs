using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpOperatorConfirmationAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpOperatorConfirmationPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Environment = table.Column<short>(type: "smallint", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    McpResource = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    McpInstance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetSetDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyVersion = table.Column<long>(type: "bigint", nullable: false),
                    OperationFamily = table.Column<int>(type: "integer", nullable: false),
                    Operation = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConfirmationClass = table.Column<short>(type: "smallint", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsumedIdempotencyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorConfirmationPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpOperatorIdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Environment = table.Column<short>(type: "smallint", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetSetDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyVersion = table.Column<long>(type: "bigint", nullable: false),
                    OperationFamily = table.Column<int>(type: "integer", nullable: false),
                    Operation = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<short>(type: "smallint", nullable: false),
                    ResultReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorIdempotencyRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorConfirmationPlans_ExpiresAtUtc_ConsumedAtUtc",
                table: "McpOperatorConfirmationPlans",
                columns: new[] { "ExpiresAtUtc", "ConsumedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorConfirmationPlans_TenantId_AgentId_CreatedAtUtc",
                table: "McpOperatorConfirmationPlans",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorConfirmationPlans_TokenHash",
                table: "McpOperatorConfirmationPlans",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorIdempotencyRecords_Environment_Subject_ClientId_~",
                table: "McpOperatorIdempotencyRecords",
                columns: new[] { "Environment", "Subject", "ClientId", "TenantId", "OperationFamily", "Operation", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorIdempotencyRecords_TenantId_AgentId_CreatedAtUtc",
                table: "McpOperatorIdempotencyRecords",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpOperatorConfirmationPlans");

            migrationBuilder.DropTable(
                name: "McpOperatorIdempotencyRecords");
        }
    }
}
