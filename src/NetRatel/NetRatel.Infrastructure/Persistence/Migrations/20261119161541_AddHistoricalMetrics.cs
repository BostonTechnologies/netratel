using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NetRatel.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoricalMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetricBuckets",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    SeriesId = table.Column<long>(type: "bigint", nullable: false),
                    Tier = table.Column<short>(type: "smallint", nullable: false),
                    Bucket = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    Min = table.Column<double>(type: "double precision", nullable: true),
                    Max = table.Column<double>(type: "double precision", nullable: true),
                    Last = table.Column<double>(type: "double precision", nullable: true),
                    LastAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastOrderAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Integral = table.Column<double>(type: "double precision", nullable: false),
                    Covered = table.Column<long>(type: "bigint", nullable: false),
                    Bytes = table.Column<decimal>(type: "numeric(29,0)", precision: 29, scale: 0, nullable: false),
                    TotalBytes = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: true),
                    FreeBytes = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: true),
                    Quality = table.Column<int>(type: "integer", nullable: false),
                    Running = table.Column<long>(type: "bigint", nullable: false),
                    Stopped = table.Column<long>(type: "bigint", nullable: false),
                    Other = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricBuckets", x => new { x.TenantId, x.SeriesId, x.Tier, x.Bucket });
                });

            migrationBuilder.CreateTable(
                name: "MetricInstanceBudgets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    RetainedRows = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricInstanceBudgets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetricPolicies",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    BaseDays = table.Column<int>(type: "integer", nullable: false),
                    FiveMinuteDays = table.Column<int>(type: "integer", nullable: false),
                    HourlyDays = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EnabledSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastPersistedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRollupAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastPurgeAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FiveSealedBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HourSealedBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BasePurgedBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FivePurgedBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetainedRows = table.Column<long>(type: "bigint", nullable: false),
                    SeriesCount = table.Column<int>(type: "integer", nullable: false),
                    LateObservations = table.Column<long>(type: "bigint", nullable: false),
                    PartialFrames = table.Column<long>(type: "bigint", nullable: false),
                    Disposition = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricPolicies", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "MetricPolicyAudits",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Policy = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricPolicyAudits", x => new { x.TenantId, x.Revision });
                });

            migrationBuilder.CreateTable(
                name: "MetricReceipts",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricReceipts", x => new { x.TenantId, x.BatchId });
                });

            migrationBuilder.CreateTable(
                name: "MetricSeries",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Resource = table.Column<string>(type: "character varying(264)", maxLength: 264, nullable: false),
                    Label = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FirstAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSourceAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastIntervalEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    StreamOrdinal = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    Gauge = table.Column<double>(type: "double precision", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: true),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WatchRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    Quality = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricSeries", x => new { x.TenantId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "MetricServiceDetails",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    SeriesId = table.Column<long>(type: "bigint", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricServiceDetails", x => new { x.TenantId, x.SeriesId, x.ObservedAt });
                });

            migrationBuilder.CreateTable(
                name: "MetricWatches",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricWatches", x => new { x.TenantId, x.AgentId, x.Platform, x.Name });
                });

            migrationBuilder.InsertData(
                table: "MetricInstanceBudgets",
                columns: new[] { "Id", "RetainedRows" },
                values: new object[] { 1, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_MetricBuckets_TenantId_Tier_Bucket_SeriesId",
                table: "MetricBuckets",
                columns: new[] { "TenantId", "Tier", "Bucket", "SeriesId" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricReceipts_TenantId_CreatedAt",
                table: "MetricReceipts",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricSeries_TenantId_AgentId_Id",
                table: "MetricSeries",
                columns: new[] { "TenantId", "AgentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricSeries_TenantId_AgentId_Kind_Resource",
                table: "MetricSeries",
                columns: new[] { "TenantId", "AgentId", "Kind", "Resource" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricServiceDetails_TenantId_ObservedAt",
                table: "MetricServiceDetails",
                columns: new[] { "TenantId", "ObservedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetricBuckets");

            migrationBuilder.DropTable(
                name: "MetricInstanceBudgets");

            migrationBuilder.DropTable(
                name: "MetricPolicies");

            migrationBuilder.DropTable(
                name: "MetricPolicyAudits");

            migrationBuilder.DropTable(
                name: "MetricReceipts");

            migrationBuilder.DropTable(
                name: "MetricSeries");

            migrationBuilder.DropTable(
                name: "MetricServiceDetails");

            migrationBuilder.DropTable(
                name: "MetricWatches");
        }
    }
}
