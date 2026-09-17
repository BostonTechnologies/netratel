using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairAgentLifecycleColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_Agents_TenantId_CreatedAtUtc";
                DROP INDEX IF EXISTS "IX_Agents_TenantId_IsEnabled";
                ALTER TABLE "Agents" DROP COLUMN IF EXISTS "RevokedAtUtc";
                ALTER TABLE "Agents" DROP COLUMN IF EXISTS "LastTokenIssuedAtUtc";
                ALTER TABLE "Agents" DROP COLUMN IF EXISTS "DisabledReason";
                ALTER TABLE "Agents" DROP COLUMN IF EXISTS "IsEnabled";
                """);
        }
    }
}
