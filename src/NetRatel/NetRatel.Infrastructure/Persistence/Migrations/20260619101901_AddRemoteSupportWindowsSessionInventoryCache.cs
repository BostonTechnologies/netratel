using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteSupportWindowsSessionInventoryCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientWindowsSessionInventoryRefreshes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RefreshRequestId = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    ClientIdentity = table.Column<string>(type: "text", nullable: false),
                    RequesterIdentity = table.Column<string>(type: "text", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Completed = table.Column<bool>(type: "boolean", nullable: false),
                    InventorySequence = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientWindowsSessionInventoryRefreshes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientWindowsSessionSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    ClientIdentity = table.Column<string>(type: "text", nullable: false),
                    WindowsSessionId = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: true),
                    Domain = table.Column<string>(type: "text", nullable: true),
                    DisplayLabel = table.Column<string>(type: "text", nullable: true),
                    UserSidHash = table.Column<string>(type: "text", nullable: true),
                    IsConsoleSession = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsConnected = table.Column<bool>(type: "boolean", nullable: false),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false),
                    IsWinlogon = table.Column<bool>(type: "boolean", nullable: false),
                    IsAssistable = table.Column<bool>(type: "boolean", nullable: false),
                    SessionType = table.Column<string>(type: "text", nullable: true),
                    Provider = table.Column<string>(type: "text", nullable: true),
                    HelperConnected = table.Column<bool>(type: "boolean", nullable: false),
                    HelperVersionMatches = table.Column<bool>(type: "boolean", nullable: false),
                    HelperLaunchable = table.Column<bool>(type: "boolean", nullable: false),
                    HelperRepairable = table.Column<bool>(type: "boolean", nullable: false),
                    HelperPid = table.Column<int>(type: "integer", nullable: true),
                    HelperVersion = table.Column<string>(type: "text", nullable: true),
                    InventorySequence = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Stale = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientWindowsSessionSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemoteSupportTargetSelectionEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    RequesterIdentity = table.Column<string>(type: "text", nullable: false),
                    ClientIdentity = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<string>(type: "text", nullable: true),
                    TargetMode = table.Column<string>(type: "text", nullable: false),
                    TargetWindowsSessionId = table.Column<int>(type: "integer", nullable: true),
                    TargetUserSidHash = table.Column<string>(type: "text", nullable: true),
                    TargetDisplayLabel = table.Column<string>(type: "text", nullable: true),
                    SelectedProvider = table.Column<string>(type: "text", nullable: true),
                    InventorySequence = table.Column<decimal>(type: "numeric(20,0)", nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Result = table.Column<string>(type: "text", nullable: false),
                    FailureReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSupportTargetSelectionEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionInventoryRefreshes_ClientIdentity_Compl~",
                table: "ClientWindowsSessionInventoryRefreshes",
                columns: new[] { "ClientIdentity", "Completed" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionInventoryRefreshes_RefreshRequestId",
                table: "ClientWindowsSessionInventoryRefreshes",
                column: "RefreshRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionInventoryRefreshes_TenantId_ClientIdent~",
                table: "ClientWindowsSessionInventoryRefreshes",
                columns: new[] { "TenantId", "ClientIdentity", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionSnapshots_ClientIdentity_InventorySeque~",
                table: "ClientWindowsSessionSnapshots",
                columns: new[] { "ClientIdentity", "InventorySequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionSnapshots_ClientIdentity_WindowsSession~",
                table: "ClientWindowsSessionSnapshots",
                columns: new[] { "ClientIdentity", "WindowsSessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionSnapshots_ExpiresAtUtc",
                table: "ClientWindowsSessionSnapshots",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ClientWindowsSessionSnapshots_TenantId_ClientIdentity",
                table: "ClientWindowsSessionSnapshots",
                columns: new[] { "TenantId", "ClientIdentity" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportTargetSelectionEvents_SessionId",
                table: "RemoteSupportTargetSelectionEvents",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSupportTargetSelectionEvents_TenantId_ClientIdentity_~",
                table: "RemoteSupportTargetSelectionEvents",
                columns: new[] { "TenantId", "ClientIdentity", "RequestedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientWindowsSessionInventoryRefreshes");

            migrationBuilder.DropTable(
                name: "ClientWindowsSessionSnapshots");

            migrationBuilder.DropTable(
                name: "RemoteSupportTargetSelectionEvents");
        }
    }
}
