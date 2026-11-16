using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationTraceability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginContextJson",
                table: "ManagedOrchestrationRequestBindings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutomationReferenceBackfill",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LastRequestId = table.Column<int>(type: "integer", nullable: false),
                    AdmissionsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    ReceiptOffset = table.Column<int>(type: "integer", nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false),
                    UnavailableReceipts = table.Column<int>(type: "integer", nullable: false),
                    UnavailableAdmissions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationReferenceBackfill", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExternalResourceReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    PeerInstanceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    PeerOrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReferenceKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NormalizedReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    ExternalUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SafeTitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ResourceKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NativeRequestId = table.Column<int>(type: "integer", nullable: true),
                    JobRunId = table.Column<long>(type: "bigint", nullable: true),
                    JobDefinitionId = table.Column<long>(type: "bigint", nullable: true),
                    FlowRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActionId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenceKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalResourceReferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalResourceReferences_TenantId_NormalizedReference_Res~",
                table: "ExternalResourceReferences",
                columns: new[] { "TenantId", "NormalizedReference", "ResourceKind", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalResourceReferences_TenantId_PeerInstanceId_PeerOrga~",
                table: "ExternalResourceReferences",
                columns: new[] { "TenantId", "PeerInstanceId", "PeerOrganizationId", "ReferenceKind", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalResourceReferences_TenantId_ResourceKind_ResourceId",
                table: "ExternalResourceReferences",
                columns: new[] { "TenantId", "ResourceKind", "ResourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationReferenceBackfill");

            migrationBuilder.DropTable(
                name: "ExternalResourceReferences");

            migrationBuilder.DropColumn(
                name: "OriginContextJson",
                table: "ManagedOrchestrationRequestBindings");
        }
    }
}
