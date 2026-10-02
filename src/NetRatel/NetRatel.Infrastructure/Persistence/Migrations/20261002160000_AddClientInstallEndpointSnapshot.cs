using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20261002160000_AddClientInstallEndpointSnapshot")]
public sealed class AddClientInstallEndpointSnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Existing scripts are immutable. Unknown historical gateway/provenance remains null.
        migrationBuilder.AddColumn<string>("EffectiveGatewayBaseUrl", "ClientInstallGrants",
            type: "character varying(2048)", maxLength: 2048, nullable: true);
        migrationBuilder.AddColumn<string>("PublicWebSource", "ClientInstallGrants",
            type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>("PublicApiSource", "ClientInstallGrants",
            type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>("GatewaySource", "ClientInstallGrants",
            type: "character varying(64)", maxLength: 64, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("EffectiveGatewayBaseUrl", "ClientInstallGrants");
        migrationBuilder.DropColumn("PublicWebSource", "ClientInstallGrants");
        migrationBuilder.DropColumn("PublicApiSource", "ClientInstallGrants");
        migrationBuilder.DropColumn("GatewaySource", "ClientInstallGrants");
    }
}
