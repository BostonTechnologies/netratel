using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDevelopmentOperatorTargetAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DevelopmentOperatorTargetGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Classification = table.Column<short>(type: "smallint", nullable: false),
                    AllowedOperations = table.Column<int>(type: "integer", nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    GrantedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevelopmentOperatorTargetGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevelopmentOperatorTargetGrants_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DevelopmentOperatorAcceptedAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetGrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<short>(type: "smallint", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevelopmentOperatorAcceptedAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevelopmentOperatorAcceptedAudits_DevelopmentOperatorTarget~",
                        column: x => x.TargetGrantId,
                        principalTable: "DevelopmentOperatorTargetGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentOperatorAcceptedAudits_TargetGrantId_OccurredAtU~",
                table: "DevelopmentOperatorAcceptedAudits",
                columns: new[] { "TargetGrantId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentOperatorAcceptedAudits_TenantId_AgentId_Occurred~",
                table: "DevelopmentOperatorAcceptedAudits",
                columns: new[] { "TenantId", "AgentId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentOperatorTargetGrants_AgentId_RevokedAtUtc",
                table: "DevelopmentOperatorTargetGrants",
                columns: new[] { "AgentId", "RevokedAtUtc" },
                unique: true,
                filter: "\"RevokedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentOperatorTargetGrants_TenantId_AgentId_RevokedAtU~",
                table: "DevelopmentOperatorTargetGrants",
                columns: new[] { "TenantId", "AgentId", "RevokedAtUtc", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DevelopmentOperatorAcceptedAudits");

            migrationBuilder.DropTable(
                name: "DevelopmentOperatorTargetGrants");
        }
    }
}
