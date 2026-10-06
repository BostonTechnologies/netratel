START TRANSACTION;
DROP INDEX "IX_FlowActions_TenantId_RunId";

ALTER TABLE "RatelDeskConnectors" ADD "AuthenticationJson" jsonb;

ALTER TABLE "RatelDeskConnectors" ADD "ReadinessJson" jsonb;

ALTER TABLE "FlowActions" ADD CONSTRAINT "AK_FlowActions_TenantId_RunId_NodeId" UNIQUE ("TenantId", "RunId", "NodeId");

CREATE TABLE "FlowReceiverEvidence" (
    "RunId" uuid NOT NULL,
    "NodeId" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "SchemaVersion" integer NOT NULL,
    "PreparationJson" jsonb NOT NULL,
    "EvidenceFingerprint" character varying(64) NOT NULL,
    "ReceiverIdempotencyKey" character varying(256) NOT NULL,
    "ReceiverFingerprint" character varying(64) NOT NULL,
    "OriginalCreatedAtUtc" timestamp with time zone NOT NULL,
    "AutomaticReplayUntilUtc" timestamp with time zone NOT NULL,
    "MayHaveCommitted" boolean NOT NULL,
    "FirstPostAttemptAtUtc" timestamp with time zone,
    "LastPostLeaseFence" bigint,
    "FinalReconciliationAttempted" boolean NOT NULL,
    "FullReceiptJson" jsonb,
    "RowVersion" bigint NOT NULL,
    CONSTRAINT "PK_FlowReceiverEvidence" PRIMARY KEY ("RunId", "NodeId"),
    CONSTRAINT "CK_FlowReceiverEvidence_V2" CHECK ("SchemaVersion" = 2 AND "TenantId" > 0 AND "RowVersion" > 0 AND jsonb_typeof("PreparationJson") = 'object' AND "AutomaticReplayUntilUtc" > "OriginalCreatedAtUtc" AND "AutomaticReplayUntilUtc" <= "OriginalCreatedAtUtc" + INTERVAL '24 hours' AND ("LastPostLeaseFence" IS NULL OR "LastPostLeaseFence" > 0) AND (NOT "MayHaveCommitted" OR "FirstPostAttemptAtUtc" IS NOT NULL) AND ("FullReceiptJson" IS NULL OR jsonb_typeof("FullReceiptJson") = 'object')),
    CONSTRAINT "FK_FlowReceiverEvidence_FlowActions_TenantId_RunId_NodeId" FOREIGN KEY ("TenantId", "RunId", "NodeId") REFERENCES "FlowActions" ("TenantId", "RunId", "NodeId") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_FlowReceiverEvidence_TenantId_RunId_NodeId" ON "FlowReceiverEvidence" ("TenantId", "RunId", "NodeId");

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

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261006195456_AddFlowReceiverEvidence', '10.0.12');

COMMIT;

