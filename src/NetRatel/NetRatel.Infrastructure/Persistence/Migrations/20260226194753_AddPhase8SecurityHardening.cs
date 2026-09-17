using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase8SecurityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedScopesJson",
                table: "Agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceInfoJson",
                table: "Agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyAlgorithm",
                table: "Agents",
                type: "text",
                nullable: false,
                defaultValue: "ecdsa-p256");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "KeyRegisteredAtUtc",
                table: "Agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MtlsThumbprint",
                table: "Agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicKey",
                table: "Agents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentNonceLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nonce = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentNonceLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentRefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReplacedByTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastUsedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentRefreshTokens_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentNonceLogs_AgentId_Nonce",
                table: "AgentNonceLogs",
                columns: new[] { "AgentId", "Nonce" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentNonceLogs_CreatedAtUtc",
                table: "AgentNonceLogs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRefreshTokens_AgentId",
                table: "AgentRefreshTokens",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRefreshTokens_AgentId_CreatedAtUtc",
                table: "AgentRefreshTokens",
                columns: new[] { "AgentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRefreshTokens_TokenHash",
                table: "AgentRefreshTokens",
                column: "TokenHash",
                unique: true);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentNonceLogs");

            migrationBuilder.DropTable(
                name: "AgentRefreshTokens");

            migrationBuilder.DropColumn(
                name: "AllowedScopesJson",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "DeviceInfoJson",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "KeyAlgorithm",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "KeyRegisteredAtUtc",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "MtlsThumbprint",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "PublicKey",
                table: "Agents");
        }
    }
}
