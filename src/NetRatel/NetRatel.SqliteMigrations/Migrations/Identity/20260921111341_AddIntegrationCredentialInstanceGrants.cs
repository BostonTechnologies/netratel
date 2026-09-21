using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.SqliteMigrations.Migrations.Identity
{
    /// <inheritdoc />
    public partial class AddIntegrationCredentialInstanceGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrationCredentialInstanceGrants",
                columns: table => new
                {
                    CredentialId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Permission = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationCredentialInstanceGrants", x => new { x.CredentialId, x.Permission });
                    table.ForeignKey(
                        name: "FK_IntegrationCredentialInstanceGrants_IntegrationCredentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "IntegrationCredentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationCredentialInstanceGrants");
        }
    }
}
