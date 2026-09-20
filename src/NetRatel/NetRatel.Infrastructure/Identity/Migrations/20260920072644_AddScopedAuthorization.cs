using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddScopedAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    IsInstanceAdministratorRole = table.Column<bool>(type: "boolean", nullable: false),
                    DelegationRank = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessRolePermissions",
                columns: table => new
                {
                    RoleId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Permission = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRolePermissions", x => new { x.RoleId, x.Permission });
                    table.ForeignKey(
                        name: "FK_AccessRolePermissions_AccessRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AccessRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrincipalRoleAssignments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PrincipalId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RoleId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByPrincipalId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrincipalRoleAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrincipalRoleAssignments_AccessRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AccessRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoles_IsBuiltIn_DelegationRank",
                table: "AccessRoles",
                columns: new[] { "IsBuiltIn", "DelegationRank" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRoles_Name",
                table: "AccessRoles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrincipalRoleAssignments_PrincipalId_TenantId",
                table: "PrincipalRoleAssignments",
                columns: new[] { "PrincipalId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_PrincipalRoleAssignments_RoleId_TenantId",
                table: "PrincipalRoleAssignments",
                columns: new[] { "RoleId", "TenantId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessRolePermissions");

            migrationBuilder.DropTable(
                name: "PrincipalRoleAssignments");

            migrationBuilder.DropTable(
                name: "AccessRoles");
        }
    }
}
