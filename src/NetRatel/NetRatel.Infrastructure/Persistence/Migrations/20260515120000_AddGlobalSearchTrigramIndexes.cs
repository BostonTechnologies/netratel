using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGlobalSearchTrigramIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Tenants_GlobalSearch_Name_trgm"
                ON "Tenants" USING gin ("Name" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Tenants_GlobalSearch_Description_trgm"
                ON "Tenants" USING gin ("Description" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Tenants_GlobalSearch_Location_trgm"
                ON "Tenants" USING gin ("Location" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Tenants_GlobalSearch_ContactPerson_trgm"
                ON "Tenants" USING gin ("ContactPerson" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Tenants_GlobalSearch_ContactEmail_trgm"
                ON "Tenants" USING gin ("ContactEmail" gin_trgm_ops);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Scripts_GlobalSearch_Name_trgm"
                ON "Scripts" USING gin ("Name" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Scripts_GlobalSearch_FolderPath_trgm"
                ON "Scripts" USING gin ("FolderPath" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Scripts_GlobalSearch_Description_trgm"
                ON "Scripts" USING gin ("Description" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Scripts_GlobalSearch_ScriptType_trgm"
                ON "Scripts" USING gin ("ScriptType" gin_trgm_ops);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Jobs_GlobalSearch_Name_trgm"
                ON "Jobs" USING gin ("Name" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Jobs_GlobalSearch_FolderPath_trgm"
                ON "Jobs" USING gin ("FolderPath" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Jobs_GlobalSearch_Description_trgm"
                ON "Jobs" USING gin ("Description" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Jobs_GlobalSearch_ClientIdentity_trgm"
                ON "Jobs" USING gin ("ClientIdentity" gin_trgm_ops);
                """);

            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Requests_GlobalSearch_SourceSystem_trgm"
                ON "Requests" USING gin ("SourceSystem" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Requests_GlobalSearch_TargetClientIdentity_trgm"
                ON "Requests" USING gin ("TargetClientIdentity" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Requests_GlobalSearch_RundeckJobDefinitionId_trgm"
                ON "Requests" USING gin ("RundeckJobDefinitionId" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Requests_GlobalSearch_RundeckExecutionId_trgm"
                ON "Requests" USING gin ("RundeckExecutionId" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Requests_GlobalSearch_Status_trgm"
                ON "Requests" USING gin ("Status" gin_trgm_ops);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_Requests_GlobalSearch_ResultMessage_trgm"
                ON "Requests" USING gin ("ResultMessage" gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Requests_GlobalSearch_ResultMessage_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Requests_GlobalSearch_Status_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Requests_GlobalSearch_RundeckExecutionId_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Requests_GlobalSearch_RundeckJobDefinitionId_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Requests_GlobalSearch_TargetClientIdentity_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Requests_GlobalSearch_SourceSystem_trgm";""");

            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Jobs_GlobalSearch_ClientIdentity_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Jobs_GlobalSearch_Description_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Jobs_GlobalSearch_FolderPath_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Jobs_GlobalSearch_Name_trgm";""");

            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Scripts_GlobalSearch_ScriptType_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Scripts_GlobalSearch_Description_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Scripts_GlobalSearch_FolderPath_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Scripts_GlobalSearch_Name_trgm";""");

            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Tenants_GlobalSearch_ContactEmail_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Tenants_GlobalSearch_ContactPerson_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Tenants_GlobalSearch_Location_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Tenants_GlobalSearch_Description_trgm";""");
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Tenants_GlobalSearch_Name_trgm";""");
        }
    }
}
