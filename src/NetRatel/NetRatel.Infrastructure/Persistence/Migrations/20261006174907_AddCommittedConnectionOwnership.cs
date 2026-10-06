using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCommittedConnectionOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DispatchOwnerConnectionId",
                table: "JobRunControls",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DispatchOwnerEpoch",
                table: "JobRunControls",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancellationBarrierUntilUtc",
                table: "ClientConnectionEpochs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClientConnectionAdmissions",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionEpoch = table.Column<long>(type: "bigint", nullable: true),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AdmissionExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AuthenticationExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    RetainUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientConnectionAdmissions", x => new { x.TenantId, x.AgentId, x.ConnectionId });
                    table.CheckConstraint("CK_ClientConnectionAdmissions_Epoch", "\"ConnectionEpoch\" IS NULL OR \"ConnectionEpoch\">0");
                    table.CheckConstraint("CK_ClientConnectionAdmissions_Identity", "\"TenantId\" > 0 AND \"AgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ConnectionId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_ClientConnectionAdmissions_Pending", "\"Status\" = 2 OR ( \"ConnectionEpoch\" IS NOT NULL AND \"ConnectionEpoch\">0 AND \"OperationId\" IS NOT NULL AND \"OperationId\"<>'00000000-0000-0000-0000-000000000000'::uuid AND \"PayloadHash\" IS NOT NULL AND \"ReceivedAtUtc\" IS NOT NULL AND \"AdmissionExpiresAtUtc\" IS NOT NULL AND \"AuthenticationExpiresAtUtc\" IS NOT NULL AND \"AdmissionExpiresAtUtc\">\"ReceivedAtUtc\" AND \"AuthenticationExpiresAtUtc\">\"ReceivedAtUtc\" AND \"MetadataJson\" IS NOT NULL)");
                    table.CheckConstraint("CK_ClientConnectionAdmissions_Status", "\"Status\" IN (1,2,3)");
                });

            migrationBuilder.CreateTable(
                name: "ClientConnectionOwners",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionEpoch = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    LastHeartbeatSequence = table.Column<decimal>(type: "numeric(20,0)", nullable: false, defaultValue: 0m),
                    LastReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PresenceExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AuthenticationExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OwnerRevision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    StartOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    AdmissionPayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    AcceptanceClockFloorUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptanceGuardAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientConnectionOwners", x => new { x.TenantId, x.AgentId });
                    table.CheckConstraint("CK_ClientConnectionOwners_Active", "\"AcceptanceGuardAtUtc\" IS NULL OR NOT \"Active\" OR ( \"ConnectionEpoch\" > 0 AND \"ConnectionId\" IS NOT NULL AND \"ConnectionId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"StartOperationId\" IS NOT NULL AND \"AdmissionPayloadHash\" IS NOT NULL AND \"LastReceivedAtUtc\" IS NOT NULL AND \"PresenceExpiresAtUtc\" IS NOT NULL AND \"AuthenticationExpiresAtUtc\" IS NOT NULL AND \"LastHeartbeatSequence\" > 0)");
                    table.CheckConstraint("CK_ClientConnectionOwners_CommittedShape", "(\"ConnectionEpoch\"=0 AND \"ConnectionId\" IS NULL) OR (\"ConnectionEpoch\">0 AND \"ConnectionId\" IS NOT NULL AND \"StartOperationId\" IS NOT NULL AND \"AdmissionPayloadHash\" IS NOT NULL AND \"LastReceivedAtUtc\" IS NOT NULL AND \"PresenceExpiresAtUtc\" IS NOT NULL AND \"AuthenticationExpiresAtUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_ClientConnectionOwners_Epoch", "\"ConnectionEpoch\" >= 0 AND \"OwnerRevision\" >= 0");
                    table.CheckConstraint("CK_ClientConnectionOwners_Identity", "\"TenantId\" > 0 AND \"AgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_ClientConnectionOwners_Sequence", "\"LastHeartbeatSequence\" BETWEEN 0 AND 18446744073709551615");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobRunControls_DispatchOwner",
                table: "JobRunControls",
                sql: "(\"DispatchOwnerConnectionId\" IS NULL AND \"DispatchOwnerEpoch\" IS NULL) OR (\"DispatchOwnerConnectionId\" IS NOT NULL AND \"DispatchOwnerConnectionId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"DispatchOwnerEpoch\" IS NOT NULL AND \"DispatchOwnerEpoch\" > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_ClientConnectionAdmissions_RetainUntilUtc_TenantId_AgentId",
                table: "ClientConnectionAdmissions",
                columns: new[] { "RetainUntilUtc", "TenantId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "UX_ClientConnectionAdmissions_Epoch",
                table: "ClientConnectionAdmissions",
                columns: new[] { "TenantId", "AgentId", "ConnectionEpoch" },
                unique: true,
                filter: "\"ConnectionEpoch\" IS NOT NULL");
            migrationBuilder.Sql(
                """
                -- Reserve fixes the original admission body once. Commit/cancel may change
                -- status and bounded retention, never those signed deadlines or grant fields.
                CREATE FUNCTION public.nr_check_admission_immutable()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public
                AS $immutable$
                BEGIN
                    IF OLD."TenantId" IS DISTINCT FROM NEW."TenantId"
                       OR OLD."AgentId" IS DISTINCT FROM NEW."AgentId"
                       OR OLD."ConnectionId" IS DISTINCT FROM NEW."ConnectionId"
                       OR OLD."ConnectionEpoch" IS DISTINCT FROM NEW."ConnectionEpoch"
                       OR OLD."OperationId" IS DISTINCT FROM NEW."OperationId"
                       OR OLD."PayloadHash" IS DISTINCT FROM NEW."PayloadHash"
                       OR OLD."ReceivedAtUtc" IS DISTINCT FROM NEW."ReceivedAtUtc"
                       OR OLD."AdmissionExpiresAtUtc" IS DISTINCT FROM NEW."AdmissionExpiresAtUtc"
                       OR OLD."AuthenticationExpiresAtUtc" IS DISTINCT FROM NEW."AuthenticationExpiresAtUtc"
                       OR OLD."MetadataJson" IS DISTINCT FROM NEW."MetadataJson"
                       OR (OLD."Status" = 2 AND NEW."Status" <> 2)
                       OR (OLD."Status" = 3 AND NEW."Status" = 1)
                    THEN
                        RAISE EXCEPTION USING ERRCODE = 'NR004',
                            CONSTRAINT = 'CK_ClientConnectionAdmissions_Immutable',
                            MESSAGE = 'ownership-admission-body-is-immutable';
                    END IF;
                    RETURN NEW;
                END;
                $immutable$;
                CREATE TRIGGER nr_admission_body_immutable
                BEFORE UPDATE ON public."ClientConnectionAdmissions"
                FOR EACH ROW EXECUTE FUNCTION public.nr_check_admission_immutable();
                
                -- This is a commit-processing validation, NOT a final WAL-flush deadline.
                -- The exact standalone COMMIT check deliberately matches Npgsql CommitAsync.
                -- It rejects early constraint draining, implicit/autocommit DML, PREPARE,
                -- END/COMMIT WORK, SQL comments, and batched COMMIT commands.
                CREATE FUNCTION public.nr_check_owner_acceptance()
                RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog, public
                AS $guard$
                DECLARE
                    admission public."ClientConnectionAdmissions"%ROWTYPE;
                    owner_now public."ClientConnectionOwners"%ROWTYPE;
                    guard_at timestamp with time zone;
                    stamped integer;
                BEGIN
                    IF pg_catalog.current_query() IS DISTINCT FROM 'COMMIT' THEN
                        RAISE EXCEPTION USING
                            ERRCODE = 'NR003',
                            CONSTRAINT = 'CK_ClientConnectionOwners_AcceptanceGuard',
                            MESSAGE = 'ownership-guard-requires-standalone-commit';
                    END IF;
                
                    -- Identity change is a replacement, never a same-epoch mutation.
                    IF TG_OP = 'UPDATE' AND NEW."ConnectionEpoch" <= OLD."ConnectionEpoch" THEN
                        RAISE EXCEPTION USING
                            ERRCODE = 'NR002',
                            CONSTRAINT = 'CK_ClientConnectionOwners_AcceptanceGuard',
                            MESSAGE = 'ownership-guard-invalid-authority';
                    END IF;
                
                    -- CommitAsync already holds the exact per-client counter/admission and
                    -- owner locks. A durable cancel uses the same counter before changing the
                    -- admission. Cancellation committed first cannot pass this exact status.
                    SELECT * INTO admission FROM public."ClientConnectionAdmissions"
                      WHERE "TenantId" = NEW."TenantId" AND "AgentId" = NEW."AgentId"
                        AND "ConnectionId" = NEW."ConnectionId";
                    IF NOT FOUND THEN
                        RAISE EXCEPTION USING ERRCODE = 'NR002',
                            CONSTRAINT = 'CK_ClientConnectionOwners_AcceptanceGuard',
                            MESSAGE = 'ownership-guard-invalid-authority';
                    END IF;
                    SELECT * INTO owner_now FROM public."ClientConnectionOwners"
                      WHERE "TenantId" = NEW."TenantId" AND "AgentId" = NEW."AgentId";
                    IF NOT FOUND THEN
                        RAISE EXCEPTION USING ERRCODE = 'NR002',
                            CONSTRAINT = 'CK_ClientConnectionOwners_AcceptanceGuard',
                            MESSAGE = 'ownership-guard-invalid-authority';
                    END IF;
                
                    -- A second replacement in this transaction, a changed immutable grant,
                    -- absent proof inputs, or an invented marker rejects the WHOLE transaction.
                    -- IS DISTINCT FROM is intentional: null never satisfies an exact binding.
                    IF admission."Status" IS DISTINCT FROM 3::smallint
                       OR admission."ConnectionEpoch" IS DISTINCT FROM NEW."ConnectionEpoch"
                       OR admission."OperationId" IS DISTINCT FROM NEW."StartOperationId"
                       OR admission."PayloadHash" IS DISTINCT FROM NEW."AdmissionPayloadHash"
                       OR admission."MetadataJson" IS DISTINCT FROM NEW."MetadataJson"
                       OR owner_now."ConnectionEpoch" IS DISTINCT FROM NEW."ConnectionEpoch"
                       OR owner_now."ConnectionId" IS DISTINCT FROM NEW."ConnectionId"
                       OR owner_now."StartOperationId" IS DISTINCT FROM NEW."StartOperationId"
                       OR owner_now."AdmissionPayloadHash" IS DISTINCT FROM NEW."AdmissionPayloadHash"
                       OR owner_now."MetadataJson" IS DISTINCT FROM NEW."MetadataJson"
                       OR owner_now."OwnerRevision" IS DISTINCT FROM NEW."OwnerRevision"
                       OR owner_now."LastHeartbeatSequence" IS DISTINCT FROM NEW."LastHeartbeatSequence"
                       OR owner_now."LastReceivedAtUtc" IS DISTINCT FROM NEW."LastReceivedAtUtc"
                       OR owner_now."PresenceExpiresAtUtc" IS DISTINCT FROM NEW."PresenceExpiresAtUtc"
                       OR owner_now."AuthenticationExpiresAtUtc" IS DISTINCT FROM NEW."AuthenticationExpiresAtUtc"
                       OR owner_now."Active" IS DISTINCT FROM TRUE
                       OR owner_now."AcceptanceGuardAtUtc" IS NOT NULL
                       OR NEW."AcceptanceGuardAtUtc" IS NOT NULL
                       OR owner_now."AcceptanceClockFloorUtc" IS NULL
                       OR NEW."AcceptanceClockFloorUtc" IS NULL
                       OR owner_now."AcceptanceClockFloorUtc" < NEW."AcceptanceClockFloorUtc"
                       OR admission."ReceivedAtUtc" IS NULL
                       OR admission."AdmissionExpiresAtUtc" IS NULL
                       OR admission."AuthenticationExpiresAtUtc" IS NULL
                       OR NEW."LastHeartbeatSequence" <= 0
                       OR NEW."LastReceivedAtUtc" < admission."ReceivedAtUtc"
                       OR NEW."LastReceivedAtUtc" >= admission."AdmissionExpiresAtUtc"
                       OR NEW."AuthenticationExpiresAtUtc" < admission."AuthenticationExpiresAtUtc"
                    THEN
                        RAISE EXCEPTION USING ERRCODE = 'NR002',
                            CONSTRAINT = 'CK_ClientConnectionOwners_AcceptanceGuard',
                            MESSAGE = 'ownership-guard-invalid-authority';
                    END IF;
                
                    -- Sample after the exact durable-authority reads. CURRENT_TIMESTAMP and
                    -- statement_timestamp are unsuitable: neither advances during a blocked
                    -- transaction/COMMIT. The caller's trusted clock floor cannot extend time.
                    guard_at := GREATEST(pg_catalog.clock_timestamp(), owner_now."AcceptanceClockFloorUtc");
                    IF admission."AdmissionExpiresAtUtc" <= guard_at
                       OR admission."AuthenticationExpiresAtUtc" <= guard_at
                       OR NEW."PresenceExpiresAtUtc" <= guard_at
                       OR NEW."AuthenticationExpiresAtUtc" <= guard_at
                       OR NEW."LastReceivedAtUtc" > guard_at
                    THEN
                        RAISE EXCEPTION USING ERRCODE = 'NR001',
                            CONSTRAINT = 'CK_ClientConnectionOwners_AcceptanceGuard',
                            MESSAGE = 'ownership-admission-expired-at-commit-guard';
                    END IF;
                
                    -- This stamp is transactional evidence, not independently visible
                    -- acceptance. If later commit processing aborts, it rolls back together
                    -- with replacement, predecessor retention and admission status=3.
                    UPDATE public."ClientConnectionOwners" SET "AcceptanceGuardAtUtc" = guard_at
                      WHERE "TenantId" = NEW."TenantId" AND "AgentId" = NEW."AgentId"
                        AND "ConnectionEpoch" = NEW."ConnectionEpoch"
                        AND "ConnectionId" = NEW."ConnectionId"
                        AND "OwnerRevision" = NEW."OwnerRevision"
                        AND "StartOperationId" = NEW."StartOperationId"
                        AND "AdmissionPayloadHash" = NEW."AdmissionPayloadHash"
                        AND "AcceptanceGuardAtUtc" IS NULL;
                    GET DIAGNOSTICS stamped = ROW_COUNT;
                    IF stamped <> 1 THEN
                        RAISE EXCEPTION USING ERRCODE = 'NR002',
                            CONSTRAINT = 'CK_ClientConnectionOwners_AcceptanceGuard',
                            MESSAGE = 'ownership-guard-invalid-authority';
                    END IF;
                    RETURN NULL;
                END;
                $guard$;
                
                -- WHEN only selects a replacement event; ALL time and status checks occur in
                -- the deferred function. Constraint-trigger WHEN runs at the original DML.
                CREATE CONSTRAINT TRIGGER nr_owner_acceptance_guard_insert
                AFTER INSERT ON public."ClientConnectionOwners"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                WHEN (NEW."ConnectionEpoch" > 0)
                EXECUTE FUNCTION public.nr_check_owner_acceptance();
                
                CREATE CONSTRAINT TRIGGER nr_owner_acceptance_guard_update
                AFTER UPDATE ON public."ClientConnectionOwners"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                WHEN (OLD."ConnectionEpoch" IS DISTINCT FROM NEW."ConnectionEpoch"
                   OR OLD."ConnectionId" IS DISTINCT FROM NEW."ConnectionId"
                   OR OLD."StartOperationId" IS DISTINCT FROM NEW."StartOperationId"
                   OR OLD."AdmissionPayloadHash" IS DISTINCT FROM NEW."AdmissionPayloadHash"
                   OR OLD."MetadataJson" IS DISTINCT FROM NEW."MetadataJson")
                EXECUTE FUNCTION public.nr_check_owner_acceptance();
                
                -- Application/database roles are trusted SQL writers; do not grant them the
                -- ability to disable these triggers, alter the function, or replace the schema.
                -- Marker-only UPDATE does not queue either trigger, avoiding recursion.
                -- Same-owner heartbeat/renew/retire keeps the original marker unchanged.
                -- Never backfill this marker from LastIssuedEpoch or cached Services state.
                
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER nr_owner_acceptance_guard_insert ON public."ClientConnectionOwners";
                DROP TRIGGER nr_owner_acceptance_guard_update ON public."ClientConnectionOwners";
                DROP FUNCTION public.nr_check_owner_acceptance();
                DROP TRIGGER nr_admission_body_immutable ON public."ClientConnectionAdmissions";
                DROP FUNCTION public.nr_check_admission_immutable();
                
                """);

            migrationBuilder.DropTable(
                name: "ClientConnectionAdmissions");

            migrationBuilder.DropTable(
                name: "ClientConnectionOwners");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobRunControls_DispatchOwner",
                table: "JobRunControls");

            migrationBuilder.DropColumn(
                name: "DispatchOwnerConnectionId",
                table: "JobRunControls");

            migrationBuilder.DropColumn(
                name: "DispatchOwnerEpoch",
                table: "JobRunControls");

            migrationBuilder.DropColumn(
                name: "CancellationBarrierUntilUtc",
                table: "ClientConnectionEpochs");
        }
    }
}
