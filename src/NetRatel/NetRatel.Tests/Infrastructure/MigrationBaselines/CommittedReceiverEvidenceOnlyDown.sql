START TRANSACTION;
DROP TRIGGER "TR_FlowReceiverEvidence_ImmutableInputs" ON "FlowReceiverEvidence";
DROP FUNCTION nr_receiver_evidence_immutable_inputs();

DROP TABLE "FlowReceiverEvidence";

ALTER TABLE "FlowActions" DROP CONSTRAINT "AK_FlowActions_TenantId_RunId_NodeId";

ALTER TABLE "RatelDeskConnectors" DROP COLUMN "AuthenticationJson";

ALTER TABLE "RatelDeskConnectors" DROP COLUMN "ReadinessJson";

CREATE INDEX "IX_FlowActions_TenantId_RunId" ON "FlowActions" ("TenantId", "RunId");

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261006195456_AddFlowReceiverEvidence';

COMMIT;

