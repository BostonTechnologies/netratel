using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDevelopmentMcpFileArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DevelopmentMcpFileArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetGrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MarkerOwned = table.Column<bool>(type: "boolean", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevelopmentMcpFileArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevelopmentMcpFileArtifacts_DevelopmentOperatorTargetGrants~",
                        column: x => x.TargetGrantId,
                        principalTable: "DevelopmentOperatorTargetGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpFileArtifacts_TargetGrantId_CreatedAtUtc",
                table: "DevelopmentMcpFileArtifacts",
                columns: new[] { "TargetGrantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevelopmentMcpFileArtifacts_TenantId_AgentId_ExpiresAtUtc",
                table: "DevelopmentMcpFileArtifacts",
                columns: new[] { "TenantId", "AgentId", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DevelopmentMcpFileArtifacts");
        }
    }
}
