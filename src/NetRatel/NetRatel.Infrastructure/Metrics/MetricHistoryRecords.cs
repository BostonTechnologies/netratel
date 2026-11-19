using Microsoft.EntityFrameworkCore;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Infrastructure.Metrics;

internal sealed class MetricPolicyRow
{
    public int TenantId { get; set; }
    public long Revision { get; set; }
    public bool Enabled { get; set; } = true;
    public int BaseDays { get; set; } = MetricHistoryLimits.BaseDays;
    public int FiveMinuteDays { get; set; } = MetricHistoryLimits.FiveMinuteDays;
    public int HourlyDays { get; set; } = MetricHistoryLimits.HourlyDays;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? EnabledSince { get; set; }
    public DateTimeOffset? LastAcceptedAt { get; set; }
    public DateTimeOffset? LastPersistedAt { get; set; }
    public DateTimeOffset? LastRollupAt { get; set; }
    public DateTimeOffset? LastPurgeAt { get; set; }
    public DateTimeOffset? FiveSealedBefore { get; set; }
    public DateTimeOffset? HourSealedBefore { get; set; }
    public DateTimeOffset? BasePurgedBefore { get; set; }
    public DateTimeOffset? FivePurgedBefore { get; set; }
    public long RetainedRows { get; set; }
    public int SeriesCount { get; set; }
    public long LateObservations { get; set; }
    public long PartialFrames { get; set; }
    public string? Disposition { get; set; }
}

internal sealed class MetricWatchRow
{
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public ClientServicePlatform Platform { get; set; }
    public required string Name { get; set; }
}

internal sealed class MetricPolicyAuditRow
{
    public int TenantId { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public required string Actor { get; set; }
    public required string Reason { get; set; }
    public required string Policy { get; set; }
}

internal sealed class MetricSeriesRow
{
    public int TenantId { get; set; }
    public long Id { get; set; }
    public Guid AgentId { get; set; }
    public MetricHistoryKind Kind { get; set; }
    public required string Resource { get; set; }
    public required string Label { get; set; }
    public DateTimeOffset FirstAt { get; set; }
    public DateTimeOffset LastAt { get; set; }
    public DateTimeOffset? LastSourceAt { get; set; }
    public DateTimeOffset? LastIntervalEnd { get; set; }
    public Guid StreamId { get; set; }
    public long StreamOrdinal { get; set; }
    public decimal Sequence { get; set; }
    public double? Gauge { get; set; }
    public ClientServiceState? State { get; set; }
    public Guid? CollectionId { get; set; }
    public decimal WatchRevision { get; set; }
    public MetricHistoryQuality Quality { get; set; }
}

internal sealed class MetricBucketRow
{
    public int TenantId { get; set; }
    public long SeriesId { get; set; }
    public short Tier { get; set; }
    public DateTimeOffset Bucket { get; set; }
    public long Count { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Last { get; set; }
    public DateTimeOffset? LastAt { get; set; }
    public DateTimeOffset? LastOrderAt { get; set; }
    public double Integral { get; set; }
    public long Covered { get; set; }
    public decimal Bytes { get; set; }
    public decimal? TotalBytes { get; set; }
    public decimal? FreeBytes { get; set; }
    public MetricHistoryQuality Quality { get; set; }
    public long Running { get; set; }
    public long Stopped { get; set; }
    public long Other { get; set; }
}

internal sealed class MetricServiceDetailRow
{
    public int TenantId { get; set; }
    public long SeriesId { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public ClientServiceState State { get; set; }
}

internal sealed class MetricReceiptRow
{
    public int TenantId { get; set; }
    public Guid BatchId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class MetricInstanceBudgetRow
{
    public int Id { get; set; }
    public long RetainedRows { get; set; }
}

internal static class MetricPersistenceModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<MetricPolicyRow>(row =>
        {
            row.ToTable("MetricPolicies");
            row.HasKey(x => x.TenantId);
            row.Property(x => x.TenantId).ValueGeneratedNever();
            row.Property(x => x.Disposition).HasMaxLength(40);
        });
        model.Entity<MetricWatchRow>(row =>
        {
            row.ToTable("MetricWatches");
            row.HasKey(x => new { x.TenantId, x.AgentId, x.Platform, x.Name });
            row.Property(x => x.Name).HasMaxLength(ClientServicesLimits.MaximumNameLength);
        });
        model.Entity<MetricPolicyAuditRow>(row =>
        {
            row.ToTable("MetricPolicyAudits");
            row.HasKey(x => new { x.TenantId, x.Revision });
            row.Property(x => x.Actor).HasMaxLength(256);
            row.Property(x => x.Reason).HasMaxLength(512);
            row.Property(x => x.Policy).HasColumnType("jsonb");
        });
        model.Entity<MetricSeriesRow>(row =>
        {
            row.ToTable("MetricSeries");
            row.HasKey(x => new { x.TenantId, x.Id });
            row.Property(x => x.Id).UseIdentityAlwaysColumn();
            row.Property(x => x.Resource).HasMaxLength(ClientServicesLimits.MaximumNameLength + 8);
            row.Property(x => x.Label).HasMaxLength(ClientServicesLimits.MaximumNameLength);
            row.Property(x => x.Sequence).HasPrecision(20, 0);
            row.Property(x => x.WatchRevision).HasPrecision(20, 0);
            row.HasIndex(x => new { x.TenantId, x.AgentId, x.Kind, x.Resource }).IsUnique();
            row.HasIndex(x => new { x.TenantId, x.AgentId, x.Id });
        });
        model.Entity<MetricBucketRow>(row =>
        {
            row.ToTable("MetricBuckets");
            row.HasKey(x => new { x.TenantId, x.SeriesId, x.Tier, x.Bucket });
            row.HasIndex(x => new { x.TenantId, x.Tier, x.Bucket, x.SeriesId });
            row.Property(x => x.Bytes).HasPrecision(29, 0);
            row.Property(x => x.TotalBytes).HasPrecision(20, 0);
            row.Property(x => x.FreeBytes).HasPrecision(20, 0);
        });
        model.Entity<MetricServiceDetailRow>(row =>
        {
            row.ToTable("MetricServiceDetails");
            row.HasKey(x => new { x.TenantId, x.SeriesId, x.ObservedAt });
            row.HasIndex(x => new { x.TenantId, x.ObservedAt });
        });
        model.Entity<MetricReceiptRow>(row =>
        {
            row.ToTable("MetricReceipts");
            row.HasKey(x => new { x.TenantId, x.BatchId });
            row.HasIndex(x => new { x.TenantId, x.CreatedAt });
        });
        model.Entity<MetricInstanceBudgetRow>(row =>
        {
            row.ToTable("MetricInstanceBudgets");
            row.HasKey(x => x.Id);
            row.Property(x => x.Id).ValueGeneratedNever();
            row.HasData(new MetricInstanceBudgetRow { Id = 1 });
        });
    }
}
