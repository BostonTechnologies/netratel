using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantPromptsAndAiFlowContinuations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContinuationJson",
                table: "FlowRuns",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiResultJson",
                table: "FlowActions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptSnapshotsJson",
                table: "AiInvestigations",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "SelectedPromptReferencesJson",
                table: "AiInvestigations",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "AiInvestigationContexts",
                columns: table => new
                {
                    InvestigationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Request = table.Column<string>(type: "text", nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    OmissionNotesJson = table.Column<string>(type: "jsonb", nullable: false),
                    PayloadBytes = table.Column<int>(type: "integer", nullable: false),
                    PreparedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SendAttemptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiInvestigationContexts", x => new { x.InvestigationId, x.MessageId });
                    table.ForeignKey(
                        name: "FK_AiInvestigationContexts_AiInvestigations_InvestigationId",
                        column: x => x.InvestigationId,
                        principalTable: "AiInvestigations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiInvestigationTurnResults",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    InvestigationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiInvestigationTurnResults", x => new { x.TenantId, x.InvestigationId, x.MessageId });
                    table.ForeignKey(
                        name: "FK_AiInvestigationTurnResults_AiInvestigations_InvestigationId",
                        column: x => x.InvestigationId,
                        principalTable: "AiInvestigations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiPrompts",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiPrompts", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_AiPrompts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiPromptDefaults",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    PromptId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiPromptDefaults", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_AiPromptDefaults_AiPrompts_TenantId_PromptId",
                        columns: x => new { x.TenantId, x.PromptId },
                        principalTable: "AiPrompts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiPromptDefaults_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiPromptRevisions",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    PromptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Body = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiPromptRevisions", x => new { x.TenantId, x.PromptId, x.Revision });
                    table.ForeignKey(
                        name: "FK_AiPromptRevisions_AiPrompts_TenantId_PromptId",
                        columns: x => new { x.TenantId, x.PromptId },
                        principalTable: "AiPrompts",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiInvestigationTurnResults_InvestigationId",
                table: "AiInvestigationTurnResults",
                column: "InvestigationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiPromptDefaults_TenantId_PromptId",
                table: "AiPromptDefaults",
                columns: new[] { "TenantId", "PromptId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiPrompts_TenantId_Archived_Name",
                table: "AiPrompts",
                columns: new[] { "TenantId", "Archived", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiInvestigationContexts");

            migrationBuilder.DropTable(
                name: "AiInvestigationTurnResults");

            migrationBuilder.DropTable(
                name: "AiPromptDefaults");

            migrationBuilder.DropTable(
                name: "AiPromptRevisions");

            migrationBuilder.DropTable(
                name: "AiPrompts");

            migrationBuilder.DropColumn(
                name: "ContinuationJson",
                table: "FlowRuns");

            migrationBuilder.DropColumn(
                name: "AiResultJson",
                table: "FlowActions");

            migrationBuilder.DropColumn(
                name: "PromptSnapshotsJson",
                table: "AiInvestigations");

            migrationBuilder.DropColumn(
                name: "SelectedPromptReferencesJson",
                table: "AiInvestigations");
        }
    }
}
