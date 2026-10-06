START TRANSACTION;
DROP TABLE "FlowActions";

DROP TABLE "FlowAudits";

DROP TABLE "FlowRuntimeIdentity";

DROP TABLE "FlowRuns";

DROP TABLE "FlowVersions";

DROP TABLE "FlowDefinitions";

DROP FUNCTION netratel_flow_event_immutable();
DROP FUNCTION netratel_flow_action_immutable();
DROP FUNCTION netratel_flow_version_immutable();

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261002195852_AddDurableFlows';

COMMIT;

