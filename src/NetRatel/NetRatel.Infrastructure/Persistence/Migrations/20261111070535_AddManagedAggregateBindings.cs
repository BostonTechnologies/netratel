using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedAggregateBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "AgentId",
                table: "ManagedOrchestrationRequestBindings",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "TargetPlanDigest",
                table: "ManagedOrchestrationRequestBindings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetPlanDigest",
                table: "ManagedOrchestrationRequestBindings");

            migrationBuilder.AlterColumn<Guid>(
                name: "AgentId",
                table: "ManagedOrchestrationRequestBindings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
