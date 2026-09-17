using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpOperatorFileArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McpOperatorFileArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    McpResource = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    McpInstance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReadRootFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOperatorFileArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOperatorFileArtifacts_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorFileArtifacts_McpOperatorAcceptedAudits_Accepted~",
                        column: x => x.AcceptedAuditId,
                        principalTable: "McpOperatorAcceptedAudits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McpOperatorFileArtifacts_McpOperatorIdempotencyRecords_Idem~",
                        column: x => x.IdempotencyId,
                        principalTable: "McpOperatorIdempotencyRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorFileArtifacts_AcceptedAuditId",
                table: "McpOperatorFileArtifacts",
                column: "AcceptedAuditId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorFileArtifacts_AgentId",
                table: "McpOperatorFileArtifacts",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorFileArtifacts_IdempotencyId",
                table: "McpOperatorFileArtifacts",
                column: "IdempotencyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorFileArtifacts_TenantId_AgentId_Subject_ClientId_~",
                table: "McpOperatorFileArtifacts",
                columns: new[] { "TenantId", "AgentId", "Subject", "ClientId", "McpResource", "McpInstance", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpOperatorFileArtifacts");
        }
    }
}
