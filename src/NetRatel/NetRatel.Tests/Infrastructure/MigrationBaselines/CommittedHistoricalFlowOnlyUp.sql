START TRANSACTION;
CREATE TABLE "FlowAudits" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "FlowId" uuid NOT NULL,
    "Revision" bigint NOT NULL,
    "ActorId" character varying(256) NOT NULL,
    "Operation" character varying(64) NOT NULL,
    "AtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_FlowAudits" PRIMARY KEY ("Id")
);

CREATE TABLE "FlowDefinitions" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "Name" character varying(128) NOT NULL,
    "Revision" bigint NOT NULL,
    "Enabled" boolean NOT NULL,
    "DraftJson" jsonb NOT NULL,
    "PublishedVersionId" uuid,
    "PublishedVersionNumber" integer NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_FlowDefinitions" PRIMARY KEY ("Id"),
    CONSTRAINT "AK_FlowDefinitions_TenantId_Id" UNIQUE ("TenantId", "Id")
);

CREATE TABLE "FlowRuntimeIdentity" (
    "Id" integer NOT NULL,
    "SourceInstanceId" uuid NOT NULL,
    CONSTRAINT "PK_FlowRuntimeIdentity" PRIMARY KEY ("Id")
);

CREATE TABLE "FlowVersions" (
    "Id" uuid NOT NULL,
    "FlowId" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "VersionNumber" integer NOT NULL,
    "GraphJson" jsonb NOT NULL,
    "ConfigurationHash" character varying(64) NOT NULL,
    "PublishedBy" character varying(256) NOT NULL,
    "PublishedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_FlowVersions" PRIMARY KEY ("Id"),
    CONSTRAINT "AK_FlowVersions_TenantId_Id" UNIQUE ("TenantId", "Id"),
    CONSTRAINT "FK_FlowVersions_FlowDefinitions_TenantId_FlowId" FOREIGN KEY ("TenantId", "FlowId") REFERENCES "FlowDefinitions" ("TenantId", "Id") ON DELETE RESTRICT
);

CREATE TABLE "FlowRuns" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "FlowId" uuid NOT NULL,
    "FlowVersionId" uuid NOT NULL,
    "EventId" uuid NOT NULL,
    "OccurrenceId" uuid NOT NULL,
    "EventJson" jsonb NOT NULL,
    "EventFingerprint" character varying(64) NOT NULL,
    "Status" integer NOT NULL,
    "Fence" bigint NOT NULL,
    "LeaseToken" uuid,
    "LeaseOwner" uuid,
    "LeaseExpiresAtUtc" timestamp with time zone,
    "Attempts" integer NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "CompletedAtUtc" timestamp with time zone,
    "NextAttemptAtUtc" timestamp with time zone,
    "Code" character varying(128),
    CONSTRAINT "PK_FlowRuns" PRIMARY KEY ("Id"),
    CONSTRAINT "AK_FlowRuns_TenantId_Id" UNIQUE ("TenantId", "Id"),
    CONSTRAINT "FK_FlowRuns_FlowVersions_TenantId_FlowVersionId" FOREIGN KEY ("TenantId", "FlowVersionId") REFERENCES "FlowVersions" ("TenantId", "Id") ON DELETE RESTRICT
);

CREATE TABLE "FlowActions" (
    "RunId" uuid NOT NULL,
    "NodeId" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "IdempotencyKey" character varying(200) NOT NULL,
    "Status" integer NOT NULL,
    "DraftJson" jsonb NOT NULL,
    "PreparedJson" jsonb,
    "ConnectorRevision" bigint,
    "SemanticFingerprint" character varying(64),
    "ReceiptJson" jsonb,
    "LeaseFence" bigint NOT NULL,
    "Attempts" integer NOT NULL,
    "NextAttemptAtUtc" timestamp with time zone,
    "Code" character varying(128),
    CONSTRAINT "PK_FlowActions" PRIMARY KEY ("RunId", "NodeId"),
    CONSTRAINT "FK_FlowActions_FlowRuns_TenantId_RunId" FOREIGN KEY ("TenantId", "RunId") REFERENCES "FlowRuns" ("TenantId", "Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_FlowActions_TenantId_IdempotencyKey" ON "FlowActions" ("TenantId", "IdempotencyKey");

CREATE INDEX "IX_FlowActions_TenantId_RunId" ON "FlowActions" ("TenantId", "RunId");

CREATE INDEX "IX_FlowAudits_TenantId_FlowId_AtUtc" ON "FlowAudits" ("TenantId", "FlowId", "AtUtc");

CREATE INDEX "IX_FlowDefinitions_TenantId_UpdatedAtUtc" ON "FlowDefinitions" ("TenantId", "UpdatedAtUtc");

CREATE INDEX "IX_FlowRuns_Status_NextAttemptAtUtc_LeaseExpiresAtUtc" ON "FlowRuns" ("Status", "NextAttemptAtUtc", "LeaseExpiresAtUtc");

CREATE UNIQUE INDEX "IX_FlowRuns_TenantId_EventId_FlowVersionId" ON "FlowRuns" ("TenantId", "EventId", "FlowVersionId");

CREATE INDEX "IX_FlowRuns_TenantId_FlowId_CreatedAtUtc" ON "FlowRuns" ("TenantId", "FlowId", "CreatedAtUtc");

CREATE INDEX "IX_FlowRuns_TenantId_FlowVersionId" ON "FlowRuns" ("TenantId", "FlowVersionId");

CREATE UNIQUE INDEX "IX_FlowVersions_TenantId_FlowId_VersionNumber" ON "FlowVersions" ("TenantId", "FlowId", "VersionNumber");

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

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261002195852_AddDurableFlows', '10.0.12');

COMMIT;

