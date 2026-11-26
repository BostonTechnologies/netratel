using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedJobRunAsGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RunAsGrantId",
                table: "ManagedOrchestrationRequestBindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ManagedJobRunAsGrants",
                columns: table => new
                {
                    MappingId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<long>(type: "bigint", nullable: false),
                    ApprovalId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    ScopeDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagedJobRunAsGrants", x => new { x.MappingId, x.JobId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManagedJobRunAsGrants");

            migrationBuilder.DropColumn(
                name: "RunAsGrantId",
                table: "ManagedOrchestrationRequestBindings");
        }
    }
}
