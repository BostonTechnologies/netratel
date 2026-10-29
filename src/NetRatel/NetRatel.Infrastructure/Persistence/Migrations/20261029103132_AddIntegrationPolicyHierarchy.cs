using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationPolicyHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid[]>(
                name: "AgentIds",
                table: "McpOperatorPolicies",
                type: "uuid[]",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "ClientTags",
                table: "McpOperatorPolicies",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntegrationCredentialId",
                table: "McpOperatorPolicies",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsContainer",
                table: "McpOperatorPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string[]>(
                name: "Operations",
                table: "McpOperatorPolicies",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentPolicyId",
                table: "McpOperatorPolicies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Resource",
                table: "McpOperatorPolicies",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicies_IntegrationCredentialId_Environment_Ten~",
                table: "McpOperatorPolicies",
                columns: new[] { "IntegrationCredentialId", "Environment", "TenantId" },
                unique: true,
                filter: "\"IntegrationCredentialId\" IS NOT NULL AND \"ParentPolicyId\" IS NULL AND \"LifecycleState\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_McpOperatorPolicies_ParentPolicyId",
                table: "McpOperatorPolicies",
                column: "ParentPolicyId");

            migrationBuilder.AddForeignKey(
                name: "FK_McpOperatorPolicies_McpOperatorPolicies_ParentPolicyId",
                table: "McpOperatorPolicies",
                column: "ParentPolicyId",
                principalTable: "McpOperatorPolicies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_McpOperatorPolicies_McpOperatorPolicies_ParentPolicyId",
                table: "McpOperatorPolicies");

            migrationBuilder.DropIndex(
                name: "IX_McpOperatorPolicies_IntegrationCredentialId_Environment_Ten~",
                table: "McpOperatorPolicies");

            migrationBuilder.DropIndex(
                name: "IX_McpOperatorPolicies_ParentPolicyId",
                table: "McpOperatorPolicies");

            migrationBuilder.DropColumn(
                name: "AgentIds",
                table: "McpOperatorPolicies");

            migrationBuilder.DropColumn(
                name: "ClientTags",
                table: "McpOperatorPolicies");

            migrationBuilder.DropColumn(
                name: "IntegrationCredentialId",
                table: "McpOperatorPolicies");

            migrationBuilder.DropColumn(
                name: "IsContainer",
                table: "McpOperatorPolicies");

            migrationBuilder.DropColumn(
                name: "Operations",
                table: "McpOperatorPolicies");

            migrationBuilder.DropColumn(
                name: "ParentPolicyId",
                table: "McpOperatorPolicies");

            migrationBuilder.DropColumn(
                name: "Resource",
                table: "McpOperatorPolicies");
        }
    }
}
