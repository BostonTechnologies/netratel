using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCallbackTerminalRejection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastFailureCode",
                table: "OrchestrationCallbackDeliveries",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastStatusCode",
                table: "OrchestrationCallbackDeliveries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RejectedAtUtc",
                table: "OrchestrationCallbackDeliveries",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastFailureCode",
                table: "OrchestrationCallbackDeliveries");

            migrationBuilder.DropColumn(
                name: "LastStatusCode",
                table: "OrchestrationCallbackDeliveries");

            migrationBuilder.DropColumn(
                name: "RejectedAtUtc",
                table: "OrchestrationCallbackDeliveries");
        }
    }
}
