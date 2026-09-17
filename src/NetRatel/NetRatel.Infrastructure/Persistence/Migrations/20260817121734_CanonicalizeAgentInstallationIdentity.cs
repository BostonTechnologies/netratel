using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalizeAgentInstallationIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicKeyFingerprint",
                table: "Agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAtUtc",
                table: "Agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByAgentId",
                table: "Agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecoveryUsedAtUtc",
                table: "AgentRefreshTokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                """
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
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_SupersededByAgentId",
                table: "Agents",
                column: "SupersededByAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_TenantId_PublicKeyFingerprint",
                table: "Agents",
                columns: new[] { "TenantId", "PublicKeyFingerprint" },
                unique: true,
                filter: "\"PublicKeyFingerprint\" IS NOT NULL AND \"SupersededAtUtc\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_Agents_SupersededByAgentId",
                table: "Agents",
                column: "SupersededByAgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Agents_Agents_SupersededByAgentId",
                table: "Agents");

            migrationBuilder.DropIndex(
                name: "IX_Agents_SupersededByAgentId",
                table: "Agents");

            migrationBuilder.DropIndex(
                name: "IX_Agents_TenantId_PublicKeyFingerprint",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "PublicKeyFingerprint",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "SupersededAtUtc",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "SupersededByAgentId",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "RecoveryUsedAtUtc",
                table: "AgentRefreshTokens");
        }
    }
}
