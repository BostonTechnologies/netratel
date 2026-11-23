using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PinJobProviderInputs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PinnedProviderInputBindingsJson",
                table: "JobAggregateControls",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderInputBindingsJson",
                table: "JobAggregateControls",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "JobAggregateControls" WHERE "ProviderInputBindingsJson" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove the protected job input binding schema while binding history exists';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "PinnedProviderInputBindingsJson",
                table: "JobAggregateControls");

            migrationBuilder.DropColumn(
                name: "ProviderInputBindingsJson",
                table: "JobAggregateControls");
        }
    }
}
