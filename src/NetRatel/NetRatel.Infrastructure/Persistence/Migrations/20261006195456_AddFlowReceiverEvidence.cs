using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFlowReceiverEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FlowActions_TenantId_RunId",
                table: "FlowActions");

            migrationBuilder.AddColumn<string>(
                name: "AuthenticationJson",
                table: "RatelDeskConnectors",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReadinessJson",
                table: "RatelDeskConnectors",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_FlowActions_TenantId_RunId_NodeId",
                table: "FlowActions",
                columns: new[] { "TenantId", "RunId", "NodeId" });

            migrationBuilder.CreateTable(
                name: "FlowReceiverEvidence",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    PreparationJson = table.Column<string>(type: "jsonb", nullable: false),
                    EvidenceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceiverIdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ReceiverFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OriginalCreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AutomaticReplayUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MayHaveCommitted = table.Column<bool>(type: "boolean", nullable: false),
                    FirstPostAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastPostLeaseFence = table.Column<long>(type: "bigint", nullable: true),
                    FinalReconciliationAttempted = table.Column<bool>(type: "boolean", nullable: false),
                    FullReceiptJson = table.Column<string>(type: "jsonb", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlowReceiverEvidence", x => new { x.RunId, x.NodeId });
                    table.CheckConstraint("CK_FlowReceiverEvidence_V2", "\"SchemaVersion\" = 2 AND \"TenantId\" > 0 AND \"RowVersion\" > 0 AND jsonb_typeof(\"PreparationJson\") = 'object' AND \"AutomaticReplayUntilUtc\" > \"OriginalCreatedAtUtc\" AND \"AutomaticReplayUntilUtc\" <= \"OriginalCreatedAtUtc\" + INTERVAL '24 hours' AND (\"LastPostLeaseFence\" IS NULL OR \"LastPostLeaseFence\" > 0) AND (NOT \"MayHaveCommitted\" OR \"FirstPostAttemptAtUtc\" IS NOT NULL) AND (\"FullReceiptJson\" IS NULL OR jsonb_typeof(\"FullReceiptJson\") = 'object')");
                    table.ForeignKey(
                        name: "FK_FlowReceiverEvidence_FlowActions_TenantId_RunId_NodeId",
                        columns: x => new { x.TenantId, x.RunId, x.NodeId },
                        principalTable: "FlowActions",
                        principalColumns: new[] { "TenantId", "RunId", "NodeId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FlowReceiverEvidence_TenantId_RunId_NodeId",
                table: "FlowReceiverEvidence",
                columns: new[] { "TenantId", "RunId", "NodeId" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION nr_receiver_evidence_immutable_inputs() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF ROW(NEW."TenantId", NEW."RunId", NEW."NodeId", NEW."SchemaVersion",
                           NEW."PreparationJson", NEW."EvidenceFingerprint", NEW."ReceiverIdempotencyKey",
                           NEW."ReceiverFingerprint", NEW."OriginalCreatedAtUtc", NEW."AutomaticReplayUntilUtc")
                       IS DISTINCT FROM
                       ROW(OLD."TenantId", OLD."RunId", OLD."NodeId", OLD."SchemaVersion",
                           OLD."PreparationJson", OLD."EvidenceFingerprint", OLD."ReceiverIdempotencyKey",
                           OLD."ReceiverFingerprint", OLD."OriginalCreatedAtUtc", OLD."AutomaticReplayUntilUtc")
                       OR NEW."RowVersion" <= OLD."RowVersion"
                       OR (OLD."FirstPostAttemptAtUtc" IS NOT NULL AND
                           NEW."FirstPostAttemptAtUtc" IS DISTINCT FROM OLD."FirstPostAttemptAtUtc")
                       OR (OLD."LastPostLeaseFence" IS NOT NULL AND
                           (NEW."LastPostLeaseFence" IS NULL OR NEW."LastPostLeaseFence" < OLD."LastPostLeaseFence"))
                       OR (OLD."FinalReconciliationAttempted" AND NOT NEW."FinalReconciliationAttempted")
                       OR (OLD."FullReceiptJson" IS NOT NULL AND
                           NEW."FullReceiptJson" IS DISTINCT FROM OLD."FullReceiptJson")
                       OR (OLD."MayHaveCommitted" AND NOT NEW."MayHaveCommitted" AND NEW."FullReceiptJson" IS NULL)
                    THEN
                        RAISE EXCEPTION 'receiver-evidence-immutable-input-conflict' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER "TR_FlowReceiverEvidence_ImmutableInputs" BEFORE UPDATE ON "FlowReceiverEvidence"
                    FOR EACH ROW EXECUTE FUNCTION nr_receiver_evidence_immutable_inputs();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER "TR_FlowReceiverEvidence_ImmutableInputs" ON "FlowReceiverEvidence";
                DROP FUNCTION nr_receiver_evidence_immutable_inputs();
                """);
            migrationBuilder.DropTable(
                name: "FlowReceiverEvidence");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_FlowActions_TenantId_RunId_NodeId",
                table: "FlowActions");

            migrationBuilder.DropColumn(
                name: "AuthenticationJson",
                table: "RatelDeskConnectors");

            migrationBuilder.DropColumn(
                name: "ReadinessJson",
                table: "RatelDeskConnectors");

            migrationBuilder.CreateIndex(
                name: "IX_FlowActions_TenantId_RunId",
                table: "FlowActions",
                columns: new[] { "TenantId", "RunId" });
        }
    }
}
