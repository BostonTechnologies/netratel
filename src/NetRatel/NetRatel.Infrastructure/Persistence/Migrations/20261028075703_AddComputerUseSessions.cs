using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComputerUseSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComputerUseAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ServicePrincipal = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Resource = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Instance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: true),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputerUseAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComputerUseOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ServicePrincipal = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Resource = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Instance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MayHaveApplied = table.Column<bool>(type: "boolean", nullable: false),
                    AcceptedAuditId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputerUseOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ComputerUseSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionEpoch = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ServicePrincipal = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Resource = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Instance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TargetJson = table.Column<string>(type: "jsonb", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ControlAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    OperatorPaused = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActiveSlot = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdleExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdleTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    PendingOperationId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputerUseSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComputerUseAudits_OperationId",
                table: "ComputerUseAudits",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_ComputerUseAudits_TenantId_AgentId_OccurredAtUtc",
                table: "ComputerUseAudits",
                columns: new[] { "TenantId", "AgentId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ComputerUseOperations_SessionId_CreatedAtUtc",
                table: "ComputerUseOperations",
                columns: new[] { "SessionId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ComputerUseSessions_TenantId_AgentId_ActiveSlot",
                table: "ComputerUseSessions",
                columns: new[] { "TenantId", "AgentId", "ActiveSlot" },
                unique: true);

            // Audit immutability also applies to SQL writers outside EF.
            migrationBuilder.Sql("""
                CREATE FUNCTION netratel_computer_use_audit_immutable() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Computer-use audit records are append-only.' USING ERRCODE = '55000';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER computer_use_audit_immutable
                    BEFORE UPDATE OR DELETE ON "ComputerUseAudits"
                    FOR EACH ROW EXECUTE FUNCTION netratel_computer_use_audit_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComputerUseAudits");

            migrationBuilder.Sql("DROP FUNCTION netratel_computer_use_audit_immutable();");

            migrationBuilder.DropTable(
                name: "ComputerUseOperations");

            migrationBuilder.DropTable(
                name: "ComputerUseSessions");
        }
    }
}
