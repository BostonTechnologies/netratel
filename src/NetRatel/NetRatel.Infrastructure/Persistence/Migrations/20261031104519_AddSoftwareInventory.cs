using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftwareInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SoftwareInventoryPolicies",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Interval = table.Column<int>(type: "integer", nullable: false),
                    Unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareInventoryPolicies", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_SoftwareInventoryPolicies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoftwareInventoryStates",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    LatestScanId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastSuccessfulScanUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextDueUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareInventoryStates", x => new { x.TenantId, x.AgentId });
                    table.ForeignKey(
                        name: "FK_SoftwareInventoryStates_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoftwareInstallations",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Identity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Publisher = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Architecture = table.Column<string>(type: "text", nullable: false),
                    PackageId = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Scope = table.Column<string>(type: "text", nullable: false),
                    InstallLocation = table.Column<string>(type: "text", nullable: true),
                    InstallDate = table.Column<string>(type: "text", nullable: true),
                    Os = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DetectedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareInstallations", x => new { x.TenantId, x.AgentId, x.Identity });
                    table.ForeignKey(
                        name: "FK_SoftwareInstallations_SoftwareInventoryStates_TenantId_Agen~",
                        columns: x => new { x.TenantId, x.AgentId },
                        principalTable: "SoftwareInventoryStates",
                        principalColumns: new[] { "TenantId", "AgentId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoftwareInventoryScans",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DetectedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TotalChunks = table.Column<int>(type: "integer", nullable: false),
                    ReceivedChunks = table.Column<int>(type: "integer", nullable: false),
                    Complete = table.Column<bool>(type: "boolean", nullable: false),
                    Os = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareInventoryScans", x => new { x.TenantId, x.AgentId, x.CollectionId });
                    table.ForeignKey(
                        name: "FK_SoftwareInventoryScans_SoftwareInventoryStates_TenantId_Age~",
                        columns: x => new { x.TenantId, x.AgentId },
                        principalTable: "SoftwareInventoryStates",
                        principalColumns: new[] { "TenantId", "AgentId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoftwareInventoryChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PreviousVersion = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<string>(type: "text", nullable: true),
                    DetectedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareInventoryChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoftwareInventoryChanges_SoftwareInventoryScans_TenantId_Ag~",
                        columns: x => new { x.TenantId, x.AgentId, x.CollectionId },
                        principalTable: "SoftwareInventoryScans",
                        principalColumns: new[] { "TenantId", "AgentId", "CollectionId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoftwareInventoryChunks",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PackagesJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareInventoryChunks", x => new { x.TenantId, x.AgentId, x.CollectionId, x.Index });
                    table.ForeignKey(
                        name: "FK_SoftwareInventoryChunks_SoftwareInventoryScans_TenantId_Age~",
                        columns: x => new { x.TenantId, x.AgentId, x.CollectionId },
                        principalTable: "SoftwareInventoryScans",
                        principalColumns: new[] { "TenantId", "AgentId", "CollectionId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInstallations_TenantId_Name_Version",
                table: "SoftwareInstallations",
                columns: new[] { "TenantId", "Name", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInstallations_TenantId_Publisher",
                table: "SoftwareInstallations",
                columns: new[] { "TenantId", "Publisher" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInstallations_TenantId_Source_Os",
                table: "SoftwareInstallations",
                columns: new[] { "TenantId", "Source", "Os" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInventoryChanges_TenantId_AgentId_CollectionId",
                table: "SoftwareInventoryChanges",
                columns: new[] { "TenantId", "AgentId", "CollectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInventoryChanges_TenantId_AgentId_Id",
                table: "SoftwareInventoryChanges",
                columns: new[] { "TenantId", "AgentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInventoryScans_TenantId_AgentId_RequestedAtUtc",
                table: "SoftwareInventoryScans",
                columns: new[] { "TenantId", "AgentId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInventoryStates_AgentId",
                table: "SoftwareInventoryStates",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareInventoryStates_TenantId_NextDueUtc",
                table: "SoftwareInventoryStates",
                columns: new[] { "TenantId", "NextDueUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SoftwareInstallations");

            migrationBuilder.DropTable(
                name: "SoftwareInventoryChanges");

            migrationBuilder.DropTable(
                name: "SoftwareInventoryChunks");

            migrationBuilder.DropTable(
                name: "SoftwareInventoryPolicies");

            migrationBuilder.DropTable(
                name: "SoftwareInventoryScans");

            migrationBuilder.DropTable(
                name: "SoftwareInventoryStates");
        }
    }
}
