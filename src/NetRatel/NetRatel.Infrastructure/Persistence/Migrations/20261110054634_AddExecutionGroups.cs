using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Agents_TenantId_Id",
                table: "Agents",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "ExecutionGroupAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    MembershipJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionGroupAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionGroupAudits_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionGroups", x => x.Id);
                    table.UniqueConstraint("AK_ExecutionGroups_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_ExecutionGroups_Revision", "\"Revision\" > 0");
                    table.ForeignKey(
                        name: "FK_ExecutionGroups_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionGroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionGroupMembers", x => new { x.GroupId, x.AgentId });
                    table.ForeignKey(
                        name: "FK_ExecutionGroupMembers_Agents_TenantId_AgentId",
                        columns: x => new { x.TenantId, x.AgentId },
                        principalTable: "Agents",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExecutionGroupMembers_ExecutionGroups_TenantId_GroupId",
                        columns: x => new { x.TenantId, x.GroupId },
                        principalTable: "ExecutionGroups",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobRunNodes_TenantId_AgentId",
                table: "JobRunNodes",
                columns: new[] { "TenantId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionGroupAudits_TenantId_GroupId_CreatedAtUtc",
                table: "ExecutionGroupAudits",
                columns: new[] { "TenantId", "GroupId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionGroupMembers_TenantId_AgentId",
                table: "ExecutionGroupMembers",
                columns: new[] { "TenantId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionGroupMembers_TenantId_GroupId",
                table: "ExecutionGroupMembers",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionGroups_TenantId_Archived_Name",
                table: "ExecutionGroups",
                columns: new[] { "TenantId", "Archived", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionGroups_TenantId_NormalizedName",
                table: "ExecutionGroups",
                columns: new[] { "TenantId", "NormalizedName" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_JobRunNodes_Agents_TenantId_AgentId",
                table: "JobRunNodes",
                columns: new[] { "TenantId", "AgentId" },
                principalTable: "Agents",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobRunNodes_Agents_TenantId_AgentId",
                table: "JobRunNodes");

            migrationBuilder.DropTable(
                name: "ExecutionGroupAudits");

            migrationBuilder.DropTable(
                name: "ExecutionGroupMembers");

            migrationBuilder.DropTable(
                name: "ExecutionGroups");

            migrationBuilder.DropIndex(
                name: "IX_JobRunNodes_TenantId_AgentId",
                table: "JobRunNodes");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Agents_TenantId_Id",
                table: "Agents");
        }
    }
}
