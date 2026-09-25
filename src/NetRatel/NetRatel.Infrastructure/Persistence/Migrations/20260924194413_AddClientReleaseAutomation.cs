using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClientReleaseAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AutomaticPublishAttemptAtUtc",
                table: "ClientReleaseImportOperations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutomaticPublishError",
                table: "ClientReleaseImportOperations",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientReleaseAutomationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    CheckEveryHours = table.Column<int>(type: "integer", nullable: false),
                    DownloadStable = table.Column<bool>(type: "boolean", nullable: false),
                    DownloadPrerelease = table.Column<bool>(type: "boolean", nullable: false),
                    PublishAutomatically = table.Column<bool>(type: "boolean", nullable: false),
                    DeployPrereleaseAutomatically = table.Column<bool>(type: "boolean", nullable: false),
                    NextCheckAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LeaseOwner = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientReleaseAutomationSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientReleaseAutomationSettings");

            migrationBuilder.DropColumn(
                name: "AutomaticPublishAttemptAtUtc",
                table: "ClientReleaseImportOperations");

            migrationBuilder.DropColumn(
                name: "AutomaticPublishError",
                table: "ClientReleaseImportOperations");
        }
    }
}
