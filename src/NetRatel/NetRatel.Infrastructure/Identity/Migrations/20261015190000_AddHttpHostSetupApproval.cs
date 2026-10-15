using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Identity.Migrations
{
    public partial class AddHttpHostSetupApproval : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HttpHostSetupRequests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserCodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DeviceCapabilityHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PublicOrigin = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Resource = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ServicePrincipal = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RegistrationFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextPollAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_HttpHostSetupRequests", x => x.Id));
            migrationBuilder.CreateIndex(name: "IX_HttpHostSetupRequests_DeviceCapabilityHash", table: "HttpHostSetupRequests", column: "DeviceCapabilityHash", unique: true);
            migrationBuilder.CreateIndex(name: "IX_HttpHostSetupRequests_UserCodeHash", table: "HttpHostSetupRequests", column: "UserCodeHash", unique: true);
            migrationBuilder.CreateIndex(name: "IX_HttpHostSetupRequests_ExpiresAtUtc", table: "HttpHostSetupRequests", column: "ExpiresAtUtc");
        }
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "HttpHostSetupRequests");
    }
}
