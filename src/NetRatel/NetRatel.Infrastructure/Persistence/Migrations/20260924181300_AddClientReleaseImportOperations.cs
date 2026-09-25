using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClientReleaseImportOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientReleaseImportOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GitHubReleaseId = table.Column<long>(type: "bigint", nullable: false),
                    SourceRepository = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Tag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BuildCommit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PublicationSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsAutomatic = table.Column<bool>(type: "boolean", nullable: false),
                    CancellationRequested = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ImportedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    DownloadedBytes = table.Column<long>(type: "bigint", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LeaseOwner = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientReleaseImportOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientReleaseImportAssets",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GitHubAssetId = table.Column<long>(type: "bigint", nullable: false),
                    SourceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SourceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    LocalSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LocalSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ConversionContract = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    State = table.Column<short>(type: "smallint", nullable: false),
                    DownloadedBytes = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientReleaseImportAssets", x => new { x.OperationId, x.RuntimeId });
                    table.ForeignKey(
                        name: "FK_ClientReleaseImportAssets_ClientReleaseImportOperations_Ope~",
                        column: x => x.OperationId,
                        principalTable: "ClientReleaseImportOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientReleaseImportOperations_GitHubReleaseId",
                table: "ClientReleaseImportOperations",
                column: "GitHubReleaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientReleaseImportOperations_State_LeaseUntilUtc_CreatedAt~",
                table: "ClientReleaseImportOperations",
                columns: new[] { "State", "LeaseUntilUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientReleaseImportOperations_Version",
                table: "ClientReleaseImportOperations",
                column: "Version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientReleaseImportAssets");

            migrationBuilder.DropTable(
                name: "ClientReleaseImportOperations");
        }
    }
}
