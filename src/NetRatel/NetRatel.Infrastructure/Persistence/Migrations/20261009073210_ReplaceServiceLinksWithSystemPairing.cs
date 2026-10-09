using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceServiceLinksWithSystemPairing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time cutover revokes the old NR↔RD grant owners before retiring their state.
            // Do not parse old descriptors/settings: malformed legacy attempts must upgrade too.
            migrationBuilder.Sql("""
                UPDATE "ServicePrincipalSecrets" SET "Status" = 'revoked'
                WHERE "ServicePrincipalId" IN (
                    SELECT "Id" FROM "ServicePrincipalRegistrations"
                    WHERE "LinkId" IS NOT NULL OR "AttemptId" IS NOT NULL
                       OR "AllowedScopesJson" LIKE '%netratel.orchestration.%');
                UPDATE "ServicePrincipalRegistrations"
                SET "Status" = 'revoked', "RevokedAtUtc" = COALESCE("RevokedAtUtc", now()),
                    "TerminalControlUntilUtc" = NULL, "Revision" = "Revision" + 1, "Version" = "Version" + 1
                WHERE "LinkId" IS NOT NULL OR "AttemptId" IS NOT NULL
                   OR "AllowedScopesJson" LIKE '%netratel.orchestration.%';
                UPDATE "RatelDeskConnectors"
                SET "ProtectedCredential" = NULL, "AuthenticationJson" = NULL, "ReadinessJson" = NULL,
                    "Revision" = "Revision" + 1, "RowVersion" = "RowVersion" + 1,
                    "ConfigurationJson" = CASE WHEN jsonb_typeof("ConfigurationJson") = 'object'
                        THEN jsonb_set(jsonb_set("ConfigurationJson", '{Enabled}', 'false'::jsonb), '{enabled}', 'false'::jsonb)
                        ELSE '{"Name":"Retired RatelDesk connection","Origin":"","OrganizationId":"","CustomerId":"","CategoryIds":[],"Priorities":{},"Enabled":false}'::jsonb END;
                """);
            migrationBuilder.DropTable(
                name: "M2MConnectivitySettings");

            migrationBuilder.DropTable(
                name: "ServiceLinkAttempts");

            migrationBuilder.DropTable(
                name: "ServiceLinkOperations");

            migrationBuilder.DropTable(
                name: "ServiceLinkRotations");

            migrationBuilder.DropTable(
                name: "ServiceLinkVerificationReceipts");

            migrationBuilder.CreateTable(
                name: "PairingCleanup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PairId = table.Column<string>(type: "text", nullable: false),
                    MappingId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProtectedPeerJson = table.Column<string>(type: "text", nullable: false),
                    ProtectedSecret = table.Column<string>(type: "text", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairingCleanup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PairingCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    CodeHash = table.Column<string>(type: "text", nullable: false),
                    AdministratorId = table.Column<string>(type: "text", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairingCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PairingConnectAttempts",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairId = table.Column<string>(type: "text", nullable: false),
                    RequestHash = table.Column<string>(type: "text", nullable: false),
                    ProtectedRequest = table.Column<string>(type: "text", nullable: false),
                    PairRevision = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairingConnectAttempts", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "PairingConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PairId = table.Column<string>(type: "text", nullable: false),
                    MappingJson = table.Column<string>(type: "text", nullable: false),
                    AdministratorId = table.Column<string>(type: "text", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SaveFingerprint = table.Column<string>(type: "text", nullable: false),
                    InboundPrincipalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProtectedInboundCredential = table.Column<string>(type: "text", nullable: true),
                    ProtectedOutboundCredential = table.Column<string>(type: "text", nullable: true),
                    LastTestJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairingConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PairingRedemptions",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeerInstanceId = table.Column<string>(type: "text", nullable: false),
                    RequestHash = table.Column<string>(type: "text", nullable: false),
                    PairId = table.Column<string>(type: "text", nullable: false),
                    ProtectedResponse = table.Column<string>(type: "text", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairingRedemptions", x => x.OperationId);
                });

            migrationBuilder.CreateTable(
                name: "SystemPairs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    PeerInstanceId = table.Column<string>(type: "text", nullable: false),
                    PeerMetadataJson = table.Column<string>(type: "text", nullable: false),
                    AdministratorId = table.Column<string>(type: "text", nullable: false),
                    InboundSecretHash = table.Column<string>(type: "text", nullable: false),
                    ProtectedInboundSecret = table.Column<string>(type: "text", nullable: false),
                    ProtectedOutboundSecret = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemPairs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PairingConnections_PairId",
                table: "PairingConnections",
                column: "PairId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemPairs_PeerInstanceId",
                table: "SystemPairs",
                column: "PeerInstanceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("The system pairing cutover revoked old credentials and cannot restore their authority. Restore a supported database backup to roll back.");
        }
    }
}
