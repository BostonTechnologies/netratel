using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalClientReleaseDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientUpdateAttempts_ClientUpdateReleases_ReleaseId",
                table: "ClientUpdateAttempts");

            migrationBuilder.DropIndex(
                name: "IX_ClientUpdateAttempts_ReleaseId",
                table: "ClientUpdateAttempts");

            migrationBuilder.AddColumn<Guid>(
                name: "PurgedReleasePublicId",
                table: "ClientUpdateAttempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReleaseRecordId",
                table: "ClientUpdateAttempts",
                type: "integer",
                nullable: true);

            // Preserve existing live references before switching the navigation FK. ReleaseId
            // remains the immutable historical snapshot when a local package is later purged.
            migrationBuilder.Sql("UPDATE \"ClientUpdateAttempts\" SET \"ReleaseRecordId\" = \"ReleaseId\"");

            migrationBuilder.CreateTable(
                name: "ClientArtifactDownloadLeases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientArtifactDownloadLeases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientReleaseDeletionReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PlanJson = table.Column<string>(type: "text", nullable: false),
                    PreviousReceiptsJson = table.Column<string>(type: "text", nullable: false),
                    Suppressed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientReleaseDeletionReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_ReleaseRecordId",
                table: "ClientUpdateAttempts",
                column: "ReleaseRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientArtifactDownloadLeases_Version_ExpiresAtUtc",
                table: "ClientArtifactDownloadLeases",
                columns: new[] { "Version", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientReleaseDeletionReceipts_Version",
                table: "ClientReleaseDeletionReceipts",
                column: "Version",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientUpdateAttempts_ClientUpdateReleases_ReleaseRecordId",
                table: "ClientUpdateAttempts",
                column: "ReleaseRecordId",
                principalTable: "ClientUpdateReleases",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removed package records cannot be fabricated to restore the former required FK.
            // Refuse this downgrade instead of deleting retained update-attempt evidence.
            migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM \"ClientUpdateAttempts\" WHERE \"PurgedReleasePublicId\" IS NOT NULL) THEN RAISE EXCEPTION 'Cannot downgrade local release deletion after a purge; historical attempts must be retained.'; END IF; END $$;");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientUpdateAttempts_ClientUpdateReleases_ReleaseRecordId",
                table: "ClientUpdateAttempts");

            migrationBuilder.DropTable(
                name: "ClientArtifactDownloadLeases");

            migrationBuilder.DropTable(
                name: "ClientReleaseDeletionReceipts");

            migrationBuilder.DropIndex(
                name: "IX_ClientUpdateAttempts_ReleaseRecordId",
                table: "ClientUpdateAttempts");

            migrationBuilder.DropColumn(
                name: "PurgedReleasePublicId",
                table: "ClientUpdateAttempts");

            migrationBuilder.DropColumn(
                name: "ReleaseRecordId",
                table: "ClientUpdateAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_ClientUpdateAttempts_ReleaseId",
                table: "ClientUpdateAttempts",
                column: "ReleaseId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientUpdateAttempts_ClientUpdateReleases_ReleaseId",
                table: "ClientUpdateAttempts",
                column: "ReleaseId",
                principalTable: "ClientUpdateReleases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
