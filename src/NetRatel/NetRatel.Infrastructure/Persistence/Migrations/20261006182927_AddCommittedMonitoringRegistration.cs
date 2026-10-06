using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommittedMonitoringRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CommittedRegistrationOrdinal",
                table: "MonitoringEvidenceStreams",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "MonitoringEvidenceRegistrationAttempts",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionEpoch = table.Column<long>(type: "bigint", nullable: false),
                    RegistrationOrdinal = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetainUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringEvidenceRegistrationAttempts", x => new { x.TenantId, x.AgentId, x.RegistrationId });
                    table.CheckConstraint("CK_MonitoringEvidenceRegistrationAttempts_Shape", "\"TenantId\">0 AND \"AgentId\"<>'00000000-0000-0000-0000-000000000000'::uuid AND \"RegistrationId\"<>'00000000-0000-0000-0000-000000000000'::uuid AND \"ConnectionId\"<>'00000000-0000-0000-0000-000000000000'::uuid AND \"ConnectionEpoch\">0 AND \"RegistrationOrdinal\">0 AND \"Status\" IN (1,2,3) AND \"ExpiresAtUtc\">\"CreatedAtUtc\" AND \"RetainUntilUtc\">=\"ExpiresAtUtc\"");
                });

            migrationBuilder.CreateTable(
                name: "MonitoringEvidenceRegistrationCounters",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastIssuedOrdinal = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringEvidenceRegistrationCounters", x => new { x.TenantId, x.AgentId });
                    table.CheckConstraint("CK_MonitoringEvidenceRegistrationCounters_Identity", "\"TenantId\">0 AND \"AgentId\"<>'00000000-0000-0000-0000-000000000000'::uuid AND \"LastIssuedOrdinal\">=0");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_MonitoringEvidenceStreams_Ordinal",
                table: "MonitoringEvidenceStreams",
                sql: "\"CommittedRegistrationOrdinal\">=0");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringEvidenceRegistrationAttempts_RetainUntilUtc",
                table: "MonitoringEvidenceRegistrationAttempts",
                column: "RetainUntilUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringEvidenceRegistrationAttempts_TenantId_AgentId_Reg~",
                table: "MonitoringEvidenceRegistrationAttempts",
                columns: new[] { "TenantId", "AgentId", "RegistrationOrdinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitoringEvidenceRegistrationAttempts");

            migrationBuilder.DropTable(
                name: "MonitoringEvidenceRegistrationCounters");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MonitoringEvidenceStreams_Ordinal",
                table: "MonitoringEvidenceStreams");

            migrationBuilder.DropColumn(
                name: "CommittedRegistrationOrdinal",
                table: "MonitoringEvidenceStreams");
        }
    }
}
