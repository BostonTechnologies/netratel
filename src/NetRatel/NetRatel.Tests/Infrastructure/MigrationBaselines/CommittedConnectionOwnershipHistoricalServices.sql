CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;
CREATE TABLE "M2MConnectivitySettings" (
    "Id" uuid NOT NULL,
    "Enabled" boolean NOT NULL,
    "RemoteBaseUrl" text,
    "RemoteAudience" text,
    "RemoteSystemName" text,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_M2MConnectivitySettings" PRIMARY KEY ("Id")
);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260224160156_InitialOrchestratorPostgres', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "Agents" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "Name" text,
    "Status" smallint NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "CreatedBy" text,
    "LastSeenUtc" timestamp with time zone,
    CONSTRAINT "PK_Agents" PRIMARY KEY ("Id")
);

CREATE TABLE "EnrollmentCodes" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "Code" text NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL DEFAULT (now()),
    "CreatedBy" text,
    "ValidFromUtc" timestamp with time zone NOT NULL,
    "ValidToUtc" timestamp with time zone NOT NULL,
    "MaxUses" integer,
    "Uses" integer NOT NULL DEFAULT 0,
    "RevokedAtUtc" timestamp with time zone,
    "RevokedBy" text,
    "Notes" text,
    CONSTRAINT "PK_EnrollmentCodes" PRIMARY KEY ("Id")
);

CREATE TABLE "OidcSigningKeys" (
    "Id" uuid NOT NULL,
    "KeyId" text NOT NULL,
    "PrivateKeyPem" text NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "IsActive" boolean NOT NULL,
    CONSTRAINT "PK_OidcSigningKeys" PRIMARY KEY ("Id")
);

CREATE TABLE "AgentCredentials" (
    "Id" uuid NOT NULL,
    "AgentId" uuid NOT NULL,
    "RefreshTokenHash" text NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone,
    "RevokedAtUtc" timestamp with time zone,
    "LastUsedUtc" timestamp with time zone,
    CONSTRAINT "PK_AgentCredentials" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AgentCredentials_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_AgentCredentials_AgentId" ON "AgentCredentials" ("AgentId");

CREATE UNIQUE INDEX "IX_AgentCredentials_RefreshTokenHash" ON "AgentCredentials" ("RefreshTokenHash");

CREATE INDEX "IX_Agents_Status" ON "Agents" ("Status");

CREATE INDEX "IX_Agents_TenantId" ON "Agents" ("TenantId");

CREATE UNIQUE INDEX "IX_EnrollmentCodes_Code" ON "EnrollmentCodes" ("Code");

CREATE INDEX "IX_EnrollmentCodes_TenantId_ValidToUtc" ON "EnrollmentCodes" ("TenantId", "ValidToUtc");

CREATE INDEX "IX_EnrollmentCodes_TenantId_ValidToUtc_Active" ON "EnrollmentCodes" ("TenantId", "ValidToUtc") WHERE "RevokedAtUtc" IS NULL;

CREATE INDEX "IX_OidcSigningKeys_IsActive" ON "OidcSigningKeys" ("IsActive");

CREATE UNIQUE INDEX "IX_OidcSigningKeys_IsActive_True" ON "OidcSigningKeys" ("IsActive") WHERE "IsActive" = TRUE;

CREATE UNIQUE INDEX "IX_OidcSigningKeys_KeyId" ON "OidcSigningKeys" ("KeyId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260224213234_AddAgentAuthBroker', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Agents" ADD "AllowedScopesJson" text;

ALTER TABLE "Agents" ADD "DeviceInfoJson" text;

ALTER TABLE "Agents" ADD "KeyAlgorithm" text NOT NULL DEFAULT 'ecdsa-p256';

ALTER TABLE "Agents" ADD "KeyRegisteredAtUtc" timestamp with time zone;

ALTER TABLE "Agents" ADD "MtlsThumbprint" text;

ALTER TABLE "Agents" ADD "PublicKey" text;

CREATE TABLE "AgentNonceLogs" (
    "Id" uuid NOT NULL,
    "AgentId" uuid NOT NULL,
    "Nonce" text NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AgentNonceLogs" PRIMARY KEY ("Id")
);

CREATE TABLE "AgentRefreshTokens" (
    "Id" uuid NOT NULL,
    "AgentId" uuid NOT NULL,
    "TokenHash" text NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone,
    "RevokedAtUtc" timestamp with time zone,
    "ReplacedByTokenId" uuid,
    "LastUsedUtc" timestamp with time zone,
    CONSTRAINT "PK_AgentRefreshTokens" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AgentRefreshTokens_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_AgentNonceLogs_AgentId_Nonce" ON "AgentNonceLogs" ("AgentId", "Nonce");

CREATE INDEX "IX_AgentNonceLogs_CreatedAtUtc" ON "AgentNonceLogs" ("CreatedAtUtc");

CREATE INDEX "IX_AgentRefreshTokens_AgentId" ON "AgentRefreshTokens" ("AgentId");

CREATE INDEX "IX_AgentRefreshTokens_AgentId_CreatedAtUtc" ON "AgentRefreshTokens" ("AgentId", "CreatedAtUtc");

CREATE UNIQUE INDEX "IX_AgentRefreshTokens_TokenHash" ON "AgentRefreshTokens" ("TokenHash");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260226194753_AddPhase8SecurityHardening', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Agents" ADD COLUMN IF NOT EXISTS "IsEnabled" boolean NOT NULL DEFAULT TRUE;
ALTER TABLE "Agents" ADD COLUMN IF NOT EXISTS "DisabledReason" text;
ALTER TABLE "Agents" ADD COLUMN IF NOT EXISTS "LastTokenIssuedAtUtc" timestamp with time zone;
ALTER TABLE "Agents" ADD COLUMN IF NOT EXISTS "RevokedAtUtc" timestamp with time zone;

UPDATE "Agents"
SET "IsEnabled" = CASE WHEN "Status" = 0 THEN TRUE ELSE FALSE END
WHERE "IsEnabled" IS DISTINCT FROM CASE WHEN "Status" = 0 THEN TRUE ELSE FALSE END;

CREATE INDEX IF NOT EXISTS "IX_Agents_TenantId_IsEnabled"
    ON "Agents" ("TenantId", "IsEnabled");
CREATE INDEX IF NOT EXISTS "IX_Agents_TenantId_CreatedAtUtc"
    ON "Agents" ("TenantId", "CreatedAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260227204349_RepairAgentLifecycleColumns', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "OutboxMessages" (
    "Id" uuid NOT NULL,
    "OccurredUtc" timestamp with time zone NOT NULL,
    "Type" text NOT NULL,
    "PayloadJson" text NOT NULL,
    "Source" text NOT NULL,
    "CorrelationId" text NOT NULL,
    "TenantId" text,
    "EntityId" text,
    "Severity" text,
    "Message" text,
    "Status" text NOT NULL,
    "Attempts" integer NOT NULL,
    "NextAttemptUtc" timestamp with time zone,
    "LockedUntilUtc" timestamp with time zone,
    "LockOwner" text,
    "LastError" text,
    CONSTRAINT "PK_OutboxMessages" PRIMARY KEY ("Id")
);

CREATE TABLE "OutboxProcessedEvents" (
    "EventId" uuid NOT NULL,
    "ConsumerName" text NOT NULL,
    "ProcessedUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_OutboxProcessedEvents" PRIMARY KEY ("EventId", "ConsumerName")
);

CREATE TABLE "OutboxReadReceipts" (
    "EventId" uuid NOT NULL,
    "UserId" text NOT NULL,
    "ReadUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_OutboxReadReceipts" PRIMARY KEY ("EventId", "UserId")
);

CREATE INDEX "IX_OutboxMessages_CorrelationId" ON "OutboxMessages" ("CorrelationId");

CREATE INDEX "IX_OutboxMessages_EntityId" ON "OutboxMessages" ("EntityId");

CREATE INDEX "IX_OutboxMessages_OccurredUtc" ON "OutboxMessages" ("OccurredUtc");

CREATE INDEX "IX_OutboxMessages_Status_NextAttemptUtc_LockedUntilUtc" ON "OutboxMessages" ("Status", "NextAttemptUtc", "LockedUntilUtc");

CREATE INDEX "IX_OutboxMessages_Type" ON "OutboxMessages" ("Type");

CREATE INDEX "IX_OutboxProcessedEvents_ProcessedUtc" ON "OutboxProcessedEvents" ("ProcessedUtc");

CREATE INDEX "IX_OutboxReadReceipts_UserId_ReadUtc" ON "OutboxReadReceipts" ("UserId", "ReadUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260304174023_AddOutboxNotifications', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "Tenants" (
    "Id" integer GENERATED BY DEFAULT AS IDENTITY,
    "Name" text NOT NULL,
    "Description" text,
    "Location" text,
    "Domains" text[] NOT NULL,
    "ContactPerson" text,
    "ContactEmail" text,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Tenants" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_Tenants_CreatedAtUtc" ON "Tenants" ("CreatedAtUtc");

CREATE UNIQUE INDEX "IX_Tenants_Name" ON "Tenants" ("Name");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260307142009_AddTenantsTable', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "Scripts" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "Name" text NOT NULL,
    "FolderPath" text NOT NULL,
    "Description" text NOT NULL,
    "Content" text NOT NULL,
    "ManifestJson" text,
    "ScriptType" text NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Scripts" PRIMARY KEY ("Id")
);

CREATE TABLE "ScriptParameters" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "ScriptId" bigint NOT NULL,
    "Name" text NOT NULL,
    "Type" text NOT NULL,
    "Required" boolean NOT NULL,
    "Default" text,
    "Description" text,
    "OptionsJson" text,
    CONSTRAINT "PK_ScriptParameters" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_ScriptParameters_Scripts_ScriptId" FOREIGN KEY ("ScriptId") REFERENCES "Scripts" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_ScriptParameters_ScriptId" ON "ScriptParameters" ("ScriptId");

CREATE INDEX "IX_Scripts_CreatedAtUtc" ON "Scripts" ("CreatedAtUtc");

CREATE INDEX "IX_Scripts_FolderPath_Name" ON "Scripts" ("FolderPath", "Name");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260307144511_AddScriptsTables', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "Secrets" (
    "Id" integer GENERATED BY DEFAULT AS IDENTITY,
    "TenantId" integer,
    "ClientIdentity" text,
    "Value" text NOT NULL,
    "Description" text,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Secrets" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_Secrets_ClientIdentity" ON "Secrets" ("ClientIdentity");

CREATE INDEX "IX_Secrets_CreatedAtUtc" ON "Secrets" ("CreatedAtUtc");

CREATE INDEX "IX_Secrets_TenantId" ON "Secrets" ("TenantId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260307145928_AddSecretsTable', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "Jobs" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "Name" text NOT NULL,
    "FolderPath" text NOT NULL,
    "Description" text,
    "TenantId" integer,
    "ClientIdentity" text NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Jobs" PRIMARY KEY ("Id")
);

CREATE TABLE "JobParameters" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "JobId" bigint NOT NULL,
    "Name" text NOT NULL,
    "Type" text NOT NULL,
    "Required" boolean NOT NULL,
    "DefaultValue" text,
    "Description" text,
    "OptionsJson" text,
    CONSTRAINT "PK_JobParameters" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_JobParameters_Jobs_JobId" FOREIGN KEY ("JobId") REFERENCES "Jobs" ("Id") ON DELETE CASCADE
);

CREATE TABLE "JobSteps" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "JobId" bigint NOT NULL,
    "Ordinal" integer NOT NULL,
    "Type" integer NOT NULL,
    "Runner" text,
    "Command" text,
    "ScriptId" bigint,
    "PayloadJson" text,
    "Enabled" boolean NOT NULL,
    CONSTRAINT "PK_JobSteps" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_JobSteps_Jobs_JobId" FOREIGN KEY ("JobId") REFERENCES "Jobs" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_JobParameters_JobId" ON "JobParameters" ("JobId");

CREATE INDEX "IX_Jobs_ClientIdentity" ON "Jobs" ("ClientIdentity");

CREATE INDEX "IX_Jobs_CreatedAtUtc" ON "Jobs" ("CreatedAtUtc");

CREATE INDEX "IX_Jobs_FolderPath_Name" ON "Jobs" ("FolderPath", "Name");

CREATE INDEX "IX_Jobs_TenantId" ON "Jobs" ("TenantId");

CREATE INDEX "IX_JobSteps_JobId" ON "JobSteps" ("JobId");

CREATE INDEX "IX_JobSteps_JobId_Ordinal" ON "JobSteps" ("JobId", "Ordinal");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260307150553_AddJobDefinitionTables', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "JobRuns" (
    "Id" bigint NOT NULL,
    "JobId" bigint NOT NULL,
    "TenantId" integer,
    "ClientIdentity" text NOT NULL,
    "StartedBy" text NOT NULL,
    "Status" integer NOT NULL,
    "CurrentStepOrdinal" integer NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "StartedAtUtc" timestamp with time zone,
    "CompletedAtUtc" timestamp with time zone,
    "Error" text,
    "InputsJson" text,
    "OptionsJson" text,
    CONSTRAINT "PK_JobRuns" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_JobRuns_Jobs_JobId" FOREIGN KEY ("JobId") REFERENCES "Jobs" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "JobStepRuns" (
    "Id" bigint NOT NULL,
    "JobRunId" bigint NOT NULL,
    "JobStepId" bigint NOT NULL,
    "Status" integer NOT NULL,
    "Ordinal" integer NOT NULL,
    "TaskRequestId" text,
    "Error" text,
    "StartedAtUtc" timestamp with time zone,
    "CompletedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_JobStepRuns" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_JobStepRuns_JobRuns_JobRunId" FOREIGN KEY ("JobRunId") REFERENCES "JobRuns" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_JobStepRuns_JobSteps_JobStepId" FOREIGN KEY ("JobStepId") REFERENCES "JobSteps" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "JobTaskActivities" (
    "Id" bigint NOT NULL,
    "RequestId" text NOT NULL,
    "JobRunId" bigint NOT NULL,
    "JobStepId" bigint NOT NULL,
    "ClientIdentity" text NOT NULL,
    "TenantId" integer,
    "TaskType" text NOT NULL,
    "Status" text NOT NULL,
    "Error" text,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "CompletedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_JobTaskActivities" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_JobTaskActivities_JobRuns_JobRunId" FOREIGN KEY ("JobRunId") REFERENCES "JobRuns" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_JobTaskActivities_JobSteps_JobStepId" FOREIGN KEY ("JobStepId") REFERENCES "JobSteps" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_JobRuns_ClientIdentity" ON "JobRuns" ("ClientIdentity");

CREATE INDEX "IX_JobRuns_CreatedAtUtc" ON "JobRuns" ("CreatedAtUtc");

CREATE INDEX "IX_JobRuns_JobId" ON "JobRuns" ("JobId");

CREATE INDEX "IX_JobRuns_TenantId" ON "JobRuns" ("TenantId");

CREATE INDEX "IX_JobStepRuns_JobRunId" ON "JobStepRuns" ("JobRunId");

CREATE INDEX "IX_JobStepRuns_JobRunId_Ordinal" ON "JobStepRuns" ("JobRunId", "Ordinal");

CREATE INDEX "IX_JobStepRuns_JobStepId" ON "JobStepRuns" ("JobStepId");

CREATE INDEX "IX_JobTaskActivities_CreatedAtUtc" ON "JobTaskActivities" ("CreatedAtUtc");

CREATE INDEX "IX_JobTaskActivities_JobRunId" ON "JobTaskActivities" ("JobRunId");

CREATE INDEX "IX_JobTaskActivities_JobStepId" ON "JobTaskActivities" ("JobStepId");

CREATE UNIQUE INDEX "IX_JobTaskActivities_RequestId" ON "JobTaskActivities" ("RequestId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260307151818_AddJobRunTables', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "Requests" (
    "Id" integer GENERATED BY DEFAULT AS IDENTITY,
    "SourceSystem" text NOT NULL,
    "TargetClientIdentity" text NOT NULL,
    "RundeckJobDefinitionId" text NOT NULL,
    "RundeckExecutionId" text,
    "Status" text NOT NULL,
    "ResultMessage" text,
    "ResultData" text,
    "JobInputs" text,
    "Logs" text[] NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Requests" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_Requests_CreatedAtUtc" ON "Requests" ("CreatedAtUtc");

CREATE INDEX "IX_Requests_Status" ON "Requests" ("Status");

CREATE INDEX "IX_Requests_TargetClientIdentity" ON "Requests" ("TargetClientIdentity");

CREATE INDEX "IX_Requests_UpdatedAtUtc" ON "Requests" ("UpdatedAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260307153208_AddRequestsTable', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "JobTaskLogs" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "RequestId" text NOT NULL,
    "JobTaskActivityId" bigint,
    "ClientIdentity" text NOT NULL,
    "TenantId" integer,
    "Stream" text NOT NULL,
    "Message" text NOT NULL,
    "Sequence" bigint NOT NULL,
    "TimestampUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_JobTaskLogs" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_JobTaskLogs_JobTaskActivities_JobTaskActivityId" FOREIGN KEY ("JobTaskActivityId") REFERENCES "JobTaskActivities" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_JobTaskLogs_JobTaskActivityId" ON "JobTaskLogs" ("JobTaskActivityId");

CREATE INDEX "IX_JobTaskLogs_RequestId" ON "JobTaskLogs" ("RequestId");

CREATE UNIQUE INDEX "IX_JobTaskLogs_RequestId_Sequence_Stream" ON "JobTaskLogs" ("RequestId", "Sequence", "Stream");

CREATE INDEX "IX_JobTaskLogs_TimestampUtc" ON "JobTaskLogs" ("TimestampUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260307173338_AddJobTaskLogsTable', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "JobTaskActivities" ADD "LegacyClientTaskId" integer;

CREATE INDEX "IX_JobTaskActivities_LegacyClientTaskId" ON "JobTaskActivities" ("LegacyClientTaskId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260307175825_AddLegacyClientTaskIdToJobTaskActivity', '10.0.12');

COMMIT;

START TRANSACTION;
DROP INDEX "IX_JobTaskActivities_LegacyClientTaskId";

ALTER TABLE "JobTaskActivities" DROP COLUMN "LegacyClientTaskId";

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260307201907_RemoveLegacyClientTaskIdFromJobTaskActivity', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "JobTaskActivities" ALTER COLUMN "JobStepId" DROP NOT NULL;

ALTER TABLE "JobTaskActivities" ALTER COLUMN "JobRunId" DROP NOT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260308203544_AllowAdHocTaskActivities', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "JobStepRuns" DROP CONSTRAINT "FK_JobStepRuns_JobSteps_JobStepId";

ALTER TABLE "JobStepRuns" ALTER COLUMN "JobStepId" DROP NOT NULL;

ALTER TABLE "JobStepRuns" ADD CONSTRAINT "FK_JobStepRuns_JobSteps_JobStepId" FOREIGN KEY ("JobStepId") REFERENCES "JobSteps" ("Id") ON DELETE SET NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260309135159_AllowNullJobStepRunJobStepId', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Tenants" ADD "AutoUpdate" boolean NOT NULL DEFAULT FALSE;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260523102251_AddTenantAutoUpdate', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Jobs" ADD "OptionsJson" text;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260524143108_AddJobExecutionRuntimePolicy', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "ClientWindowsSessionInventoryRefreshes" (
    "Id" uuid NOT NULL,
    "RefreshRequestId" text NOT NULL,
    "TenantId" integer NOT NULL,
    "ClientIdentity" text NOT NULL,
    "RequesterIdentity" text NOT NULL,
    "RequestedAtUtc" timestamp with time zone NOT NULL,
    "CompletedAtUtc" timestamp with time zone,
    "Completed" boolean NOT NULL,
    "InventorySequence" numeric(20,0),
    "ObservedAtUtc" timestamp with time zone,
    "Source" text NOT NULL,
    "Status" text NOT NULL,
    "Error" text,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ClientWindowsSessionInventoryRefreshes" PRIMARY KEY ("Id")
);

CREATE TABLE "ClientWindowsSessionSnapshots" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "ClientIdentity" text NOT NULL,
    "WindowsSessionId" integer NOT NULL,
    "State" text NOT NULL,
    "Username" text,
    "Domain" text,
    "DisplayLabel" text,
    "UserSidHash" text,
    "IsConsoleSession" boolean NOT NULL,
    "IsActive" boolean NOT NULL,
    "IsConnected" boolean NOT NULL,
    "IsLocked" boolean NOT NULL,
    "IsWinlogon" boolean NOT NULL,
    "IsAssistable" boolean NOT NULL,
    "SessionType" text,
    "Provider" text,
    "HelperConnected" boolean NOT NULL,
    "HelperVersionMatches" boolean NOT NULL,
    "HelperLaunchable" boolean NOT NULL,
    "HelperRepairable" boolean NOT NULL,
    "HelperPid" integer,
    "HelperVersion" text,
    "InventorySequence" numeric(20,0) NOT NULL,
    "ObservedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "Source" text NOT NULL,
    "Stale" boolean NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ClientWindowsSessionSnapshots" PRIMARY KEY ("Id")
);

CREATE TABLE "RemoteSupportTargetSelectionEvents" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "RequesterIdentity" text NOT NULL,
    "ClientIdentity" text NOT NULL,
    "SessionId" text,
    "TargetMode" text NOT NULL,
    "TargetWindowsSessionId" integer,
    "TargetUserSidHash" text,
    "TargetDisplayLabel" text,
    "SelectedProvider" text,
    "InventorySequence" numeric(20,0),
    "RequestedAtUtc" timestamp with time zone NOT NULL,
    "Result" text NOT NULL,
    "FailureReason" text,
    CONSTRAINT "PK_RemoteSupportTargetSelectionEvents" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_ClientWindowsSessionInventoryRefreshes_ClientIdentity_Compl~" ON "ClientWindowsSessionInventoryRefreshes" ("ClientIdentity", "Completed");

CREATE UNIQUE INDEX "IX_ClientWindowsSessionInventoryRefreshes_RefreshRequestId" ON "ClientWindowsSessionInventoryRefreshes" ("RefreshRequestId");

CREATE INDEX "IX_ClientWindowsSessionInventoryRefreshes_TenantId_ClientIdent~" ON "ClientWindowsSessionInventoryRefreshes" ("TenantId", "ClientIdentity", "RequestedAtUtc");

CREATE INDEX "IX_ClientWindowsSessionSnapshots_ClientIdentity_InventorySeque~" ON "ClientWindowsSessionSnapshots" ("ClientIdentity", "InventorySequence");

CREATE UNIQUE INDEX "IX_ClientWindowsSessionSnapshots_ClientIdentity_WindowsSession~" ON "ClientWindowsSessionSnapshots" ("ClientIdentity", "WindowsSessionId");

CREATE INDEX "IX_ClientWindowsSessionSnapshots_ExpiresAtUtc" ON "ClientWindowsSessionSnapshots" ("ExpiresAtUtc");

CREATE INDEX "IX_ClientWindowsSessionSnapshots_TenantId_ClientIdentity" ON "ClientWindowsSessionSnapshots" ("TenantId", "ClientIdentity");

CREATE INDEX "IX_RemoteSupportTargetSelectionEvents_SessionId" ON "RemoteSupportTargetSelectionEvents" ("SessionId");

CREATE INDEX "IX_RemoteSupportTargetSelectionEvents_TenantId_ClientIdentity_~" ON "RemoteSupportTargetSelectionEvents" ("TenantId", "ClientIdentity", "RequestedAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260619101901_AddRemoteSupportWindowsSessionInventoryCache', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "CommandInboxReceipts" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "ClientId" uuid NOT NULL,
    "CommandId" character varying(256) NOT NULL,
    "CorrelationId" character varying(256) NOT NULL,
    "Version" numeric(20,0) NOT NULL,
    "Sequence" numeric(20,0) NOT NULL,
    "FirstReceivedAtUtc" timestamp with time zone NOT NULL,
    "LastReceivedAtUtc" timestamp with time zone NOT NULL,
    "DuplicateCount" bigint NOT NULL,
    CONSTRAINT "PK_CommandInboxReceipts" PRIMARY KEY ("Id")
);

CREATE TABLE "CommandIntentEvents" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "ClientId" uuid NOT NULL,
    "CommandId" character varying(256) NOT NULL,
    "CorrelationId" character varying(256) NOT NULL,
    "RequestTimestamp" timestamp with time zone NOT NULL,
    "StatusTimestamp" timestamp with time zone NOT NULL,
    "Version" numeric(20,0) NOT NULL,
    "Sequence" numeric(20,0) NOT NULL,
    "Status" smallint NOT NULL,
    "Source" character varying(64) NOT NULL,
    "IsAuthoritative" boolean NOT NULL,
    "RecordedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_CommandIntentEvents" PRIMARY KEY ("Id")
);

CREATE TABLE "CommandOutbox" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "ClientId" uuid NOT NULL,
    "CommandId" character varying(256) NOT NULL,
    "CorrelationId" character varying(256) NOT NULL,
    "RequestTimestamp" timestamp with time zone NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "LastObservedAtUtc" timestamp with time zone NOT NULL,
    "TerminalAtUtc" timestamp with time zone,
    "LastAcceptedVersion" numeric(20,0) NOT NULL,
    "LastAcceptedSequence" numeric(20,0) NOT NULL,
    "CurrentStatus" smallint NOT NULL,
    "ObservedDispatchCount" integer NOT NULL,
    "Mode" character varying(32) NOT NULL,
    "IsAuthoritative" boolean NOT NULL,
    CONSTRAINT "PK_CommandOutbox" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_CommandInboxReceipts_FirstReceivedAtUtc" ON "CommandInboxReceipts" ("FirstReceivedAtUtc");

CREATE INDEX "IX_CommandInboxReceipts_TenantId_CorrelationId" ON "CommandInboxReceipts" ("TenantId", "CorrelationId");

CREATE UNIQUE INDEX "UX_CommandInbox_Idempotency" ON "CommandInboxReceipts" ("TenantId", "CommandId", "Version", "Sequence");

CREATE INDEX "IX_CommandIntentEvents_RecordedAtUtc" ON "CommandIntentEvents" ("RecordedAtUtc");

CREATE INDEX "IX_CommandIntentEvents_TenantId_CorrelationId" ON "CommandIntentEvents" ("TenantId", "CorrelationId");

CREATE UNIQUE INDEX "UX_CommandIntentHistory_Order" ON "CommandIntentEvents" ("TenantId", "CommandId", "Version", "Sequence");

CREATE INDEX "IX_CommandOutbox_TenantId_CorrelationId" ON "CommandOutbox" ("TenantId", "CorrelationId");

CREATE INDEX "IX_CommandOutbox_TerminalAtUtc_CreatedAtUtc" ON "CommandOutbox" ("TerminalAtUtc", "CreatedAtUtc");

CREATE UNIQUE INDEX "UX_CommandOutbox_Command" ON "CommandOutbox" ("TenantId", "CommandId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260807102017_AddCommandPersistenceFoundation', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "JobShadowObservations" (
    "Id" uuid NOT NULL,
    "SourceSystem" character varying(64) NOT NULL,
    "SourceEventId" bigint NOT NULL,
    "JobRunId" numeric(20,0) NOT NULL,
    "JobId" numeric(20,0) NOT NULL,
    "TenantId" integer,
    "ClientIdentity" character varying(256) NOT NULL,
    "Kind" smallint NOT NULL,
    "StartedBy" character varying(256),
    "RunStatus" smallint,
    "CurrentStepOrdinal" integer,
    "RunCreatedAtUtc" timestamp with time zone,
    "JobStepRunId" numeric(20,0),
    "JobStepId" numeric(20,0),
    "StepStatus" smallint,
    "StepOrdinal" integer,
    "TaskRequestId" character varying(256),
    "CommandCorrelationStatus" smallint NOT NULL,
    "CorrelatedCommandStatus" smallint,
    "StartedAtUtc" timestamp with time zone,
    "CompletedAtUtc" timestamp with time zone,
    "ObservedAtUtc" timestamp with time zone NOT NULL,
    "IsAuthoritative" boolean NOT NULL,
    "RecordedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_JobShadowObservations" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_JobShadowObservations_JobRunId_SourceEventId" ON "JobShadowObservations" ("JobRunId", "SourceEventId");

CREATE INDEX "IX_JobShadowObservations_RecordedAtUtc" ON "JobShadowObservations" ("RecordedAtUtc");

CREATE INDEX "IX_JobShadowObservations_TenantId_TaskRequestId" ON "JobShadowObservations" ("TenantId", "TaskRequestId");

CREATE UNIQUE INDEX "UX_JobShadowObservation_Source" ON "JobShadowObservations" ("SourceSystem", "SourceEventId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260807105643_AddJobShadowObservations', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "PrimaryClientAgentBindings" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid,
    "EnrollmentCodeId" uuid,
    "PrimaryClientIdentity" text NOT NULL,
    "Status" smallint NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "CreatedBy" text NOT NULL,
    "BoundAtUtc" timestamp with time zone,
    "BoundBy" text,
    "RevokedAtUtc" timestamp with time zone,
    "RevokedBy" text,
    "BindingSource" text NOT NULL,
    "Notes" text,
    "Version" bigint NOT NULL,
    CONSTRAINT "PK_PrimaryClientAgentBindings" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_PrimaryClientAgentBindings_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_PrimaryClientAgentBindings_EnrollmentCodes_EnrollmentCodeId" FOREIGN KEY ("EnrollmentCodeId") REFERENCES "EnrollmentCodes" ("Id") ON DELETE RESTRICT
);

CREATE UNIQUE INDEX "IX_PrimaryClientAgentBindings_AgentId" ON "PrimaryClientAgentBindings" ("AgentId") WHERE "AgentId" IS NOT NULL AND "Status" <> 3;

CREATE UNIQUE INDEX "IX_PrimaryClientAgentBindings_EnrollmentCodeId" ON "PrimaryClientAgentBindings" ("EnrollmentCodeId");

CREATE UNIQUE INDEX "IX_PrimaryClientAgentBindings_TenantId_PrimaryClientIdentity" ON "PrimaryClientAgentBindings" ("TenantId", "PrimaryClientIdentity") WHERE "Status" <> 3;

CREATE INDEX "IX_PrimaryClientAgentBindings_TenantId_Status_CreatedAtUtc" ON "PrimaryClientAgentBindings" ("TenantId", "Status", "CreatedAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260809064936_AddPrimaryClientAgentBindings', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Requests" ADD "TargetAgentId" uuid;

ALTER TABLE "Requests" ADD "TargetTenantId" integer;

ALTER TABLE "JobTaskActivities" ADD "AgentId" uuid;

ALTER TABLE "Jobs" ADD "AgentId" uuid;

ALTER TABLE "JobRuns" ADD "AgentId" uuid;

CREATE INDEX "IX_Requests_TargetTenantId_TargetAgentId" ON "Requests" ("TargetTenantId", "TargetAgentId");

CREATE INDEX "IX_JobTaskActivities_TenantId_AgentId_CreatedAtUtc" ON "JobTaskActivities" ("TenantId", "AgentId", "CreatedAtUtc");

CREATE INDEX "IX_Jobs_TenantId_AgentId" ON "Jobs" ("TenantId", "AgentId");

CREATE INDEX "IX_JobRuns_TenantId_AgentId_CreatedAtUtc" ON "JobRuns" ("TenantId", "AgentId", "CreatedAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260815123451_AddAkkaOrchestrationTargets', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX IF NOT EXISTS "IX_Tenants_GlobalSearch_Name_trgm" ON "Tenants" USING gin ("Name" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Tenants_GlobalSearch_Description_trgm" ON "Tenants" USING gin ("Description" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Tenants_GlobalSearch_Location_trgm" ON "Tenants" USING gin ("Location" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Tenants_GlobalSearch_ContactPerson_trgm" ON "Tenants" USING gin ("ContactPerson" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Tenants_GlobalSearch_ContactEmail_trgm" ON "Tenants" USING gin ("ContactEmail" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Scripts_GlobalSearch_Name_trgm" ON "Scripts" USING gin ("Name" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Scripts_GlobalSearch_FolderPath_trgm" ON "Scripts" USING gin ("FolderPath" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Scripts_GlobalSearch_Description_trgm" ON "Scripts" USING gin ("Description" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Scripts_GlobalSearch_ScriptType_trgm" ON "Scripts" USING gin ("ScriptType" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Jobs_GlobalSearch_Name_trgm" ON "Jobs" USING gin ("Name" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Jobs_GlobalSearch_FolderPath_trgm" ON "Jobs" USING gin ("FolderPath" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Jobs_GlobalSearch_Description_trgm" ON "Jobs" USING gin ("Description" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Jobs_GlobalSearch_ClientIdentity_trgm" ON "Jobs" USING gin ("ClientIdentity" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Requests_GlobalSearch_SourceSystem_trgm" ON "Requests" USING gin ("SourceSystem" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Requests_GlobalSearch_TargetClientIdentity_trgm" ON "Requests" USING gin ("TargetClientIdentity" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Requests_GlobalSearch_RundeckJobDefinitionId_trgm" ON "Requests" USING gin ("RundeckJobDefinitionId" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Requests_GlobalSearch_RundeckExecutionId_trgm" ON "Requests" USING gin ("RundeckExecutionId" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Requests_GlobalSearch_Status_trgm" ON "Requests" USING gin ("Status" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_Requests_GlobalSearch_ResultMessage_trgm" ON "Requests" USING gin ("ResultMessage" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_JobTaskActivities_GlobalSearch_RequestId_trgm" ON "JobTaskActivities" USING gin ("RequestId" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_JobTaskActivities_GlobalSearch_TaskType_trgm" ON "JobTaskActivities" USING gin ("TaskType" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_JobTaskActivities_GlobalSearch_Status_trgm" ON "JobTaskActivities" USING gin ("Status" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_JobTaskActivities_GlobalSearch_Error_trgm" ON "JobTaskActivities" USING gin ("Error" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS "IX_JobTaskActivities_GlobalSearch_ClientIdentity_trgm" ON "JobTaskActivities" USING gin ("ClientIdentity" gin_trgm_ops);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260815141614_RepairGlobalSearchIndexes', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX "IX_Agents_GlobalSearch_DeviceInfoJson_trgm" ON "Agents" USING gin ("DeviceInfoJson" gin_trgm_ops);

CREATE INDEX "IX_Agents_GlobalSearch_Name_trgm" ON "Agents" USING gin ("Name" gin_trgm_ops);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260817100237_AddAgentGlobalSearchIndexes', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Agents" ADD "PublicKeyFingerprint" text;

ALTER TABLE "Agents" ADD "SupersededAtUtc" timestamp with time zone;

ALTER TABLE "Agents" ADD "SupersededByAgentId" uuid;

ALTER TABLE "AgentRefreshTokens" ADD "RecoveryUsedAtUtc" timestamp with time zone;

UPDATE "Agents"
SET "PublicKeyFingerprint" = encode(sha256(decode(trim("PublicKey"), 'base64')), 'hex')
WHERE "PublicKey" IS NOT NULL
  AND trim("PublicKey") <> ''
  AND lower("KeyAlgorithm") = 'ecdsa-p256';

CREATE TEMP TABLE "AgentIdentityConsolidation" ON COMMIT DROP AS
WITH ranked AS (
    SELECT
        agent."Id" AS "AgentId",
        agent."TenantId",
        first_value(agent."Id") OVER (
            PARTITION BY agent."TenantId", agent."PublicKeyFingerprint"
            ORDER BY
                agent."IsEnabled" DESC,
                (agent."Status" = 0) DESC,
                EXISTS (
                    SELECT 1
                    FROM "AgentRefreshTokens" refresh
                    WHERE refresh."AgentId" = agent."Id"
                      AND refresh."RevokedAtUtc" IS NULL
                      AND (refresh."ExpiresAtUtc" IS NULL OR refresh."ExpiresAtUtc" > now())) DESC,
                agent."LastTokenIssuedAtUtc" DESC NULLS LAST,
                agent."LastSeenUtc" DESC NULLS LAST,
                agent."CreatedAtUtc" DESC,
                agent."Id"
        ) AS "CanonicalAgentId",
        count(*) OVER (
            PARTITION BY agent."TenantId", agent."PublicKeyFingerprint") AS "IdentityCount"
    FROM "Agents" agent
    WHERE agent."PublicKeyFingerprint" IS NOT NULL
      AND agent."SupersededAtUtc" IS NULL
)
SELECT "AgentId", "TenantId", "CanonicalAgentId"
FROM ranked
WHERE "IdentityCount" > 1
  AND "AgentId" <> "CanonicalAgentId";

UPDATE "Jobs" target
SET
    "AgentId" = map."CanonicalAgentId",
    "TenantId" = coalesce(target."TenantId", map."TenantId")
FROM "AgentIdentityConsolidation" map
WHERE target."AgentId" = map."AgentId"
  AND (target."TenantId" IS NULL OR target."TenantId" = map."TenantId");

UPDATE "Requests" target
SET
    "TargetAgentId" = map."CanonicalAgentId",
    "TargetTenantId" = coalesce(target."TargetTenantId", map."TenantId")
FROM "AgentIdentityConsolidation" map
WHERE target."TargetAgentId" = map."AgentId"
  AND (target."TargetTenantId" IS NULL OR target."TargetTenantId" = map."TenantId");

UPDATE "JobRuns" target
SET
    "AgentId" = map."CanonicalAgentId",
    "TenantId" = coalesce(target."TenantId", map."TenantId")
FROM "AgentIdentityConsolidation" map
WHERE target."AgentId" = map."AgentId"
  AND (target."TenantId" IS NULL OR target."TenantId" = map."TenantId");

UPDATE "JobTaskActivities" target
SET
    "AgentId" = map."CanonicalAgentId",
    "TenantId" = coalesce(target."TenantId", map."TenantId")
FROM "AgentIdentityConsolidation" map
WHERE target."AgentId" = map."AgentId"
  AND (target."TenantId" IS NULL OR target."TenantId" = map."TenantId");

CREATE TEMP TABLE "AgentIdentityBindingKeep" ON COMMIT DROP AS
SELECT DISTINCT ON (map."CanonicalAgentId")
    binding."Id" AS "BindingId",
    map."CanonicalAgentId"
FROM "AgentIdentityConsolidation" map
JOIN "PrimaryClientAgentBindings" binding
  ON binding."TenantId" = map."TenantId"
 AND binding."AgentId" IN (map."AgentId", map."CanonicalAgentId")
 AND binding."Status" <> 3
ORDER BY
    map."CanonicalAgentId",
    (binding."AgentId" = map."CanonicalAgentId") DESC,
    (binding."Status" = 1) DESC,
    binding."BoundAtUtc" DESC NULLS LAST,
    binding."CreatedAtUtc" DESC,
    binding."Id";

UPDATE "PrimaryClientAgentBindings" binding
SET
    "Status" = 3,
    "RevokedAtUtc" = now(),
    "RevokedBy" = 'agent-identity-migration',
    "Notes" = concat_ws(E'\n', nullif(binding."Notes", ''), 'Revoked while consolidating an exact cryptographic Agent identity match.'),
    "Version" = binding."Version" + 1
FROM "AgentIdentityConsolidation" map
WHERE binding."TenantId" = map."TenantId"
  AND binding."AgentId" = map."AgentId"
  AND binding."Status" <> 3
  AND NOT EXISTS (
      SELECT 1
      FROM "AgentIdentityBindingKeep" keep
      WHERE keep."BindingId" = binding."Id");

UPDATE "PrimaryClientAgentBindings" binding
SET
    "AgentId" = keep."CanonicalAgentId",
    "BoundBy" = 'agent-identity-migration',
    "Notes" = concat_ws(E'\n', nullif(binding."Notes", ''), 'Moved to the canonical Agent after an exact cryptographic identity match.'),
    "Version" = binding."Version" + 1
FROM "AgentIdentityBindingKeep" keep
WHERE binding."Id" = keep."BindingId"
  AND binding."AgentId" <> keep."CanonicalAgentId";

UPDATE "AgentCredentials" credential
SET "RevokedAtUtc" = coalesce(credential."RevokedAtUtc", now())
FROM "AgentIdentityConsolidation" map
WHERE credential."AgentId" = map."AgentId";

UPDATE "AgentRefreshTokens" refresh
SET "RevokedAtUtc" = coalesce(refresh."RevokedAtUtc", now())
FROM "AgentIdentityConsolidation" map
WHERE refresh."AgentId" = map."AgentId";

UPDATE "Agents" agent
SET
    "IsEnabled" = false,
    "Status" = 1,
    "DisabledReason" = 'Superseded after exact cryptographic installation identity match.',
    "RevokedAtUtc" = coalesce(agent."RevokedAtUtc", now()),
    "SupersededByAgentId" = map."CanonicalAgentId",
    "SupersededAtUtc" = now()
FROM "AgentIdentityConsolidation" map
WHERE agent."Id" = map."AgentId";

INSERT INTO "OutboxMessages" (
    "Id", "OccurredUtc", "Type", "PayloadJson", "Source", "CorrelationId",
    "TenantId", "EntityId", "Severity", "Message", "Status", "Attempts", "NextAttemptUtc")
SELECT
    md5('agent-superseded:' || map."AgentId"::text)::uuid,
    now(),
    'DomainEvent.Orchestration.Agent.Superseded',
    jsonb_build_object(
        'agentId', map."AgentId",
        'tenantId', map."TenantId",
        'supersededByAgentId', map."CanonicalAgentId",
        'actor', 'agent-identity-migration')::text,
    'Agent',
    'netratel-agent-' || replace(map."CanonicalAgentId"::text, '-', ''),
    map."TenantId"::text,
    map."AgentId"::text,
    'Info',
    'Agent superseded after exact cryptographic installation identity match.',
    'Pending',
    0,
    now()
FROM "AgentIdentityConsolidation" map;

CREATE INDEX "IX_Agents_SupersededByAgentId" ON "Agents" ("SupersededByAgentId");

CREATE UNIQUE INDEX "IX_Agents_TenantId_PublicKeyFingerprint" ON "Agents" ("TenantId", "PublicKeyFingerprint") WHERE "PublicKeyFingerprint" IS NOT NULL AND "SupersededAtUtc" IS NULL;

ALTER TABLE "Agents" ADD CONSTRAINT "FK_Agents_Agents_SupersededByAgentId" FOREIGN KEY ("SupersededByAgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260817121734_CanonicalizeAgentInstallationIdentity', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "AgentClientUpdateStates" (
    "AgentId" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "SuspendedAtUtc" timestamp with time zone,
    "SuspensionReason" character varying(256),
    "SuspensionAttemptId" uuid,
    "SuppressedReleaseId" uuid,
    "PolicyRevision" bigint NOT NULL,
    "ResumedAtUtc" timestamp with time zone,
    "ResumedBy" text,
    CONSTRAINT "PK_AgentClientUpdateStates" PRIMARY KEY ("AgentId"),
    CONSTRAINT "FK_AgentClientUpdateStates_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE CASCADE
);

CREATE TABLE "ClientUpdateCatalogRevision" (
    "Id" integer NOT NULL,
    "Revision" bigint NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ClientUpdateCatalogRevision" PRIMARY KEY ("Id")
);

CREATE TABLE "ClientUpdateReleases" (
    "Id" integer GENERATED BY DEFAULT AS IDENTITY,
    "PublicId" uuid NOT NULL,
    "Revision" bigint NOT NULL,
    "RuntimeId" character varying(32) NOT NULL,
    "Version" character varying(64) NOT NULL,
    "Channel" character varying(16) NOT NULL,
    "ArtifactKey" character varying(512) NOT NULL,
    "Sha256" character varying(64) NOT NULL,
    "SizeBytes" bigint NOT NULL,
    "ManifestJson" jsonb NOT NULL,
    "Enabled" boolean NOT NULL,
    "PublishedAtUtc" timestamp with time zone NOT NULL,
    "PublishedBy" text,
    "DisabledAtUtc" timestamp with time zone,
    "DisabledBy" text,
    CONSTRAINT "PK_ClientUpdateReleases" PRIMARY KEY ("Id")
);

CREATE TABLE "ClientUpdateAttempts" (
    "Id" integer GENERATED BY DEFAULT AS IDENTITY,
    "PublicId" uuid NOT NULL,
    "ReleaseId" integer NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "FromVersion" character varying(64) NOT NULL,
    "TargetVersion" character varying(64) NOT NULL,
    "RuntimeId" character varying(32) NOT NULL,
    "State" smallint NOT NULL,
    "AdmissionNonceHash" character varying(64) NOT NULL,
    "GatewayConnectionId" uuid,
    "GatewayConnectionEpoch" bigint,
    "ConfirmationId" uuid,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "ReadmittedAtUtc" timestamp with time zone,
    "ConfirmedAtUtc" timestamp with time zone,
    "FailureCode" character varying(64),
    "Message" text,
    CONSTRAINT "PK_ClientUpdateAttempts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_ClientUpdateAttempts_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ClientUpdateAttempts_ClientUpdateReleases_ReleaseId" FOREIGN KEY ("ReleaseId") REFERENCES "ClientUpdateReleases" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_AgentClientUpdateStates_TenantId_SuspendedAtUtc" ON "AgentClientUpdateStates" ("TenantId", "SuspendedAtUtc");

CREATE UNIQUE INDEX "IX_ClientUpdateAttempts_AgentId_ReleaseId" ON "ClientUpdateAttempts" ("AgentId", "ReleaseId");

CREATE UNIQUE INDEX "IX_ClientUpdateAttempts_PublicId" ON "ClientUpdateAttempts" ("PublicId");

CREATE INDEX "IX_ClientUpdateAttempts_ReleaseId" ON "ClientUpdateAttempts" ("ReleaseId");

CREATE INDEX "IX_ClientUpdateAttempts_TenantId_AgentId_UpdatedAtUtc" ON "ClientUpdateAttempts" ("TenantId", "AgentId", "UpdatedAtUtc");

CREATE INDEX "IX_ClientUpdateReleases_Enabled_RuntimeId_Channel_PublishedAtU~" ON "ClientUpdateReleases" ("Enabled", "RuntimeId", "Channel", "PublishedAtUtc");

CREATE UNIQUE INDEX "IX_ClientUpdateReleases_PublicId" ON "ClientUpdateReleases" ("PublicId");

CREATE UNIQUE INDEX "IX_ClientUpdateReleases_Revision" ON "ClientUpdateReleases" ("Revision");

CREATE UNIQUE INDEX "IX_ClientUpdateReleases_RuntimeId_Version" ON "ClientUpdateReleases" ("RuntimeId", "Version");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260818180104_AddAkkaClientUpdates', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "RemoteSupportSessions" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "OpenRequestId" uuid NOT NULL,
    "ContractVersion" integer NOT NULL DEFAULT 1,
    "InitiatingOperatorId" character varying(256) NOT NULL,
    "TargetKind" character varying(32) NOT NULL,
    "TargetWindowsSessionId" integer,
    "TargetUserSidHash" character varying(256),
    "TargetInventorySequence" numeric(20,0),
    "RequestedCapabilitiesJson" text NOT NULL,
    "GrantedCapabilitiesJson" text NOT NULL,
    "State" character varying(64) NOT NULL,
    "LifecycleRevision" numeric(20,0) NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone,
    "TerminalAtUtc" timestamp with time zone,
    "TerminalReasonCode" character varying(128),
    CONSTRAINT "PK_RemoteSupportSessions" PRIMARY KEY ("Id")
);

CREATE TABLE "RemoteSupportAuditEvents" (
    "Id" uuid NOT NULL,
    "RemoteSupportSessionId" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "ContractVersion" integer NOT NULL,
    "AuditSequence" numeric(20,0) NOT NULL,
    "LifecycleRevision" numeric(20,0) NOT NULL,
    "EventType" character varying(128) NOT NULL,
    "ActorKind" character varying(64) NOT NULL,
    "ActorId" character varying(256) NOT NULL,
    "RequestId" uuid,
    "Outcome" character varying(64) NOT NULL,
    "FailureCode" character varying(128),
    "OccurredAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_RemoteSupportAuditEvents" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_RemoteSupportAuditEvents_RemoteSupportSessions_RemoteSuppor~" FOREIGN KEY ("RemoteSupportSessionId") REFERENCES "RemoteSupportSessions" ("Id") ON DELETE RESTRICT
);

CREATE UNIQUE INDEX "IX_RemoteSupportAuditEvents_RemoteSupportSessionId_AuditSequen~" ON "RemoteSupportAuditEvents" ("RemoteSupportSessionId", "AuditSequence");

CREATE INDEX "IX_RemoteSupportAuditEvents_RemoteSupportSessionId_RequestId" ON "RemoteSupportAuditEvents" ("RemoteSupportSessionId", "RequestId");

CREATE INDEX "IX_RemoteSupportAuditEvents_TenantId_AgentId_OccurredAtUtc" ON "RemoteSupportAuditEvents" ("TenantId", "AgentId", "OccurredAtUtc");

CREATE INDEX "IX_RemoteSupportSessions_ExpiresAtUtc" ON "RemoteSupportSessions" ("ExpiresAtUtc");

CREATE INDEX "IX_RemoteSupportSessions_TenantId_AgentId_CreatedAtUtc" ON "RemoteSupportSessions" ("TenantId", "AgentId", "CreatedAtUtc");

CREATE UNIQUE INDEX "IX_RemoteSupportSessions_TenantId_AgentId_OpenRequestId" ON "RemoteSupportSessions" ("TenantId", "AgentId", "OpenRequestId");

CREATE INDEX "IX_RemoteSupportSessions_TenantId_InitiatingOperatorId_Updated~" ON "RemoteSupportSessions" ("TenantId", "InitiatingOperatorId", "UpdatedAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260820171115_AddRemoteSupportV2LifecycleContracts', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Tenants" ADD "AutoUpdateChannel" text NOT NULL DEFAULT '';

ALTER TABLE "Tenants" ADD "AutoUpdateTargetVersion" text;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260823110407_AddTenantApprovedClientRollout', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "DevelopmentOperatorTargetGrants" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "Classification" smallint NOT NULL,
    "AllowedOperations" integer NOT NULL,
    "EvidenceReference" character varying(512) NOT NULL,
    "GrantedBy" character varying(256) NOT NULL,
    "GrantedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "RevokedAtUtc" timestamp with time zone,
    "RevokedBy" character varying(256),
    "RevocationReason" character varying(256),
    CONSTRAINT "PK_DevelopmentOperatorTargetGrants" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_DevelopmentOperatorTargetGrants_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "DevelopmentOperatorAcceptedAudits" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "TargetGrantId" uuid NOT NULL,
    "Operation" smallint NOT NULL,
    "ActorId" character varying(256) NOT NULL,
    "CorrelationId" character varying(256) NOT NULL,
    "OccurredAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_DevelopmentOperatorAcceptedAudits" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_DevelopmentOperatorAcceptedAudits_DevelopmentOperatorTarget~" FOREIGN KEY ("TargetGrantId") REFERENCES "DevelopmentOperatorTargetGrants" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_DevelopmentOperatorAcceptedAudits_TargetGrantId_OccurredAtU~" ON "DevelopmentOperatorAcceptedAudits" ("TargetGrantId", "OccurredAtUtc");

CREATE INDEX "IX_DevelopmentOperatorAcceptedAudits_TenantId_AgentId_Occurred~" ON "DevelopmentOperatorAcceptedAudits" ("TenantId", "AgentId", "OccurredAtUtc");

CREATE UNIQUE INDEX "IX_DevelopmentOperatorTargetGrants_AgentId_RevokedAtUtc" ON "DevelopmentOperatorTargetGrants" ("AgentId", "RevokedAtUtc") WHERE "RevokedAtUtc" IS NULL;

CREATE INDEX "IX_DevelopmentOperatorTargetGrants_TenantId_AgentId_RevokedAtU~" ON "DevelopmentOperatorTargetGrants" ("TenantId", "AgentId", "RevokedAtUtc", "ExpiresAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260825170134_AddDevelopmentOperatorTargetAuthority', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "DevelopmentOperatorTargetGrants" ADD "FileFixtureRoot" character varying(4096);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260825175518_AddDevelopmentOperatorFileFixtureRoot', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "DevelopmentMcpFileArtifacts" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "TargetGrantId" uuid NOT NULL,
    "FileName" character varying(512) NOT NULL,
    "SizeBytes" bigint NOT NULL,
    "Sha256" character varying(64) NOT NULL,
    "MarkerOwned" boolean NOT NULL,
    "Content" bytea NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "DeletedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_DevelopmentMcpFileArtifacts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_DevelopmentMcpFileArtifacts_DevelopmentOperatorTargetGrants~" FOREIGN KEY ("TargetGrantId") REFERENCES "DevelopmentOperatorTargetGrants" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_DevelopmentMcpFileArtifacts_TargetGrantId_CreatedAtUtc" ON "DevelopmentMcpFileArtifacts" ("TargetGrantId", "CreatedAtUtc");

CREATE INDEX "IX_DevelopmentMcpFileArtifacts_TenantId_AgentId_ExpiresAtUtc" ON "DevelopmentMcpFileArtifacts" ("TenantId", "AgentId", "ExpiresAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260825180742_AddDevelopmentMcpFileArtifacts', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "DevelopmentMcpScripts" (
    "Id" uuid NOT NULL,
    "ScriptId" bigint NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "TargetGrantId" uuid NOT NULL,
    "Marker" character varying(128) NOT NULL,
    "Shell" character varying(32) NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "DeletedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_DevelopmentMcpScripts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_DevelopmentMcpScripts_DevelopmentOperatorTargetGrants_Targe~" FOREIGN KEY ("TargetGrantId") REFERENCES "DevelopmentOperatorTargetGrants" ("Id") ON DELETE RESTRICT
);

CREATE UNIQUE INDEX "IX_DevelopmentMcpScripts_ScriptId" ON "DevelopmentMcpScripts" ("ScriptId");

CREATE INDEX "IX_DevelopmentMcpScripts_TargetGrantId_CreatedAtUtc" ON "DevelopmentMcpScripts" ("TargetGrantId", "CreatedAtUtc");

CREATE INDEX "IX_DevelopmentMcpScripts_TenantId_AgentId_DeletedAtUtc_Created~" ON "DevelopmentMcpScripts" ("TenantId", "AgentId", "DeletedAtUtc", "CreatedAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260825183341_AddDevelopmentMcpMarkerScripts', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "DevelopmentMcpMarkerJobs" (
    "Id" uuid NOT NULL,
    "JobId" bigint NOT NULL,
    "ScriptId" bigint NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "TargetGrantId" uuid NOT NULL,
    "Marker" character varying(128) NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "DeletedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_DevelopmentMcpMarkerJobs" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_DevelopmentMcpMarkerJobs_DevelopmentOperatorTargetGrants_TargetGrantId" FOREIGN KEY ("TargetGrantId") REFERENCES "DevelopmentOperatorTargetGrants" ("Id") ON DELETE RESTRICT
);

CREATE UNIQUE INDEX "IX_DevelopmentMcpMarkerJobs_JobId" ON "DevelopmentMcpMarkerJobs" ("JobId");

CREATE UNIQUE INDEX "IX_DevelopmentMcpMarkerJobs_ScriptId" ON "DevelopmentMcpMarkerJobs" ("ScriptId");

CREATE INDEX "IX_DevelopmentMcpMarkerJobs_TargetGrantId_CreatedAtUtc" ON "DevelopmentMcpMarkerJobs" ("TargetGrantId", "CreatedAtUtc");

CREATE INDEX "IX_DevelopmentMcpMarkerJobs_TenantId_AgentId_DeletedAtUtc_CreatedAtUtc" ON "DevelopmentMcpMarkerJobs" ("TenantId", "AgentId", "DeletedAtUtc", "CreatedAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260826001500_AddDevelopmentMcpMarkerJobs', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "DevelopmentMcpScripts" ADD "ExecutionMode" character varying(32) NOT NULL DEFAULT 'standard';

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260828080023_AddDevelopmentMcpCancellationProbe', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "EnrollmentCodes" ADD "DevelopmentMcpTargetAgentId" uuid;

ALTER TABLE "EnrollmentCodes" ADD "DevelopmentMcpMarker" text;

CREATE INDEX "IX_EnrollmentCodes_TenantId_DevelopmentMcpTargetAgentId_DevelopmentMcpMarker" ON "EnrollmentCodes" ("TenantId", "DevelopmentMcpTargetAgentId", "DevelopmentMcpMarker");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260828170000_AddDevelopmentMcpEnrollmentOwnership', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "McpOperatorPolicies" (
    "Id" uuid NOT NULL,
    "Name" character varying(160) NOT NULL,
    "Environment" smallint NOT NULL,
    "Effect" smallint NOT NULL,
    "Priority" integer NOT NULL,
    "PrincipalSelectorKind" smallint NOT NULL,
    "PrincipalSelectorValue" character varying(256) NOT NULL,
    "TargetSelectorKind" smallint NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid,
    "ClientTag" character varying(128),
    "TargetClassification" smallint,
    "OperationFamily" integer NOT NULL,
    "Operation" character varying(256),
    "ConstraintsJson" jsonb NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "CreatedBy" character varying(256) NOT NULL,
    "ExpiresAtUtc" timestamp with time zone,
    "ReviewByUtc" timestamp with time zone,
    "DisabledAtUtc" timestamp with time zone,
    "DisabledBy" character varying(256),
    "Version" bigint NOT NULL,
    "AuditReference" character varying(512),
    CONSTRAINT "PK_McpOperatorPolicies" PRIMARY KEY ("Id")
);

CREATE TABLE "McpOperatorAcceptedAudits" (
    "Id" uuid NOT NULL,
    "PolicyId" uuid NOT NULL,
    "Environment" smallint NOT NULL,
    "ServicePrincipal" character varying(256) NOT NULL,
    "Subject" character varying(256) NOT NULL,
    "ClientId" character varying(256),
    "TenantId" integer NOT NULL,
    "AgentId" uuid,
    "OperationFamily" integer NOT NULL,
    "Operation" character varying(256) NOT NULL,
    "CorrelationId" character varying(256) NOT NULL,
    "RequestId" character varying(256) NOT NULL,
    "OccurredAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_McpOperatorAcceptedAudits" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorAcceptedAudits_McpOperatorPolicies_PolicyId" FOREIGN KEY ("PolicyId") REFERENCES "McpOperatorPolicies" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_McpOperatorAcceptedAudits_PolicyId_OccurredAtUtc" ON "McpOperatorAcceptedAudits" ("PolicyId", "OccurredAtUtc");

CREATE INDEX "IX_McpOperatorAcceptedAudits_RequestId_OccurredAtUtc" ON "McpOperatorAcceptedAudits" ("RequestId", "OccurredAtUtc");

CREATE INDEX "IX_McpOperatorAcceptedAudits_TenantId_AgentId_OccurredAtUtc" ON "McpOperatorAcceptedAudits" ("TenantId", "AgentId", "OccurredAtUtc");

CREATE INDEX "IX_McpOperatorPolicies_Environment_PrincipalSelectorKind_Princ~" ON "McpOperatorPolicies" ("Environment", "PrincipalSelectorKind", "PrincipalSelectorValue");

CREATE INDEX "IX_McpOperatorPolicies_Environment_TargetSelectorKind_TenantId~" ON "McpOperatorPolicies" ("Environment", "TargetSelectorKind", "TenantId", "AgentId");

CREATE INDEX "IX_McpOperatorPolicies_Environment_TenantId_DisabledAtUtc_Expi~" ON "McpOperatorPolicies" ("Environment", "TenantId", "DisabledAtUtc", "ExpiresAtUtc", "Effect", "Priority");

INSERT INTO "McpOperatorPolicies" (
    "Id", "Name", "Environment", "Effect", "Priority",
    "PrincipalSelectorKind", "PrincipalSelectorValue",
    "TargetSelectorKind", "TenantId", "AgentId", "ClientTag",
    "TargetClassification", "OperationFamily", "Operation",
    "ConstraintsJson", "CreatedAtUtc", "CreatedBy", "ExpiresAtUtc",
    "ReviewByUtc", "DisabledAtUtc", "DisabledBy", "Version", "AuditReference")
SELECT
    md5('legacy-dev-mcp-policy:' || legacy_grant."Id"::text)::uuid,
    left('Legacy Dev target grant ' || legacy_grant."Id"::text, 160),
    1, 2, 0,
    5, 'development-compatibility',
    1, legacy_grant."TenantId", legacy_grant."AgentId", NULL,
    CASE legacy_grant."Classification" WHEN 1 THEN 1 WHEN 2 THEN 2 ELSE NULL END,
    (CASE WHEN (legacy_grant."AllowedOperations" & 1) <> 0 THEN 2816 ELSE 0 END) |
    (CASE WHEN (legacy_grant."AllowedOperations" & 2) <> 0 THEN 1024 ELSE 0 END) |
    (CASE WHEN (legacy_grant."AllowedOperations" & 4) <> 0 THEN 16 ELSE 0 END) |
    (CASE WHEN (legacy_grant."AllowedOperations" & 8) <> 0 THEN 6 ELSE 0 END) |
    (CASE WHEN (legacy_grant."AllowedOperations" & 16) <> 0 THEN 24 ELSE 0 END) |
    (CASE WHEN (legacy_grant."AllowedOperations" & 32) <> 0 THEN 96 ELSE 0 END) |
    (CASE WHEN (legacy_grant."AllowedOperations" & 64) <> 0 THEN 4096 ELSE 0 END) |
    (CASE WHEN (legacy_grant."AllowedOperations" & 128) <> 0 THEN 65536 ELSE 0 END) |
    (CASE WHEN (legacy_grant."AllowedOperations" & 256) <> 0 THEN 131072 ELSE 0 END) |
    (CASE WHEN (legacy_grant."AllowedOperations" & 512) <> 0 THEN 1 ELSE 0 END),
    NULL,
    jsonb_strip_nulls(jsonb_build_object(
        'readRoots', CASE WHEN legacy_grant."FileFixtureRoot" IS NULL THEN NULL ELSE jsonb_build_array(legacy_grant."FileFixtureRoot") END,
        'writeRoots', CASE WHEN legacy_grant."FileFixtureRoot" IS NULL THEN NULL ELSE jsonb_build_array(legacy_grant."FileFixtureRoot") END)),
    legacy_grant."GrantedAtUtc", legacy_grant."GrantedBy", legacy_grant."ExpiresAtUtc",
    NULL, NULL, NULL, 1, 'legacy-dev-grant:' || legacy_grant."Id"::text
FROM "DevelopmentOperatorTargetGrants" AS legacy_grant
WHERE legacy_grant."RevokedAtUtc" IS NULL
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829051150_AddMcpOperatorPolicyFoundation', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "McpOperatorAcceptedAudits" ADD "AuthorizedParty" character varying(256);

ALTER TABLE "McpOperatorAcceptedAudits" ADD "GroupsJson" jsonb NOT NULL DEFAULT '[]';

ALTER TABLE "McpOperatorAcceptedAudits" ADD "McpInstance" character varying(32);

ALTER TABLE "McpOperatorAcceptedAudits" ADD "McpResource" character varying(512);

ALTER TABLE "McpOperatorAcceptedAudits" ADD "RolesJson" jsonb NOT NULL DEFAULT '[]';

ALTER TABLE "McpOperatorAcceptedAudits" ADD "ScopesJson" jsonb NOT NULL DEFAULT '[]';

ALTER TABLE "McpOperatorAcceptedAudits" ADD "Tool" character varying(128);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829062048_AddMcpOperatorDelegatedIdentityAuditFacts', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "McpOperatorTargetProfiles" (
    "AgentId" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "Classification" smallint NOT NULL,
    "TagsJson" jsonb NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedBy" character varying(256) NOT NULL,
    "Version" bigint NOT NULL,
    CONSTRAINT "PK_McpOperatorTargetProfiles" PRIMARY KEY ("AgentId"),
    CONSTRAINT "FK_McpOperatorTargetProfiles_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_McpOperatorTargetProfiles_TenantId_Classification" ON "McpOperatorTargetProfiles" ("TenantId", "Classification");

INSERT INTO "McpOperatorTargetProfiles" (
    "AgentId", "TenantId", "Classification", "TagsJson",
    "UpdatedAtUtc", "UpdatedBy", "Version")
SELECT
    legacy_grant."AgentId",
    legacy_grant."TenantId",
    CASE legacy_grant."Classification"
        WHEN 1 THEN 1
        WHEN 2 THEN 2
        ELSE 3
    END,
    '[]'::jsonb,
    legacy_grant."GrantedAtUtc",
    legacy_grant."GrantedBy",
    1
FROM "DevelopmentOperatorTargetGrants" AS legacy_grant
WHERE legacy_grant."RevokedAtUtc" IS NULL
ON CONFLICT ("AgentId") DO NOTHING;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829063129_AddMcpOperatorTargetProfiles', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "McpOperatorPolicyChangeAudits" (
    "Id" uuid NOT NULL,
    "Action" character varying(64) NOT NULL,
    "PolicyId" uuid,
    "AgentId" uuid,
    "TenantId" integer NOT NULL,
    "ActorId" character varying(256) NOT NULL,
    "Version" bigint NOT NULL,
    "OccurredAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_McpOperatorPolicyChangeAudits" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_McpOperatorPolicyChangeAudits_AgentId_OccurredAtUtc" ON "McpOperatorPolicyChangeAudits" ("AgentId", "OccurredAtUtc");

CREATE INDEX "IX_McpOperatorPolicyChangeAudits_PolicyId_OccurredAtUtc" ON "McpOperatorPolicyChangeAudits" ("PolicyId", "OccurredAtUtc");

CREATE INDEX "IX_McpOperatorPolicyChangeAudits_TenantId_OccurredAtUtc" ON "McpOperatorPolicyChangeAudits" ("TenantId", "OccurredAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829064731_AddMcpOperatorPolicyChangeAudits', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "McpOperatorConfirmationPlans" (
    "Id" uuid NOT NULL,
    "TokenHash" character varying(64) NOT NULL,
    "Environment" smallint NOT NULL,
    "Subject" character varying(256) NOT NULL,
    "ClientId" character varying(256) NOT NULL,
    "McpResource" character varying(512),
    "McpInstance" character varying(32),
    "TenantId" integer NOT NULL,
    "AgentId" uuid,
    "TargetSetDigest" character varying(64) NOT NULL,
    "PolicyId" uuid NOT NULL,
    "PolicyVersion" bigint NOT NULL,
    "OperationFamily" integer NOT NULL,
    "Operation" character varying(256) NOT NULL,
    "ConfirmationClass" smallint NOT NULL,
    "PayloadHash" character varying(64) NOT NULL,
    "IdempotencyKey" character varying(128) NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "ConsumedAtUtc" timestamp with time zone,
    "ConsumedIdempotencyId" uuid,
    "Version" bigint NOT NULL,
    CONSTRAINT "PK_McpOperatorConfirmationPlans" PRIMARY KEY ("Id")
);

CREATE TABLE "McpOperatorIdempotencyRecords" (
    "Id" uuid NOT NULL,
    "Environment" smallint NOT NULL,
    "Subject" character varying(256) NOT NULL,
    "ClientId" character varying(256) NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid,
    "TargetSetDigest" character varying(64) NOT NULL,
    "PolicyId" uuid NOT NULL,
    "PolicyVersion" bigint NOT NULL,
    "OperationFamily" integer NOT NULL,
    "Operation" character varying(256) NOT NULL,
    "IdempotencyKey" character varying(128) NOT NULL,
    "PayloadHash" character varying(64) NOT NULL,
    "Outcome" smallint NOT NULL,
    "ResultReference" character varying(512),
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "CompletedAtUtc" timestamp with time zone,
    "Version" bigint NOT NULL,
    CONSTRAINT "PK_McpOperatorIdempotencyRecords" PRIMARY KEY ("Id")
);

CREATE INDEX "IX_McpOperatorConfirmationPlans_ExpiresAtUtc_ConsumedAtUtc" ON "McpOperatorConfirmationPlans" ("ExpiresAtUtc", "ConsumedAtUtc");

CREATE INDEX "IX_McpOperatorConfirmationPlans_TenantId_AgentId_CreatedAtUtc" ON "McpOperatorConfirmationPlans" ("TenantId", "AgentId", "CreatedAtUtc");

CREATE UNIQUE INDEX "IX_McpOperatorConfirmationPlans_TokenHash" ON "McpOperatorConfirmationPlans" ("TokenHash");

CREATE UNIQUE INDEX "IX_McpOperatorIdempotencyRecords_Environment_Subject_ClientId_~" ON "McpOperatorIdempotencyRecords" ("Environment", "Subject", "ClientId", "TenantId", "OperationFamily", "Operation", "IdempotencyKey");

CREATE INDEX "IX_McpOperatorIdempotencyRecords_TenantId_AgentId_CreatedAtUtc" ON "McpOperatorIdempotencyRecords" ("TenantId", "AgentId", "CreatedAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829070648_AddMcpOperatorConfirmationAndIdempotency', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "McpOperatorPolicies" ADD "LifecycleState" smallint NOT NULL DEFAULT 1;

UPDATE "McpOperatorPolicies" SET "LifecycleState" = 2 WHERE "DisabledAtUtc" IS NOT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829123000_AddMcpOperatorPolicyLifecycleState', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "McpOperatorTerminalSessions" (
    "Id" uuid NOT NULL,
    "SessionId" character varying(64) NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "Generation" numeric(20,0) NOT NULL,
    "Subject" character varying(256) NOT NULL,
    "ClientId" character varying(256) NOT NULL,
    "McpResource" character varying(512) NOT NULL,
    "McpInstance" character varying(32) NOT NULL,
    "PolicyId" uuid NOT NULL,
    "PolicyVersion" bigint NOT NULL,
    "AcceptedAuditId" uuid NOT NULL,
    "IdempotencyId" uuid,
    "ShellType" character varying(32) NOT NULL,
    "WorkingDirectory" character varying(4096),
    "Columns" integer NOT NULL,
    "Rows" integer NOT NULL,
    "EffectiveConstraintsJson" jsonb NOT NULL,
    "State" smallint NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "LastActivityAtUtc" timestamp with time zone NOT NULL,
    "IdleExpiresAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "CloseRequestedAtUtc" timestamp with time zone,
    "CloseReason" character varying(128),
    "ClosedAtUtc" timestamp with time zone,
    "FailureCode" character varying(64),
    "Version" bigint NOT NULL,
    CONSTRAINT "PK_McpOperatorTerminalSessions" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorTerminalSessions_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorTerminalSessions_McpOperatorAcceptedAudits_AcceptedAuditId" FOREIGN KEY ("AcceptedAuditId") REFERENCES "McpOperatorAcceptedAudits" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorTerminalSessions_McpOperatorIdempotencyRecords_IdempotencyId" FOREIGN KEY ("IdempotencyId") REFERENCES "McpOperatorIdempotencyRecords" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorTerminalSessions_McpOperatorPolicies_PolicyId" FOREIGN KEY ("PolicyId") REFERENCES "McpOperatorPolicies" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "McpOperatorTerminalSessionAudits" (
    "Id" uuid NOT NULL,
    "SessionRecordId" uuid NOT NULL,
    "State" smallint NOT NULL,
    "Action" character varying(64) NOT NULL,
    "Reason" character varying(128),
    "OccurredAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_McpOperatorTerminalSessionAudits" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorTerminalSessionAudits_McpOperatorTerminalSessions_SessionRecordId" FOREIGN KEY ("SessionRecordId") REFERENCES "McpOperatorTerminalSessions" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_McpOperatorTerminalSessionAudits_SessionRecordId_OccurredAtUtc" ON "McpOperatorTerminalSessionAudits" ("SessionRecordId", "OccurredAtUtc");

CREATE INDEX "IX_McpOperatorTerminalSessions_AcceptedAuditId" ON "McpOperatorTerminalSessions" ("AcceptedAuditId");

CREATE INDEX "IX_McpOperatorTerminalSessions_AgentId" ON "McpOperatorTerminalSessions" ("AgentId");

CREATE UNIQUE INDEX "IX_McpOperatorTerminalSessions_IdempotencyId" ON "McpOperatorTerminalSessions" ("IdempotencyId") WHERE "IdempotencyId" IS NOT NULL;

CREATE INDEX "IX_McpOperatorTerminalSessions_PolicyId" ON "McpOperatorTerminalSessions" ("PolicyId");

CREATE UNIQUE INDEX "IX_McpOperatorTerminalSessions_SessionId" ON "McpOperatorTerminalSessions" ("SessionId");

CREATE INDEX "IX_McpOperatorTerminalSessions_TenantId_AgentId_State_ExpiresAtUtc" ON "McpOperatorTerminalSessions" ("TenantId", "AgentId", "State", "ExpiresAtUtc");

CREATE INDEX "IX_McpOperatorTerminalSessions_TenantId_AgentId_Subject_ClientId_State" ON "McpOperatorTerminalSessions" ("TenantId", "AgentId", "Subject", "ClientId", "State");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829133000_AddMcpOperatorTerminalSessionOwnership', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "McpOperatorCommands" (
    "Id" uuid NOT NULL,
    "CommandId" character varying(64) NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "Subject" character varying(256) NOT NULL,
    "ClientId" character varying(256) NOT NULL,
    "McpResource" character varying(512) NOT NULL,
    "McpInstance" character varying(32) NOT NULL,
    "PolicyId" uuid NOT NULL,
    "PolicyVersion" bigint NOT NULL,
    "AcceptedAuditId" uuid NOT NULL,
    "IdempotencyId" uuid,
    "CorrelationId" character varying(128) NOT NULL,
    "ShellType" character varying(32) NOT NULL,
    "WorkingDirectory" character varying(4096),
    "CommandHash" character varying(64) NOT NULL,
    "CommandLength" integer NOT NULL,
    "EnvironmentReferencesJson" jsonb NOT NULL,
    "TimeoutSeconds" integer NOT NULL,
    "MaximumOutputBytes" integer NOT NULL,
    "EffectiveConstraintsJson" jsonb NOT NULL,
    "State" smallint NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "LastUpdatedAtUtc" timestamp with time zone NOT NULL,
    "FailureCode" character varying(64),
    "Version" bigint NOT NULL,
    CONSTRAINT "PK_McpOperatorCommands" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorCommands_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorCommands_McpOperatorAcceptedAudits_AcceptedAuditId" FOREIGN KEY ("AcceptedAuditId") REFERENCES "McpOperatorAcceptedAudits" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorCommands_McpOperatorIdempotencyRecords_IdempotencyId" FOREIGN KEY ("IdempotencyId") REFERENCES "McpOperatorIdempotencyRecords" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorCommands_McpOperatorPolicies_PolicyId" FOREIGN KEY ("PolicyId") REFERENCES "McpOperatorPolicies" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_McpOperatorCommands_AcceptedAuditId" ON "McpOperatorCommands" ("AcceptedAuditId");

CREATE INDEX "IX_McpOperatorCommands_AgentId" ON "McpOperatorCommands" ("AgentId");

CREATE UNIQUE INDEX "IX_McpOperatorCommands_CommandId" ON "McpOperatorCommands" ("CommandId");

CREATE UNIQUE INDEX "IX_McpOperatorCommands_IdempotencyId" ON "McpOperatorCommands" ("IdempotencyId") WHERE "IdempotencyId" IS NOT NULL;

CREATE INDEX "IX_McpOperatorCommands_PolicyId" ON "McpOperatorCommands" ("PolicyId");

CREATE INDEX "IX_McpOperatorCommands_TenantId_AgentId_Subject_ClientId_State" ON "McpOperatorCommands" ("TenantId", "AgentId", "Subject", "ClientId", "State");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829143000_AddMcpOperatorCommandOwnership', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "McpOperatorScripts" (
    "Id" uuid NOT NULL,
    "ScriptId" bigint NOT NULL,
    "TenantId" integer NOT NULL,
    "Subject" character varying(256) NOT NULL,
    "ClientId" character varying(256) NOT NULL,
    "McpResource" character varying(512) NOT NULL,
    "McpInstance" character varying(32) NOT NULL,
    "Name" character varying(120) NOT NULL,
    "Description" character varying(512) NOT NULL,
    "ShellType" character varying(32) NOT NULL,
    "PolicyId" uuid NOT NULL,
    "PolicyVersion" bigint NOT NULL,
    "ContentHash" character varying(64) NOT NULL,
    "ManifestHash" character varying(64) NOT NULL,
    "ParametersJson" jsonb NOT NULL,
    "TimeoutSeconds" integer NOT NULL,
    "WorkingDirectory" character varying(4096) NOT NULL,
    "DeclaredSideEffectsJson" jsonb NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "DeletedAtUtc" timestamp with time zone,
    "Version" bigint NOT NULL,
    CONSTRAINT "PK_McpOperatorScripts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorScripts_McpOperatorPolicies_PolicyId" FOREIGN KEY ("PolicyId") REFERENCES "McpOperatorPolicies" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorScripts_Scripts_ScriptId" FOREIGN KEY ("ScriptId") REFERENCES "Scripts" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "McpOperatorScriptVersions" (
    "Id" uuid NOT NULL,
    "ScriptRecordId" uuid NOT NULL,
    "ScriptId" bigint NOT NULL,
    "ScriptVersion" bigint NOT NULL,
    "Action" character varying(32) NOT NULL,
    "AcceptedAuditId" uuid NOT NULL,
    "Name" character varying(120) NOT NULL,
    "Description" character varying(512) NOT NULL,
    "ShellType" character varying(32) NOT NULL,
    "ContentHash" character varying(64) NOT NULL,
    "ManifestHash" character varying(64) NOT NULL,
    "ParametersJson" jsonb NOT NULL,
    "TimeoutSeconds" integer NOT NULL,
    "WorkingDirectory" character varying(4096) NOT NULL,
    "DeclaredSideEffectsJson" jsonb NOT NULL,
    "OccurredAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_McpOperatorScriptVersions" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorScriptVersions_McpOperatorAcceptedAudits_AcceptedAuditId" FOREIGN KEY ("AcceptedAuditId") REFERENCES "McpOperatorAcceptedAudits" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorScriptVersions_McpOperatorScripts_ScriptRecordId" FOREIGN KEY ("ScriptRecordId") REFERENCES "McpOperatorScripts" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_McpOperatorScripts_PolicyId" ON "McpOperatorScripts" ("PolicyId");

CREATE UNIQUE INDEX "IX_McpOperatorScripts_ScriptId" ON "McpOperatorScripts" ("ScriptId");

CREATE INDEX "IX_McpOperatorScripts_TenantId_Subject_ClientId_McpResource_McpInstance_DeletedAtUtc_UpdatedAtUtc" ON "McpOperatorScripts" ("TenantId", "Subject", "ClientId", "McpResource", "McpInstance", "DeletedAtUtc", "UpdatedAtUtc");

CREATE INDEX "IX_McpOperatorScriptVersions_AcceptedAuditId" ON "McpOperatorScriptVersions" ("AcceptedAuditId");

CREATE INDEX "IX_McpOperatorScriptVersions_ScriptId_OccurredAtUtc" ON "McpOperatorScriptVersions" ("ScriptId", "OccurredAtUtc");

CREATE UNIQUE INDEX "IX_McpOperatorScriptVersions_ScriptRecordId_ScriptVersion" ON "McpOperatorScriptVersions" ("ScriptRecordId", "ScriptVersion");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829150000_AddMcpOperatorScriptOwnership', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "McpOperatorJobs" (
    "Id" uuid NOT NULL,
    "JobId" bigint NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "Subject" character varying(256) NOT NULL,
    "ClientId" character varying(256) NOT NULL,
    "McpResource" character varying(512) NOT NULL,
    "McpInstance" character varying(32) NOT NULL,
    "PolicyId" uuid NOT NULL,
    "PolicyVersion" bigint NOT NULL,
    "TargetSetDigest" character varying(64) NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "DeletedAtUtc" timestamp with time zone,
    "Version" bigint NOT NULL,
    CONSTRAINT "PK_McpOperatorJobs" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorJobs_Jobs_JobId" FOREIGN KEY ("JobId") REFERENCES "Jobs" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorJobs_McpOperatorPolicies_PolicyId" FOREIGN KEY ("PolicyId") REFERENCES "McpOperatorPolicies" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "McpOperatorJobAudits" (
    "Id" uuid NOT NULL,
    "JobRecordId" uuid NOT NULL,
    "JobId" bigint NOT NULL,
    "JobVersion" bigint NOT NULL,
    "Action" character varying(32) NOT NULL,
    "AcceptedAuditId" uuid NOT NULL,
    "OccurredAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_McpOperatorJobAudits" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorJobAudits_McpOperatorAcceptedAudits_AcceptedAuditId" FOREIGN KEY ("AcceptedAuditId") REFERENCES "McpOperatorAcceptedAudits" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorJobAudits_McpOperatorJobs_JobRecordId" FOREIGN KEY ("JobRecordId") REFERENCES "McpOperatorJobs" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "McpOperatorJobRuns" (
    "Id" uuid NOT NULL,
    "JobRunId" numeric(20,0) NOT NULL,
    "JobRecordId" uuid NOT NULL,
    "JobId" bigint NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "AcceptedAuditId" uuid NOT NULL,
    "IdempotencyId" uuid,
    "CorrelationId" character varying(256) NOT NULL,
    "TargetSetDigest" character varying(64) NOT NULL,
    "CancellationRequested" boolean NOT NULL,
    "CancellationRequestedAtUtc" timestamp with time zone,
    "DeletedAtUtc" timestamp with time zone,
    "Version" bigint NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_McpOperatorJobRuns" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorJobRuns_McpOperatorAcceptedAudits_AcceptedAuditId" FOREIGN KEY ("AcceptedAuditId") REFERENCES "McpOperatorAcceptedAudits" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorJobRuns_McpOperatorIdempotencyRecords_IdempotencyId" FOREIGN KEY ("IdempotencyId") REFERENCES "McpOperatorIdempotencyRecords" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorJobRuns_McpOperatorJobs_JobRecordId" FOREIGN KEY ("JobRecordId") REFERENCES "McpOperatorJobs" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "McpOperatorJobRunAudits" (
    "Id" uuid NOT NULL,
    "JobRunRecordId" uuid NOT NULL,
    "Action" character varying(32) NOT NULL,
    "AcceptedAuditId" uuid NOT NULL,
    "OccurredAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_McpOperatorJobRunAudits" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorJobRunAudits_McpOperatorAcceptedAudits_AcceptedAuditId" FOREIGN KEY ("AcceptedAuditId") REFERENCES "McpOperatorAcceptedAudits" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorJobRunAudits_McpOperatorJobRuns_JobRunRecordId" FOREIGN KEY ("JobRunRecordId") REFERENCES "McpOperatorJobRuns" ("Id") ON DELETE RESTRICT
);

CREATE UNIQUE INDEX "IX_McpOperatorJobs_JobId" ON "McpOperatorJobs" ("JobId");

CREATE INDEX "IX_McpOperatorJobs_PolicyId" ON "McpOperatorJobs" ("PolicyId");

CREATE INDEX "IX_McpOperatorJobs_TenantId_AgentId_Subject_ClientId_McpResource_McpInstance_DeletedAtUtc_UpdatedAtUtc" ON "McpOperatorJobs" ("TenantId", "AgentId", "Subject", "ClientId", "McpResource", "McpInstance", "DeletedAtUtc", "UpdatedAtUtc");

CREATE INDEX "IX_McpOperatorJobAudits_AcceptedAuditId" ON "McpOperatorJobAudits" ("AcceptedAuditId");

CREATE INDEX "IX_McpOperatorJobAudits_JobId_OccurredAtUtc" ON "McpOperatorJobAudits" ("JobId", "OccurredAtUtc");

CREATE UNIQUE INDEX "IX_McpOperatorJobAudits_JobRecordId_JobVersion" ON "McpOperatorJobAudits" ("JobRecordId", "JobVersion");

CREATE INDEX "IX_McpOperatorJobRuns_AcceptedAuditId" ON "McpOperatorJobRuns" ("AcceptedAuditId");

CREATE UNIQUE INDEX "IX_McpOperatorJobRuns_IdempotencyId" ON "McpOperatorJobRuns" ("IdempotencyId") WHERE "IdempotencyId" IS NOT NULL;

CREATE INDEX "IX_McpOperatorJobRuns_JobRecordId" ON "McpOperatorJobRuns" ("JobRecordId");

CREATE UNIQUE INDEX "IX_McpOperatorJobRuns_JobRunId" ON "McpOperatorJobRuns" ("JobRunId");

CREATE INDEX "IX_McpOperatorJobRuns_TenantId_AgentId_DeletedAtUtc_CreatedAtUtc" ON "McpOperatorJobRuns" ("TenantId", "AgentId", "DeletedAtUtc", "CreatedAtUtc");

CREATE INDEX "IX_McpOperatorJobRunAudits_AcceptedAuditId" ON "McpOperatorJobRunAudits" ("AcceptedAuditId");

CREATE INDEX "IX_McpOperatorJobRunAudits_JobRunRecordId_OccurredAtUtc" ON "McpOperatorJobRunAudits" ("JobRunRecordId", "OccurredAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829160000_AddMcpOperatorJobOwnership', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "McpOperatorTasks" (
    "Id" uuid NOT NULL,
    "TaskActivityId" bigint NOT NULL,
    "CommandId" character varying(64) NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "Subject" character varying(256) NOT NULL,
    "ClientId" character varying(256) NOT NULL,
    "McpResource" character varying(512) NOT NULL,
    "McpInstance" character varying(32) NOT NULL,
    "PolicyId" uuid NOT NULL,
    "PolicyVersion" bigint NOT NULL,
    "TargetSetDigest" character varying(64) NOT NULL,
    "AcceptedAuditId" uuid NOT NULL,
    "IdempotencyId" uuid NOT NULL,
    "CorrelationId" character varying(256) NOT NULL,
    "TaskType" character varying(64) NOT NULL,
    "ShellType" character varying(32),
    "CommandHash" character varying(64),
    "CommandLength" integer NOT NULL,
    "ScriptId" bigint,
    "ScriptVersion" bigint,
    "ScriptContentHash" character varying(64),
    "TimeoutSeconds" integer NOT NULL,
    "MaximumOutputBytes" integer NOT NULL,
    "State" character varying(32) NOT NULL,
    "ResultSummary" character varying(49152),
    "CancellationRequested" boolean NOT NULL,
    "CancellationRequestedAtUtc" timestamp with time zone,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "CompletedAtUtc" timestamp with time zone,
    "Version" bigint NOT NULL,
    CONSTRAINT "PK_McpOperatorTasks" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorTasks_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorTasks_JobTaskActivities_TaskActivityId" FOREIGN KEY ("TaskActivityId") REFERENCES "JobTaskActivities" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorTasks_McpOperatorAcceptedAudits_AcceptedAuditId" FOREIGN KEY ("AcceptedAuditId") REFERENCES "McpOperatorAcceptedAudits" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorTasks_McpOperatorIdempotencyRecords_IdempotencyId" FOREIGN KEY ("IdempotencyId") REFERENCES "McpOperatorIdempotencyRecords" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorTasks_McpOperatorPolicies_PolicyId" FOREIGN KEY ("PolicyId") REFERENCES "McpOperatorPolicies" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "McpOperatorTaskAudits" (
    "Id" uuid NOT NULL,
    "TaskRecordId" uuid NOT NULL,
    "Action" character varying(32) NOT NULL,
    "AcceptedAuditId" uuid NOT NULL,
    "OccurredAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_McpOperatorTaskAudits" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorTaskAudits_McpOperatorAcceptedAudits_AcceptedAuditId" FOREIGN KEY ("AcceptedAuditId") REFERENCES "McpOperatorAcceptedAudits" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorTaskAudits_McpOperatorTasks_TaskRecordId" FOREIGN KEY ("TaskRecordId") REFERENCES "McpOperatorTasks" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_McpOperatorTasks_AcceptedAuditId" ON "McpOperatorTasks" ("AcceptedAuditId");

CREATE INDEX "IX_McpOperatorTasks_AgentId" ON "McpOperatorTasks" ("AgentId");

CREATE UNIQUE INDEX "IX_McpOperatorTasks_CommandId" ON "McpOperatorTasks" ("CommandId");

CREATE UNIQUE INDEX "IX_McpOperatorTasks_IdempotencyId" ON "McpOperatorTasks" ("IdempotencyId");

CREATE INDEX "IX_McpOperatorTasks_PolicyId" ON "McpOperatorTasks" ("PolicyId");

CREATE UNIQUE INDEX "IX_McpOperatorTasks_TaskActivityId" ON "McpOperatorTasks" ("TaskActivityId");

CREATE INDEX "IX_McpOperatorTasks_TenantId_AgentId_Subject_ClientId_McpResource_McpInstance_State_UpdatedAtUtc" ON "McpOperatorTasks" ("TenantId", "AgentId", "Subject", "ClientId", "McpResource", "McpInstance", "State", "UpdatedAtUtc");

CREATE INDEX "IX_McpOperatorTaskAudits_AcceptedAuditId" ON "McpOperatorTaskAudits" ("AcceptedAuditId");

CREATE INDEX "IX_McpOperatorTaskAudits_TaskRecordId_OccurredAtUtc" ON "McpOperatorTaskAudits" ("TaskRecordId", "OccurredAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829170000_AddMcpOperatorTaskOwnership', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "McpOperatorRequests" (
    "Id" uuid NOT NULL,
    "RequestId" integer NOT NULL,
    "JobId" bigint NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "Subject" character varying(256) NOT NULL,
    "ClientId" character varying(256) NOT NULL,
    "McpResource" character varying(512) NOT NULL,
    "McpInstance" character varying(32) NOT NULL,
    "PolicyId" uuid NOT NULL,
    "PolicyVersion" bigint NOT NULL,
    "TargetSetDigest" character varying(64) NOT NULL,
    "AcceptedAuditId" uuid NOT NULL,
    "IdempotencyId" uuid NOT NULL,
    "CorrelationId" character varying(256) NOT NULL,
    "State" character varying(32) NOT NULL,
    "Summary" character varying(4096) NOT NULL,
    "ResultSummary" character varying(49152),
    "ClaimReferenceHash" character varying(64),
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "CompletedAtUtc" timestamp with time zone,
    "Version" bigint NOT NULL,
    CONSTRAINT "PK_McpOperatorRequests" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorRequests_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorRequests_Jobs_JobId" FOREIGN KEY ("JobId") REFERENCES "Jobs" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorRequests_McpOperatorAcceptedAudits_AcceptedAuditId" FOREIGN KEY ("AcceptedAuditId") REFERENCES "McpOperatorAcceptedAudits" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorRequests_McpOperatorIdempotencyRecords_IdempotencyId" FOREIGN KEY ("IdempotencyId") REFERENCES "McpOperatorIdempotencyRecords" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorRequests_McpOperatorPolicies_PolicyId" FOREIGN KEY ("PolicyId") REFERENCES "McpOperatorPolicies" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorRequests_Requests_RequestId" FOREIGN KEY ("RequestId") REFERENCES "Requests" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "McpOperatorRequestAudits" (
    "Id" uuid NOT NULL,
    "RequestRecordId" uuid NOT NULL,
    "Action" character varying(32) NOT NULL,
    "AcceptedAuditId" uuid NOT NULL,
    "OccurredAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_McpOperatorRequestAudits" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorRequestAudits_McpOperatorAcceptedAudits_AcceptedAuditId" FOREIGN KEY ("AcceptedAuditId") REFERENCES "McpOperatorAcceptedAudits" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorRequestAudits_McpOperatorRequests_RequestRecordId" FOREIGN KEY ("RequestRecordId") REFERENCES "McpOperatorRequests" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_McpOperatorRequests_AcceptedAuditId" ON "McpOperatorRequests" ("AcceptedAuditId");

CREATE INDEX "IX_McpOperatorRequests_AgentId" ON "McpOperatorRequests" ("AgentId");

CREATE UNIQUE INDEX "IX_McpOperatorRequests_IdempotencyId" ON "McpOperatorRequests" ("IdempotencyId");

CREATE INDEX "IX_McpOperatorRequests_JobId_UpdatedAtUtc" ON "McpOperatorRequests" ("JobId", "UpdatedAtUtc");

CREATE INDEX "IX_McpOperatorRequests_PolicyId" ON "McpOperatorRequests" ("PolicyId");

CREATE UNIQUE INDEX "IX_McpOperatorRequests_RequestId" ON "McpOperatorRequests" ("RequestId");

CREATE INDEX "IX_McpOperatorRequests_TenantId_AgentId_Subject_ClientId_McpResource_McpInstance_State_UpdatedAtUtc" ON "McpOperatorRequests" ("TenantId", "AgentId", "Subject", "ClientId", "McpResource", "McpInstance", "State", "UpdatedAtUtc");

CREATE INDEX "IX_McpOperatorRequestAudits_AcceptedAuditId" ON "McpOperatorRequestAudits" ("AcceptedAuditId");

CREATE INDEX "IX_McpOperatorRequestAudits_RequestRecordId_OccurredAtUtc" ON "McpOperatorRequestAudits" ("RequestRecordId", "OccurredAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829180000_AddMcpOperatorRequestOwnership', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Agents" ADD "DeletedAtUtc" timestamp with time zone;

ALTER TABLE "Agents" ADD "DeletedBy" text;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829183612_AddAgentDecommissionState', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Tenants" ADD "Version" bigint NOT NULL DEFAULT 1;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260829190000_AddTenantOperatorConcurrency', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "McpOperatorTerminalActions" (
    "Id" uuid NOT NULL,
    "SessionRecordId" uuid NOT NULL,
    "Operation" character varying(64) NOT NULL,
    "DelegationRequestId" character varying(128) NOT NULL,
    "PayloadHash" character varying(64) NOT NULL,
    "Outcome" smallint NOT NULL,
    "ResultReference" character varying(128),
    "AcceptedAuditId" uuid,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "CompletedAtUtc" timestamp with time zone,
    "Version" bigint NOT NULL,
    CONSTRAINT "PK_McpOperatorTerminalActions" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorTerminalActions_McpOperatorAcceptedAudits_Accept~" FOREIGN KEY ("AcceptedAuditId") REFERENCES "McpOperatorAcceptedAudits" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorTerminalActions_McpOperatorTerminalSessions_Sess~" FOREIGN KEY ("SessionRecordId") REFERENCES "McpOperatorTerminalSessions" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_McpOperatorTerminalActions_AcceptedAuditId" ON "McpOperatorTerminalActions" ("AcceptedAuditId");

CREATE INDEX "IX_McpOperatorTerminalActions_SessionRecordId_CreatedAtUtc" ON "McpOperatorTerminalActions" ("SessionRecordId", "CreatedAtUtc");

CREATE UNIQUE INDEX "IX_McpOperatorTerminalActions_SessionRecordId_Operation_Delega~" ON "McpOperatorTerminalActions" ("SessionRecordId", "Operation", "DelegationRequestId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260830122359_AddMcpOperatorTerminalActionIdempotency', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "McpOperatorFileArtifacts" (
    "Id" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "Subject" character varying(256) NOT NULL,
    "ClientId" character varying(256) NOT NULL,
    "McpResource" character varying(512) NOT NULL,
    "McpInstance" character varying(32) NOT NULL,
    "ReadRootFingerprint" character varying(64) NOT NULL,
    "FileName" character varying(512) NOT NULL,
    "SizeBytes" bigint NOT NULL,
    "Sha256" character varying(64) NOT NULL,
    "MimeType" character varying(256) NOT NULL,
    "Content" bytea NOT NULL,
    "AcceptedAuditId" uuid NOT NULL,
    "IdempotencyId" uuid NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "DeletedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_McpOperatorFileArtifacts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_McpOperatorFileArtifacts_Agents_AgentId" FOREIGN KEY ("AgentId") REFERENCES "Agents" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorFileArtifacts_McpOperatorAcceptedAudits_Accepted~" FOREIGN KEY ("AcceptedAuditId") REFERENCES "McpOperatorAcceptedAudits" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_McpOperatorFileArtifacts_McpOperatorIdempotencyRecords_Idem~" FOREIGN KEY ("IdempotencyId") REFERENCES "McpOperatorIdempotencyRecords" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_McpOperatorFileArtifacts_AcceptedAuditId" ON "McpOperatorFileArtifacts" ("AcceptedAuditId");

CREATE INDEX "IX_McpOperatorFileArtifacts_AgentId" ON "McpOperatorFileArtifacts" ("AgentId");

CREATE UNIQUE INDEX "IX_McpOperatorFileArtifacts_IdempotencyId" ON "McpOperatorFileArtifacts" ("IdempotencyId");

CREATE INDEX "IX_McpOperatorFileArtifacts_TenantId_AgentId_Subject_ClientId_~" ON "McpOperatorFileArtifacts" ("TenantId", "AgentId", "Subject", "ClientId", "McpResource", "McpInstance", "ExpiresAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260830160317_AddMcpOperatorFileArtifacts', '10.0.12');

COMMIT;

START TRANSACTION;
INSERT INTO "McpOperatorPolicies" (
    "Id", "Name", "Environment", "Effect", "Priority",
    "PrincipalSelectorKind", "PrincipalSelectorValue",
    "TargetSelectorKind", "TenantId", "AgentId", "ClientTag",
    "TargetClassification", "OperationFamily", "Operation",
    "ConstraintsJson", "CreatedAtUtc", "CreatedBy", "ExpiresAtUtc",
    "ReviewByUtc", "DisabledAtUtc", "DisabledBy", "Version", "AuditReference",
    "LifecycleState")
SELECT
    md5('dev-mcp-policy-bootstrap:netratel-mcp-dev-policy-bootstrap-administrator:v1')::uuid,
    'Dev QA PolicyAdministration bootstrap',
    1, 2, 100,
    3, 'netratel-mcp-dev-policy-bootstrap-administrator',
    1, legacy_grant."TenantId", legacy_grant."AgentId", NULL,
    1, 262144, NULL,
    jsonb_build_object(
        'maxFanOut', 1,
        'allowedTargetClassifications', jsonb_build_array(1),
        'destructiveOperationsAllowed', false),
    clock_timestamp(), 'dev-mcp-policy-bootstrap-migration', legacy_grant."ExpiresAtUtc",
    LEAST(legacy_grant."ExpiresAtUtc", clock_timestamp() + INTERVAL '7 days'),
    NULL, NULL, 1,
    'dev-policy-bootstrap:netratel-mcp-dev-policy-bootstrap-administrator', 1
FROM "DevelopmentOperatorTargetGrants" AS legacy_grant
WHERE legacy_grant."TenantId" = 1
  AND legacy_grant."AgentId" = '1641ed2c-26f7-4277-abfb-81a56c26cee9'::uuid
  AND legacy_grant."Classification" = 1
  AND legacy_grant."RevokedAtUtc" IS NULL
  AND legacy_grant."ExpiresAtUtc" > clock_timestamp()
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260831065009_SeedDevMcpPolicyAdministrationBootstrap', '10.0.12');

COMMIT;

START TRANSACTION;
UPDATE "McpOperatorPolicies"
SET "TargetClassification" = NULL,
    "ConstraintsJson" = jsonb_build_object(
        'maxFanOut', 1,
        'destructiveOperationsAllowed', false),
    "Version" = "Version" + 1
WHERE "Id" = md5('dev-mcp-policy-bootstrap:netratel-mcp-dev-policy-bootstrap-administrator:v1')::uuid
  AND "Environment" = 1
  AND "Effect" = 2
  AND "Priority" = 100
  AND "PrincipalSelectorKind" = 3
  AND "PrincipalSelectorValue" = 'netratel-mcp-dev-policy-bootstrap-administrator'
  AND "TargetSelectorKind" = 1
  AND "TenantId" = 1
  AND "AgentId" = '1641ed2c-26f7-4277-abfb-81a56c26cee9'::uuid
  AND "TargetClassification" = 1
  AND "OperationFamily" = 262144
  AND "Operation" IS NULL
  AND "LifecycleState" = 1
  AND "ConstraintsJson" @> jsonb_build_object(
        'maxFanOut', 1,
        'allowedTargetClassifications', jsonb_build_array(1),
        'destructiveOperationsAllowed', false)
  AND NOT EXISTS (
        SELECT 1
        FROM "McpOperatorAcceptedAudits" AS audit
        WHERE audit."PolicyId" = "McpOperatorPolicies"."Id");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260831083000_RemoveUnresolvableDevBootstrapClassificationPredicate', '10.0.12');

COMMIT;

START TRANSACTION;
INSERT INTO "McpOperatorPolicies" (
    "Id", "Name", "Environment", "Effect", "Priority",
    "PrincipalSelectorKind", "PrincipalSelectorValue",
    "TargetSelectorKind", "TenantId", "AgentId", "ClientTag",
    "TargetClassification", "OperationFamily", "Operation",
    "ConstraintsJson", "CreatedAtUtc", "CreatedBy", "ExpiresAtUtc",
    "ReviewByUtc", "DisabledAtUtc", "DisabledBy", "Version", "AuditReference",
    "LifecycleState")
SELECT
    md5('dev-mcp-policy-bootstrap:netratel-mcp-dev-policy-administrator:tenant-provisioning:v1')::uuid,
    'Dev tenant PolicyAdministration provisioning admission',
    1, 2, 80,
    3, 'netratel-mcp-dev-policy-administrator',
    2, normal_admission."TenantId", NULL, NULL,
    NULL, 262144, NULL,
    jsonb_build_object(
        'maxFanOut', 1,
        'destructiveOperationsAllowed', false),
    clock_timestamp(), 'dev-mcp-policy-provisioning-migration',
    LEAST(legacy_grant."ExpiresAtUtc", normal_admission."ExpiresAtUtc"),
    LEAST(legacy_grant."ExpiresAtUtc", normal_admission."ReviewByUtc"),
    NULL, NULL, 1,
    'dev-policy-bootstrap:netratel-mcp-dev-policy-administrator:tenant-provisioning', 1
FROM "DevelopmentOperatorTargetGrants" AS legacy_grant
JOIN "McpOperatorPolicies" AS normal_admission
  ON normal_admission."Environment" = 1
 AND normal_admission."Effect" = 2
 AND normal_admission."PrincipalSelectorKind" = 3
 AND normal_admission."PrincipalSelectorValue" = 'netratel-mcp-dev-policy-administrator'
 AND normal_admission."TargetSelectorKind" = 1
 AND normal_admission."TenantId" = 1
 AND normal_admission."AgentId" = '1641ed2c-26f7-4277-abfb-81a56c26cee9'::uuid
 AND normal_admission."OperationFamily" = 262144
 AND normal_admission."Operation" IS NULL
 AND normal_admission."ExpiresAtUtc" > clock_timestamp()
 AND normal_admission."ReviewByUtc" IS NOT NULL
 AND normal_admission."LifecycleState" = 1
WHERE legacy_grant."TenantId" = 1
  AND legacy_grant."AgentId" = '1641ed2c-26f7-4277-abfb-81a56c26cee9'::uuid
  AND legacy_grant."Classification" = 1
  AND legacy_grant."RevokedAtUtc" IS NULL
  AND legacy_grant."ExpiresAtUtc" > clock_timestamp()
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260831133310_SeedDevMcpPolicyAdminTenantProvisioning', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "JobTaskActivities" ADD "ResultJson" text;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260831181727_AddTaskActivityResultJson', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "McpOperatorCommands" ADD "OutputJson" jsonb;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260905194254_AddMcpCommandOutput', '10.0.12');

COMMIT;

START TRANSACTION;
INSERT INTO "McpOperatorPolicies" (
    "Id", "Name", "Environment", "Effect", "Priority",
    "PrincipalSelectorKind", "PrincipalSelectorValue",
    "TargetSelectorKind", "TenantId", "AgentId", "ClientTag",
    "TargetClassification", "OperationFamily", "Operation", "ConstraintsJson",
    "CreatedAtUtc", "CreatedBy", "Version", "AuditReference", "LifecycleState")
VALUES (
    md5('dev-mcp-feature-testing:netratel-mcp-dev-feature-testers:v1')::uuid, 'Full Dev MCP feature testing', 1, 2, 1000,
    3, 'netratel-mcp-dev-feature-testers', 5, 0, NULL, NULL, NULL, 524287, NULL,
    jsonb_build_object(
        'readRoots', jsonb_build_array('/', 'A:/', 'B:/', 'C:/', 'D:/', 'E:/', 'F:/', 'G:/', 'H:/', 'I:/', 'J:/', 'K:/', 'L:/', 'M:/', 'N:/', 'O:/', 'P:/', 'Q:/', 'R:/', 'S:/', 'T:/', 'U:/', 'V:/', 'W:/', 'X:/', 'Y:/', 'Z:/'),
        'writeRoots', jsonb_build_array('/', 'A:/', 'B:/', 'C:/', 'D:/', 'E:/', 'F:/', 'G:/', 'H:/', 'I:/', 'J:/', 'K:/', 'L:/', 'M:/', 'N:/', 'O:/', 'P:/', 'Q:/', 'R:/', 'S:/', 'T:/', 'U:/', 'V:/', 'W:/', 'X:/', 'Y:/', 'Z:/'),
        'allowedShells', jsonb_build_array('bash', 'sh', 'pwsh', 'powershell', 'cmd', 'zsh', 'fish'),
        'workingDirectories', jsonb_build_array('*'),
        'maxCommandDurationSeconds', 3600,
        'maxTerminalIdleSeconds', 3600,
        'maxTerminalLifetimeSeconds', 86400,
        'maxConcurrentTerminalSessions', 100,
        'maxConcurrentCommands', 100,
        'maxOutputBytes', 16777216,
        'maxArtifactBytes', 268435456,
        'maxScriptBytes', 1048576,
        'maxJobTargetCount', 1000,
        'maxTaskTargetCount', 1000,
        'maxFanOut', 1000,
        'maxOnboardingCodeLifetimeSeconds', 2592000,
        'maxOnboardingCodeUses', 10000,
        'destructiveOperationsAllowed', true,
        'allowActiveTenantDeletion', true),
    clock_timestamp(), 'dev-mcp-feature-testing-migration', 1,
    'public-source-migration', 1)
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260905195000_SeedDevMcpFeatureTesting', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "Scripts" ADD "DeletedAtUtc" timestamp with time zone;

ALTER TABLE "Scripts" ADD "SourceRevision" bigint NOT NULL DEFAULT 1;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260906090619_AddScriptSourceRevision', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "BootstrapInitializations" (
    "Id" integer NOT NULL,
    "BootstrapInstanceId" uuid NOT NULL,
    "OperationId" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "AdministratorUserId" character varying(450) NOT NULL,
    "CompletedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_BootstrapInitializations" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX "IX_BootstrapInitializations_BootstrapInstanceId" ON "BootstrapInitializations" ("BootstrapInstanceId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260921103721_AddBootstrapInitializationRecord', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "ClientReleaseImportOperations" (
    "Id" uuid NOT NULL,
    "GitHubReleaseId" bigint NOT NULL,
    "SourceRepository" character varying(128) NOT NULL,
    "Tag" character varying(128) NOT NULL,
    "Version" character varying(64) NOT NULL,
    "BuildCommit" character varying(40),
    "PublicationSha256" character varying(64),
    "State" smallint NOT NULL,
    "RequestedBy" character varying(256) NOT NULL,
    "IsAutomatic" boolean NOT NULL,
    "CancellationRequested" boolean NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "ImportedAtUtc" timestamp with time zone,
    "PublishedAtUtc" timestamp with time zone,
    "PublishedBy" character varying(256),
    "TotalBytes" bigint,
    "DownloadedBytes" bigint NOT NULL,
    "AttemptCount" integer NOT NULL,
    "Error" character varying(2048),
    "LeaseOwner" uuid,
    "LeaseUntilUtc" timestamp with time zone,
    "LeaseGeneration" bigint NOT NULL,
    CONSTRAINT "PK_ClientReleaseImportOperations" PRIMARY KEY ("Id")
);

CREATE TABLE "ClientReleaseImportAssets" (
    "OperationId" uuid NOT NULL,
    "RuntimeId" character varying(32) NOT NULL,
    "GitHubAssetId" bigint NOT NULL,
    "SourceName" character varying(256) NOT NULL,
    "SourceSha256" character varying(64) NOT NULL,
    "SourceSizeBytes" bigint NOT NULL,
    "LocalSha256" character varying(64),
    "LocalSizeBytes" bigint,
    "ConversionContract" character varying(128),
    "State" smallint NOT NULL,
    "DownloadedBytes" bigint NOT NULL,
    "Error" character varying(2048),
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ClientReleaseImportAssets" PRIMARY KEY ("OperationId", "RuntimeId"),
    CONSTRAINT "FK_ClientReleaseImportAssets_ClientReleaseImportOperations_Ope~" FOREIGN KEY ("OperationId") REFERENCES "ClientReleaseImportOperations" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_ClientReleaseImportOperations_GitHubReleaseId" ON "ClientReleaseImportOperations" ("GitHubReleaseId");

CREATE INDEX "IX_ClientReleaseImportOperations_State_LeaseUntilUtc_CreatedAt~" ON "ClientReleaseImportOperations" ("State", "LeaseUntilUtc", "CreatedAtUtc");

CREATE INDEX "IX_ClientReleaseImportOperations_Version" ON "ClientReleaseImportOperations" ("Version");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260924181300_AddClientReleaseImportOperations', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "EnrollmentCodes" ADD "CodeHash" character varying(64);

CREATE TABLE "ClientInstallGrants" (
    "Id" uuid NOT NULL,
    "TokenHash" character varying(64) NOT NULL,
    "ProtectedToken" text NOT NULL,
    "ProtectedScript" text NOT NULL,
    "RequestKey" character varying(128) NOT NULL,
    "RequestFingerprint" character varying(64) NOT NULL,
    "EnrollmentCodeId" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "RuntimeId" character varying(32) NOT NULL,
    "ArtifactVersion" character varying(64) NOT NULL,
    "ArtifactSha256" character varying(64) NOT NULL,
    "InstallAsService" boolean NOT NULL,
    "SilentInstall" boolean NOT NULL,
    "PublicWebBaseUrl" character varying(2048) NOT NULL,
    "PublicApiBaseUrl" character varying(2048) NOT NULL,
    "CreatedBy" character varying(256) NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "MaxUses" integer NOT NULL,
    "RevokedAtUtc" timestamp with time zone,
    "RevokedBy" character varying(256),
    CONSTRAINT "PK_ClientInstallGrants" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_ClientInstallGrants_EnrollmentCodes_EnrollmentCodeId" FOREIGN KEY ("EnrollmentCodeId") REFERENCES "EnrollmentCodes" ("Id") ON DELETE RESTRICT
);

CREATE UNIQUE INDEX "IX_EnrollmentCodes_CodeHash" ON "EnrollmentCodes" ("CodeHash");

CREATE INDEX "IX_ClientInstallGrants_EnrollmentCodeId" ON "ClientInstallGrants" ("EnrollmentCodeId");

CREATE UNIQUE INDEX "IX_ClientInstallGrants_RequestKey" ON "ClientInstallGrants" ("RequestKey");

CREATE INDEX "IX_ClientInstallGrants_TenantId_CreatedAtUtc" ON "ClientInstallGrants" ("TenantId", "CreatedAtUtc");

CREATE UNIQUE INDEX "IX_ClientInstallGrants_TokenHash" ON "ClientInstallGrants" ("TokenHash");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260924185042_AddProtectedClientInstallLinks', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "ClientReleaseImportOperations" ADD "AutomaticPublishAttemptAtUtc" timestamp with time zone;

ALTER TABLE "ClientReleaseImportOperations" ADD "AutomaticPublishError" character varying(2048);

CREATE TABLE "ClientReleaseAutomationSettings" (
    "Id" integer NOT NULL,
    "CheckEveryHours" integer NOT NULL,
    "DownloadStable" boolean NOT NULL,
    "DownloadPrerelease" boolean NOT NULL,
    "PublishAutomatically" boolean NOT NULL,
    "DeployPrereleaseAutomatically" boolean NOT NULL,
    "NextCheckAtUtc" timestamp with time zone,
    "LastAttemptAtUtc" timestamp with time zone,
    "LastSuccessAtUtc" timestamp with time zone,
    "LastError" character varying(2048),
    "LeaseOwner" uuid,
    "LeaseUntilUtc" timestamp with time zone,
    "Revision" bigint NOT NULL,
    "UpdatedBy" character varying(256) NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ClientReleaseAutomationSettings" PRIMARY KEY ("Id")
);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260924194413_AddClientReleaseAutomation', '10.0.12');

COMMIT;

START TRANSACTION;
ALTER TABLE "ClientInstallGrants" ADD "EffectiveGatewayBaseUrl" character varying(2048);

ALTER TABLE "ClientInstallGrants" ADD "PublicWebSource" character varying(64);

ALTER TABLE "ClientInstallGrants" ADD "PublicApiSource" character varying(64);

ALTER TABLE "ClientInstallGrants" ADD "GatewaySource" character varying(64);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261002160000_AddClientInstallEndpointSnapshot', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "ClientConnectionEpochs" (
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "LastIssuedEpoch" bigint NOT NULL,
    CONSTRAINT "PK_ClientConnectionEpochs" PRIMARY KEY ("TenantId", "AgentId")
);

CREATE TABLE "ClientServicesSnapshots" (
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "ConnectionEpoch" bigint NOT NULL,
    "LastAcceptedSequence" numeric(20,0) NOT NULL,
    "Revision" bigint NOT NULL,
    "StateJson" jsonb NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ClientServicesSnapshots" PRIMARY KEY ("TenantId", "AgentId")
);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261002170000_AddClientServicesSnapshots', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "ManagedOrchestrationRequestBindings" (
    "RequestId" integer NOT NULL,
    "ServicePrincipalId" uuid NOT NULL,
    "TenantId" integer NOT NULL,
    "AgentId" uuid NOT NULL,
    "JobDefinitionId" character varying(256) NOT NULL,
    "ExecutionId" character varying(256),
    "ParentRequestId" character varying(256) NOT NULL,
    "RequestTaskId" character varying(256) NOT NULL,
    "CorrelationId" character varying(256) NOT NULL,
    "IngestFingerprint" character varying(64) NOT NULL,
    "LinkId" character varying(256),
    "LinkRevision" bigint NOT NULL,
    "GrantHash" character varying(64),
    "PeerInstanceId" character varying(256) NOT NULL,
    "PeerTenantId" character varying(256) NOT NULL,
    "CallbackUrl" character varying(2048),
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ManagedOrchestrationRequestBindings" PRIMARY KEY ("RequestId"),
    CONSTRAINT "FK_ManagedOrchestrationRequestBindings_Requests_RequestId" FOREIGN KEY ("RequestId") REFERENCES "Requests" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "OrchestrationCallbackDeliveries" (
    "RequestId" integer NOT NULL,
    "Phase" character varying(32) NOT NULL,
    "Revision" bigint NOT NULL,
    "LeaseId" uuid,
    "LeaseExpiresAtUtc" timestamp with time zone,
    "NextAttemptAtUtc" timestamp with time zone NOT NULL,
    "DeliveredAtUtc" timestamp with time zone,
    "Attempts" integer NOT NULL,
    CONSTRAINT "PK_OrchestrationCallbackDeliveries" PRIMARY KEY ("RequestId", "Phase"),
    CONSTRAINT "FK_OrchestrationCallbackDeliveries_Requests_RequestId" FOREIGN KEY ("RequestId") REFERENCES "Requests" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "ServiceIdentityConfiguration" (
    "Id" integer GENERATED BY DEFAULT AS IDENTITY,
    "Revision" bigint NOT NULL,
    "Enabled" boolean NOT NULL,
    "WebBaseUrl" character varying(2048) NOT NULL,
    "ApiBaseUrl" character varying(2048) NOT NULL,
    "Issuer" character varying(2048) NOT NULL,
    "Audience" character varying(256) NOT NULL,
    "UpdatedBy" character varying(256) NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_ServiceIdentityConfiguration" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_ServiceIdentityConfiguration_Singleton" CHECK ("Id" = 1)
);

CREATE TABLE "ServiceLinkAttempts" (
    "AttemptId" character varying(128) NOT NULL,
    "Role" text NOT NULL,
    "LocalTenantId" text NOT NULL,
    "LocalActorId" text NOT NULL,
    "PeerInstanceId" text NOT NULL,
    "PeerTenantId" text,
    "LinkId" text,
    "ActiveRelationshipKey" text,
    "LinkRevision" bigint NOT NULL,
    "LifecycleState" text NOT NULL,
    "Decision" text NOT NULL,
    "CommitId" text,
    "AbortId" text,
    "RevocationId" text,
    "DescriptorJson" text NOT NULL,
    "DescriptorHash" text NOT NULL,
    "GrantSummaryJson" text,
    "GrantHash" text,
    "ConsentId" text,
    "ProtectedVerifier" text,
    "ProtectedBrowserState" text,
    "SessionBindingHash" text,
    "PairingCodeHash" text,
    "ProtectedPairingCode" text,
    "ProtectedInboundEscrow" text,
    "ProtectedExchangeResponse" text,
    "ExchangeFingerprint" text,
    "ExchangeResponseHash" text,
    "ExchangeDispatched" boolean NOT NULL,
    "InboundPrincipalId" uuid,
    "ProtectedOutboundCredential" text,
    "OutboundProfileRevision" integer,
    "PeerPreparedAcknowledged" boolean NOT NULL,
    "LocalPreparedAcknowledged" boolean NOT NULL,
    "LocalInboundActive" boolean NOT NULL,
    "LocalBusinessSenderEnabled" boolean NOT NULL,
    "PeerActiveAcknowledged" boolean NOT NULL,
    "LocalActiveAcknowledged" boolean NOT NULL,
    "PeerRevocationAcknowledged" boolean NOT NULL,
    "InitiatorVerificationReceiptId" text,
    "ResponderVerificationReceiptId" text,
    "LastErrorCode" text,
    "ExpiresAtUnixSeconds" bigint NOT NULL,
    "TerminalControlExpiresAtUnixSeconds" bigint,
    "CreatedAtUnixSeconds" bigint NOT NULL,
    "UpdatedAtUnixSeconds" bigint NOT NULL,
    "NextWorkAtUnixSeconds" bigint NOT NULL,
    "Revision" bigint NOT NULL,
    CONSTRAINT "PK_ServiceLinkAttempts" PRIMARY KEY ("AttemptId")
);

CREATE TABLE "ServiceLinkOperations" (
    "LinkId" character varying(128) NOT NULL,
    "OperationId" character varying(128) NOT NULL,
    "Kind" text NOT NULL,
    "RequestFingerprint" text NOT NULL,
    "ResponseJson" text NOT NULL,
    "ProtectedRequestJson" text,
    "Outbound" boolean NOT NULL,
    "Completed" boolean NOT NULL,
    "CreatedAtUnixSeconds" bigint NOT NULL,
    CONSTRAINT "PK_ServiceLinkOperations" PRIMARY KEY ("LinkId", "OperationId")
);

CREATE TABLE "ServiceLinkRotations" (
    "RotationId" text NOT NULL,
    "LinkId" text NOT NULL,
    "DirectionId" text NOT NULL,
    "IsIssuer" boolean NOT NULL,
    "RotationState" text NOT NULL,
    "ActiveRotationKey" text,
    "ExpectedCurrentCredentialRevision" bigint NOT NULL,
    "SuccessorCredentialRevision" bigint,
    "ProtectedOffer" text,
    "ProtectedCandidate" text,
    "OfferExpiresAtUnixSeconds" bigint,
    "SuccessorVerificationReceiptId" text,
    "ActivateDecisionId" text,
    "CallerSwitchRevision" bigint,
    "PredecessorRetireAtUnixSeconds" bigint,
    "LastErrorCode" text,
    "CreatedAtUnixSeconds" bigint NOT NULL,
    "Revision" bigint NOT NULL,
    CONSTRAINT "PK_ServiceLinkRotations" PRIMARY KEY ("RotationId")
);

CREATE TABLE "ServiceLinkRuntimeIdentity" (
    "Id" integer NOT NULL,
    "InstanceId" uuid NOT NULL,
    "SourceInstanceId" uuid,
    "Revision" bigint NOT NULL,
    "SourceAdoptedBy" text,
    "SourceAdoptedAtUnixSeconds" bigint,
    CONSTRAINT "PK_ServiceLinkRuntimeIdentity" PRIMARY KEY ("Id")
);

CREATE TABLE "ServiceLinkVerificationReceipts" (
    "VerificationReceiptId" text NOT NULL,
    "LinkId" text NOT NULL,
    "AttemptId" text NOT NULL,
    "GrantHash" text NOT NULL,
    "ServicePrincipalId" uuid NOT NULL,
    "DirectionId" text NOT NULL,
    "CredentialRevision" bigint NOT NULL,
    "RotationId" text,
    "VerifiedAtUnixSeconds" bigint NOT NULL,
    CONSTRAINT "PK_ServiceLinkVerificationReceipts" PRIMARY KEY ("VerificationReceiptId")
);

CREATE TABLE "ServicePrincipalRegistrations" (
    "Id" uuid NOT NULL,
    "ClientId" character varying(256) NOT NULL,
    "NormalizedClientId" character varying(256) NOT NULL,
    "AliasKey" character varying(128) NOT NULL,
    "Name" character varying(256) NOT NULL,
    "TenantId" integer NOT NULL,
    "PeerInstanceId" character varying(256) NOT NULL,
    "PeerTenantId" character varying(256) NOT NULL,
    "AllowedScopesJson" text NOT NULL,
    "ResourceConstraintsJson" text NOT NULL,
    "LinkId" character varying(256),
    "AttemptId" character varying(256),
    "GrantHash" character varying(64),
    "DescriptorHash" character varying(64),
    "DirectionId" character varying(64),
    "LinkRevision" bigint NOT NULL,
    "Revision" bigint NOT NULL,
    "Version" bigint NOT NULL,
    "CurrentCredentialRevision" bigint NOT NULL,
    "Status" text NOT NULL,
    "Source" text NOT NULL,
    "DeploymentFingerprint" text,
    "CreatedBy" character varying(256) NOT NULL,
    "ApprovedBy" character varying(256) NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "RevokedAtUtc" timestamp with time zone,
    "TerminalControlUntilUtc" timestamp with time zone,
    CONSTRAINT "PK_ServicePrincipalRegistrations" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_ServicePrincipal_Status" CHECK ("Status" IN ('pending','prepared','verified','in_doubt','active','revoked','expired','failed')),
    CONSTRAINT "FK_ServicePrincipalRegistrations_Tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "Tenants" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "ServiceSigningKeys" (
    "Kid" character varying(64) NOT NULL,
    "Issuer" character varying(2048) NOT NULL,
    "ProtectedPrivateKey" text NOT NULL,
    "PublicModulus" text NOT NULL,
    "PublicExponent" text NOT NULL,
    "ActiveSlot" integer,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ValidateUntilUtc" timestamp with time zone,
    CONSTRAINT "PK_ServiceSigningKeys" PRIMARY KEY ("Kid")
);

CREATE TABLE "ServicePrincipalSecrets" (
    "ServicePrincipalId" uuid NOT NULL,
    "CredentialRevision" bigint NOT NULL,
    "SecretHash" character varying(64) NOT NULL,
    "Salt" character varying(64) NOT NULL,
    "Status" text NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "RetireAtUtc" timestamp with time zone,
    CONSTRAINT "PK_ServicePrincipalSecrets" PRIMARY KEY ("ServicePrincipalId", "CredentialRevision"),
    CONSTRAINT "CK_ServiceSecret_Status" CHECK ("Status" IN ('pending','active','retiring','revoked')),
    CONSTRAINT "FK_ServicePrincipalSecrets_ServicePrincipalRegistrations_Servi~" FOREIGN KEY ("ServicePrincipalId") REFERENCES "ServicePrincipalRegistrations" ("Id") ON DELETE RESTRICT
);

CREATE UNIQUE INDEX "IX_ManagedOrchestrationRequestBindings_ExecutionId" ON "ManagedOrchestrationRequestBindings" ("ExecutionId") WHERE "ExecutionId" IS NOT NULL;

CREATE UNIQUE INDEX "IX_ManagedOrchestrationRequestBindings_ServicePrincipalId_Pare~" ON "ManagedOrchestrationRequestBindings" ("ServicePrincipalId", "ParentRequestId", "RequestTaskId");

CREATE INDEX "IX_OrchestrationCallbackDeliveries_DeliveredAtUtc_NextAttemptA~" ON "OrchestrationCallbackDeliveries" ("DeliveredAtUtc", "NextAttemptAtUtc");

CREATE UNIQUE INDEX "IX_ServiceLinkAttempts_ActiveRelationshipKey" ON "ServiceLinkAttempts" ("ActiveRelationshipKey");

CREATE INDEX "IX_ServiceLinkAttempts_LifecycleState_NextWorkAtUnixSeconds" ON "ServiceLinkAttempts" ("LifecycleState", "NextWorkAtUnixSeconds");

CREATE UNIQUE INDEX "IX_ServiceLinkAttempts_LinkId" ON "ServiceLinkAttempts" ("LinkId");

CREATE INDEX "IX_ServiceLinkAttempts_PeerInstanceId_LocalTenantId_PeerTenant~" ON "ServiceLinkAttempts" ("PeerInstanceId", "LocalTenantId", "PeerTenantId", "LinkRevision", "Role");

CREATE INDEX "IX_ServiceLinkOperations_Outbound_Completed" ON "ServiceLinkOperations" ("Outbound", "Completed");

CREATE UNIQUE INDEX "IX_ServiceLinkRotations_ActiveRotationKey" ON "ServiceLinkRotations" ("ActiveRotationKey");

CREATE INDEX "IX_ServiceLinkRotations_LinkId_DirectionId_ExpectedCurrentCred~" ON "ServiceLinkRotations" ("LinkId", "DirectionId", "ExpectedCurrentCredentialRevision");

CREATE INDEX "IX_ServiceLinkVerificationReceipts_LinkId_ServicePrincipalId_C~" ON "ServiceLinkVerificationReceipts" ("LinkId", "ServicePrincipalId", "CredentialRevision", "RotationId");

CREATE UNIQUE INDEX "IX_ServicePrincipalRegistrations_AliasKey" ON "ServicePrincipalRegistrations" ("AliasKey");

CREATE UNIQUE INDEX "IX_ServicePrincipalRegistrations_LinkId_DirectionId" ON "ServicePrincipalRegistrations" ("LinkId", "DirectionId") WHERE "LinkId" IS NOT NULL;

CREATE UNIQUE INDEX "IX_ServicePrincipalRegistrations_NormalizedClientId" ON "ServicePrincipalRegistrations" ("NormalizedClientId");

CREATE INDEX "IX_ServicePrincipalRegistrations_TenantId" ON "ServicePrincipalRegistrations" ("TenantId");

CREATE UNIQUE INDEX "IX_ServicePrincipalSecrets_ServicePrincipalId" ON "ServicePrincipalSecrets" ("ServicePrincipalId") WHERE "Status" = 'pending';

CREATE UNIQUE INDEX "IX_ServiceSigningKeys_ActiveSlot" ON "ServiceSigningKeys" ("ActiveSlot") WHERE "ActiveSlot" IS NOT NULL;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261005050831_AddHelpdeskReciprocalServiceIdentity', '10.0.12');

COMMIT;

START TRANSACTION;
CREATE TABLE "JobRunControls" (
    "RunId" bigint NOT NULL,
    "Revision" bigint NOT NULL,
    "DispatchPreparedAtUtc" timestamp with time zone,
    "DispatchEnqueuedAtUtc" timestamp with time zone,
    "NativeDeadlineUtc" timestamp with time zone,
    "CancellationRequestedAtUtc" timestamp with time zone,
    "CancellationReason" character varying(256),
    "CancellationEnqueuedAtUtc" timestamp with time zone,
    "TerminalReadyAtUtc" timestamp with time zone,
    "TerminalResultHash" character varying(64),
    CONSTRAINT "PK_JobRunControls" PRIMARY KEY ("RunId"),
    CONSTRAINT "FK_JobRunControls_JobRuns_RunId" FOREIGN KEY ("RunId") REFERENCES "JobRuns" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_JobRunControls_TerminalReadyAtUtc_NativeDeadlineUtc" ON "JobRunControls" ("TerminalReadyAtUtc", "NativeDeadlineUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261005113424_AddDurableJobRunControlBoundary', '10.0.12');

COMMIT;

