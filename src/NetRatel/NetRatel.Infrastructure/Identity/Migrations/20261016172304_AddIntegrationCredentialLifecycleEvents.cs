using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationCredentialLifecycleEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrationCredentialLifecycleEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CredentialId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorPrincipalId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationCredentialLifecycleEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCredentialLifecycleEvents_CredentialId_OccurredA~",
                table: "IntegrationCredentialLifecycleEvents",
                columns: new[] { "CredentialId", "OccurredAtUtc" });

            // Preserve existing revocation attribution before those management records can be deleted.
            migrationBuilder.Sql("""
                INSERT INTO "IntegrationCredentialLifecycleEvents"
                    ("Id", "CredentialId", "OwnerPrincipalId", "ActorPrincipalId", "Action", "OccurredAtUtc")
                SELECT "Id", "Id", "OwnerPrincipalId", COALESCE("RevokedByPrincipalId", "OwnerPrincipalId"), 'revoked', "RevokedAtUtc"
                FROM "IntegrationCredentials" WHERE "RevokedAtUtc" IS NOT NULL
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationCredentialLifecycleEvents");
        }
    }
}
