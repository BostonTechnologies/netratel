using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClientOperationsAndNetclawInvestigations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiInvestigations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AgentIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    FlowRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceActionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RemoteSessionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ProfileFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    ActiveMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActiveCredentialId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Automated = table.Column<bool>(type: "boolean", nullable: false),
                    LastActivityUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastTransportActivityAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiInvestigations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientServiceOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResultJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientServiceOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NetclawConnections",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ProtectedDeviceToken = table.Column<string>(type: "text", nullable: false),
                    AllowPrivateHttp = table.Column<bool>(type: "boolean", nullable: false),
                    ProfileFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LastTestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastTestSucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    LastTestCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetclawConnections", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "AiInvestigationApprovals",
                columns: table => new
                {
                    InvestigationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CallId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OptionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SelectedKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiInvestigationApprovals", x => new { x.InvestigationId, x.CallId });
                    table.ForeignKey(
                        name: "FK_AiInvestigationApprovals_AiInvestigations_InvestigationId",
                        column: x => x.InvestigationId,
                        principalTable: "AiInvestigations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiInvestigationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvestigationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CallId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OptionsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiInvestigationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiInvestigationEvents_AiInvestigations_InvestigationId",
                        column: x => x.InvestigationId,
                        principalTable: "AiInvestigations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiInvestigationEvents_InvestigationId_MessageId",
                table: "AiInvestigationEvents",
                columns: new[] { "InvestigationId", "MessageId" },
                unique: true,
                filter: "\"MessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AiInvestigationEvents_InvestigationId_Sequence",
                table: "AiInvestigationEvents",
                columns: new[] { "InvestigationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiInvestigations_TenantId_LastActivityUtc",
                table: "AiInvestigations",
                columns: new[] { "TenantId", "LastActivityUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiInvestigations_TenantId_OccurrenceId",
                table: "AiInvestigations",
                columns: new[] { "TenantId", "OccurrenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiInvestigations_TenantId_RequestId",
                table: "AiInvestigations",
                columns: new[] { "TenantId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiInvestigations_TenantId_SourceActionKey",
                table: "AiInvestigations",
                columns: new[] { "TenantId", "SourceActionKey" },
                unique: true,
                filter: "\"SourceActionKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClientServiceOperations_TenantId_AgentId_CreatedAtUtc",
                table: "ClientServiceOperations",
                columns: new[] { "TenantId", "AgentId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NetclawConnections_Id",
                table: "NetclawConnections",
                column: "Id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiInvestigationApprovals");

            migrationBuilder.DropTable(
                name: "AiInvestigationEvents");

            migrationBuilder.DropTable(
                name: "ClientServiceOperations");

            migrationBuilder.DropTable(
                name: "NetclawConnections");

            migrationBuilder.DropTable(
                name: "AiInvestigations");
        }
    }
}
