using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairGlobalSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The earlier hand-authored Global Search migration was never discoverable by EF.
            // This generated migration repairs the deployed schema with the indexes used by the
            // bounded direct-match phase of the current search endpoints.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            CreateTrigramIndex(migrationBuilder, "Tenants", "Name");
            CreateTrigramIndex(migrationBuilder, "Tenants", "Description");
            CreateTrigramIndex(migrationBuilder, "Tenants", "Location");
            CreateTrigramIndex(migrationBuilder, "Tenants", "ContactPerson");
            CreateTrigramIndex(migrationBuilder, "Tenants", "ContactEmail");

            CreateTrigramIndex(migrationBuilder, "Scripts", "Name");
            CreateTrigramIndex(migrationBuilder, "Scripts", "FolderPath");
            CreateTrigramIndex(migrationBuilder, "Scripts", "Description");
            CreateTrigramIndex(migrationBuilder, "Scripts", "ScriptType");

            CreateTrigramIndex(migrationBuilder, "Jobs", "Name");
            CreateTrigramIndex(migrationBuilder, "Jobs", "FolderPath");
            CreateTrigramIndex(migrationBuilder, "Jobs", "Description");
            CreateTrigramIndex(migrationBuilder, "Jobs", "ClientIdentity");

            CreateTrigramIndex(migrationBuilder, "Requests", "SourceSystem");
            CreateTrigramIndex(migrationBuilder, "Requests", "TargetClientIdentity");
            CreateTrigramIndex(migrationBuilder, "Requests", "RundeckJobDefinitionId");
            CreateTrigramIndex(migrationBuilder, "Requests", "RundeckExecutionId");
            CreateTrigramIndex(migrationBuilder, "Requests", "Status");
            CreateTrigramIndex(migrationBuilder, "Requests", "ResultMessage");

            CreateTrigramIndex(migrationBuilder, "JobTaskActivities", "RequestId");
            CreateTrigramIndex(migrationBuilder, "JobTaskActivities", "TaskType");
            CreateTrigramIndex(migrationBuilder, "JobTaskActivities", "Status");
            CreateTrigramIndex(migrationBuilder, "JobTaskActivities", "Error");
            CreateTrigramIndex(migrationBuilder, "JobTaskActivities", "ClientIdentity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropTrigramIndex(migrationBuilder, "Tenants", "Name");
            DropTrigramIndex(migrationBuilder, "Tenants", "Description");
            DropTrigramIndex(migrationBuilder, "Tenants", "Location");
            DropTrigramIndex(migrationBuilder, "Tenants", "ContactPerson");
            DropTrigramIndex(migrationBuilder, "Tenants", "ContactEmail");

            DropTrigramIndex(migrationBuilder, "Scripts", "Name");
            DropTrigramIndex(migrationBuilder, "Scripts", "FolderPath");
            DropTrigramIndex(migrationBuilder, "Scripts", "Description");
            DropTrigramIndex(migrationBuilder, "Scripts", "ScriptType");

            DropTrigramIndex(migrationBuilder, "Jobs", "Name");
            DropTrigramIndex(migrationBuilder, "Jobs", "FolderPath");
            DropTrigramIndex(migrationBuilder, "Jobs", "Description");
            DropTrigramIndex(migrationBuilder, "Jobs", "ClientIdentity");

            DropTrigramIndex(migrationBuilder, "Requests", "SourceSystem");
            DropTrigramIndex(migrationBuilder, "Requests", "TargetClientIdentity");
            DropTrigramIndex(migrationBuilder, "Requests", "RundeckJobDefinitionId");
            DropTrigramIndex(migrationBuilder, "Requests", "RundeckExecutionId");
            DropTrigramIndex(migrationBuilder, "Requests", "Status");
            DropTrigramIndex(migrationBuilder, "Requests", "ResultMessage");

            DropTrigramIndex(migrationBuilder, "JobTaskActivities", "RequestId");
            DropTrigramIndex(migrationBuilder, "JobTaskActivities", "TaskType");
            DropTrigramIndex(migrationBuilder, "JobTaskActivities", "Status");
            DropTrigramIndex(migrationBuilder, "JobTaskActivities", "Error");
            DropTrigramIndex(migrationBuilder, "JobTaskActivities", "ClientIdentity");
        }

        private static void CreateTrigramIndex(MigrationBuilder migrationBuilder, string table, string column) =>
            migrationBuilder.Sql($"CREATE INDEX IF NOT EXISTS \"IX_{table}_GlobalSearch_{column}_trgm\" ON \"{table}\" USING gin (\"{column}\" gin_trgm_ops);");

        private static void DropTrigramIndex(MigrationBuilder migrationBuilder, string table, string column) =>
            migrationBuilder.Sql($"DROP INDEX IF EXISTS \"IX_{table}_GlobalSearch_{column}_trgm\";");
    }
}
