using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FlowAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    FlowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FlowDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    DraftJson = table.Column<string>(type: "jsonb", nullable: false),
                    PublishedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedVersionNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowDefinitions", x => x.Id);
                    table.UniqueConstraint("AK_FlowDefinitions_TenantId_Id", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "FlowRuntimeIdentity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    SourceInstanceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowRuntimeIdentity", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FlowVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    GraphJson = table.Column<string>(type: "jsonb", nullable: false),
                    ConfigurationHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PublishedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowVersions", x => x.Id);
                    table.UniqueConstraint("AK_FlowVersions_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_FlowVersions_FlowDefinitions_TenantId_FlowId",
                        columns: x => new { x.TenantId, x.FlowId },
                        principalTable: "FlowDefinitions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FlowRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    FlowId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventJson = table.Column<string>(type: "jsonb", nullable: false),
                    EventFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Fence = table.Column<long>(type: "bigint", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseOwner = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowRuns", x => x.Id);
                    table.UniqueConstraint("AK_FlowRuns_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_FlowRuns_FlowVersions_TenantId_FlowVersionId",
                        columns: x => new { x.TenantId, x.FlowVersionId },
                        principalTable: "FlowVersions",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FlowActions",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DraftJson = table.Column<string>(type: "jsonb", nullable: false),
                    PreparedJson = table.Column<string>(type: "jsonb", nullable: true),
                    ConnectorRevision = table.Column<long>(type: "bigint", nullable: true),
                    SemanticFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReceiptJson = table.Column<string>(type: "jsonb", nullable: true),
                    LeaseFence = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowActions", x => new { x.RunId, x.NodeId });
                    table.ForeignKey(
                        name: "FK_FlowActions_FlowRuns_TenantId_RunId",
                        columns: x => new { x.TenantId, x.RunId },
                        principalTable: "FlowRuns",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FlowActions_TenantId_IdempotencyKey",
                table: "FlowActions",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlowActions_TenantId_RunId",
                table: "FlowActions",
                columns: new[] { "TenantId", "RunId" });

            migrationBuilder.CreateIndex(
                name: "IX_FlowAudits_TenantId_FlowId_AtUtc",
                table: "FlowAudits",
                columns: new[] { "TenantId", "FlowId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FlowDefinitions_TenantId_UpdatedAtUtc",
                table: "FlowDefinitions",
                columns: new[] { "TenantId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FlowRuns_Status_NextAttemptAtUtc_LeaseExpiresAtUtc",
                table: "FlowRuns",
                columns: new[] { "Status", "NextAttemptAtUtc", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FlowRuns_TenantId_EventId_FlowVersionId",
                table: "FlowRuns",
                columns: new[] { "TenantId", "EventId", "FlowVersionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlowRuns_TenantId_FlowId_CreatedAtUtc",
                table: "FlowRuns",
                columns: new[] { "TenantId", "FlowId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FlowRuns_TenantId_FlowVersionId",
                table: "FlowRuns",
                columns: new[] { "TenantId", "FlowVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_FlowVersions_TenantId_FlowId_VersionNumber",
                table: "FlowVersions",
                columns: new[] { "TenantId", "FlowId", "VersionNumber" },
                unique: true);

            // Published definitions and the semantic payload committed before a send remain immutable
            // even if a future repository implementation accidentally issues an UPDATE.
            migrationBuilder.Sql("""
                CREATE FUNCTION netratel_flow_version_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW IS DISTINCT FROM OLD THEN
                        RAISE EXCEPTION 'flow-version-immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER flow_version_immutable BEFORE UPDATE ON "FlowVersions"
                    FOR EACH ROW EXECUTE FUNCTION netratel_flow_version_immutable();

                CREATE FUNCTION netratel_flow_action_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF ROW(NEW."RunId", NEW."NodeId", NEW."TenantId", NEW."IdempotencyKey", NEW."DraftJson")
                        IS DISTINCT FROM ROW(OLD."RunId", OLD."NodeId", OLD."TenantId", OLD."IdempotencyKey", OLD."DraftJson")
                        OR (OLD."PreparedJson" IS NOT NULL AND
                            ROW(NEW."PreparedJson", NEW."ConnectorRevision", NEW."SemanticFingerprint") IS DISTINCT FROM
                            ROW(OLD."PreparedJson", OLD."ConnectorRevision", OLD."SemanticFingerprint"))
                        OR (OLD."ReceiptJson" IS NOT NULL AND NEW."ReceiptJson" IS DISTINCT FROM OLD."ReceiptJson") THEN
                        RAISE EXCEPTION 'flow-action-immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER flow_action_immutable BEFORE UPDATE ON "FlowActions"
                    FOR EACH ROW EXECUTE FUNCTION netratel_flow_action_immutable();

                CREATE FUNCTION netratel_flow_event_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF ROW(NEW."TenantId", NEW."FlowId", NEW."FlowVersionId", NEW."EventId", NEW."OccurrenceId", NEW."EventJson", NEW."EventFingerprint")
                        IS DISTINCT FROM ROW(OLD."TenantId", OLD."FlowId", OLD."FlowVersionId", OLD."EventId", OLD."OccurrenceId", OLD."EventJson", OLD."EventFingerprint") THEN
                        RAISE EXCEPTION 'flow-event-immutable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER flow_event_immutable BEFORE UPDATE ON "FlowRuns"
                    FOR EACH ROW EXECUTE FUNCTION netratel_flow_event_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlowActions");

            migrationBuilder.DropTable(
                name: "FlowAudits");

            migrationBuilder.DropTable(
                name: "FlowRuntimeIdentity");

            migrationBuilder.DropTable(
                name: "FlowRuns");

            migrationBuilder.DropTable(
                name: "FlowVersions");

            migrationBuilder.DropTable(
                name: "FlowDefinitions");

            migrationBuilder.Sql("""
                DROP FUNCTION netratel_flow_event_immutable();
                DROP FUNCTION netratel_flow_action_immutable();
                DROP FUNCTION netratel_flow_version_immutable();
                """);
        }
    }
}
