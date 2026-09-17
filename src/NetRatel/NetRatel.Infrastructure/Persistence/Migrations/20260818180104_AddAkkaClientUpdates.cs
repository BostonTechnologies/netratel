using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAkkaClientUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentClientUpdateStates",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    SuspendedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SuspensionReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SuspensionAttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    SuppressedReleaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    PolicyRevision = table.Column<long>(type: "bigint", nullable: false),
                    ResumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResumedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentClientUpdateStates", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_AgentClientUpdateStates_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientUpdateCatalogRevision",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientUpdateCatalogRevision", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientUpdateReleases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    RuntimeId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ArtifactKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ManifestJson = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedBy = table.Column<string>(type: "text", nullable: true),
                    DisabledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisabledBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientUpdateReleases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientUpdateAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RuntimeId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    AdmissionNonceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    GatewayConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    GatewayConnectionEpoch = table.Column<long>(type: "bigint", nullable: true),
                    ConfirmationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientUpdateAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientUpdateAttempts_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClientUpdateAttempts_ClientUpdateReleases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "ClientUpdateReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentClientUpdateStates_TenantId_SuspendedAtUtc",
                table: "AgentClientUpdateStates",
                columns: new[] { "TenantId", "SuspendedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_AgentId_ReleaseId",
                table: "ClientUpdateAttempts",
                columns: new[] { "AgentId", "ReleaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_PublicId",
                table: "ClientUpdateAttempts",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_ReleaseId",
                table: "ClientUpdateAttempts",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_TenantId_AgentId_UpdatedAtUtc",
                table: "ClientUpdateAttempts",
                columns: new[] { "TenantId", "AgentId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateReleases_Enabled_RuntimeId_Channel_PublishedAtU~",
                table: "ClientUpdateReleases",
                columns: new[] { "Enabled", "RuntimeId", "Channel", "PublishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateReleases_PublicId",
                table: "ClientUpdateReleases",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateReleases_Revision",
                table: "ClientUpdateReleases",
                column: "Revision",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateReleases_RuntimeId_Version",
                table: "ClientUpdateReleases",
                columns: new[] { "RuntimeId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentClientUpdateStates");

            migrationBuilder.DropTable(
                name: "ClientUpdateAttempts");

            migrationBuilder.DropTable(
                name: "ClientUpdateCatalogRevision");

            migrationBuilder.DropTable(
                name: "ClientUpdateReleases");
        }
    }
}
