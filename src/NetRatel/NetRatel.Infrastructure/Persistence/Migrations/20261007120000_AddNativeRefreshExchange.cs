using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations;

public partial class AddNativeRefreshExchange : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("ExchangeId", "AgentRefreshTokens", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<int>("ExchangeTenantId", "AgentRefreshTokens", type: "integer", nullable: true);
        migrationBuilder.AddColumn<string>("ExchangeKeyHash", "AgentRefreshTokens", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("ExchangeRequestedScopesJson", "AgentRefreshTokens", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("ExchangeGrantedScopesJson", "AgentRefreshTokens", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("ExchangeMtlsThumbprint", "AgentRefreshTokens", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("ProtectedSuccessorToken", "AgentRefreshTokens", type: "text", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("ExchangeAcknowledgedAtUtc", "AgentRefreshTokens", type: "timestamp with time zone", nullable: true);
        migrationBuilder.CreateIndex("IX_AgentRefreshTokens_AgentId_ExchangeId", "AgentRefreshTokens",
            columns: new[] { "AgentId", "ExchangeId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_AgentRefreshTokens_AgentId_ExchangeId", "AgentRefreshTokens");
        migrationBuilder.DropColumn("ExchangeId", "AgentRefreshTokens");
        migrationBuilder.DropColumn("ExchangeTenantId", "AgentRefreshTokens");
        migrationBuilder.DropColumn("ExchangeKeyHash", "AgentRefreshTokens");
        migrationBuilder.DropColumn("ExchangeRequestedScopesJson", "AgentRefreshTokens");
        migrationBuilder.DropColumn("ExchangeGrantedScopesJson", "AgentRefreshTokens");
        migrationBuilder.DropColumn("ExchangeMtlsThumbprint", "AgentRefreshTokens");
        migrationBuilder.DropColumn("ProtectedSuccessorToken", "AgentRefreshTokens");
        migrationBuilder.DropColumn("ExchangeAcknowledgedAtUtc", "AgentRefreshTokens");
    }
}
