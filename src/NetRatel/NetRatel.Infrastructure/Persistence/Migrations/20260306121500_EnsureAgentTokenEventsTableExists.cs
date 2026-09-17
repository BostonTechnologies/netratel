using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    public partial class EnsureAgentTokenEventsTableExists : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "AgentTokenEvents" (
                    "Id" uuid NOT NULL,
                    "TenantId" integer NOT NULL,
                    "AgentId" uuid NOT NULL,
                    "EventType" text NOT NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    "Ip" text NULL,
                    "UserAgent" text NULL,
                    "DetailsJson" text NULL,
                    CONSTRAINT "PK_AgentTokenEvents" PRIMARY KEY ("Id")
                );

                CREATE INDEX IF NOT EXISTS "IX_AgentTokenEvents_TenantId_AgentId_CreatedAtUtc"
                    ON "AgentTokenEvents" ("TenantId", "AgentId", "CreatedAtUtc");
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_AgentTokenEvents_TenantId_AgentId_CreatedAtUtc";
                DROP TABLE IF EXISTS "AgentTokenEvents";
                """);
        }
    }
}
