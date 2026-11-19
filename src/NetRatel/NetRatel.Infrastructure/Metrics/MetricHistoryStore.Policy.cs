using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;
using static NetRatel.Infrastructure.Metrics.MetricMinuteAccumulator;

namespace NetRatel.Infrastructure.Metrics;

public sealed partial class MetricHistoryStore
{
    public async Task<MetricHistoryPolicy> ReadPolicyAsync(int tenant, CancellationToken ct)
    {
        var policy = await ReadPolicyRowAsync(tenant, ct).ConfigureAwait(false);
        if (policy is null) return MetricHistoryPolicy.Default;
        var watches = await _db.Set<MetricWatchRow>().AsNoTracking().Where(item => item.TenantId == tenant)
            .OrderBy(item => item.AgentId).ThenBy(item => item.Platform).ThenBy(item => item.Name)
            .Select(item => new MetricServiceWatch(item.AgentId, item.Platform, item.Name))
            .Take(MetricHistoryLimits.MaximumWatches).ToArrayAsync(ct).ConfigureAwait(false);
        return Policy(policy, watches);
    }

    public async Task<MetricServiceWatch[]> ReadClientWatchesAsync(ClientKey client, CancellationToken ct)
    {
        _db.Database.SetCommandTimeout(2);
        return await (from watch in _db.Set<MetricWatchRow>().AsNoTracking()
                      join policy in _db.Set<MetricPolicyRow>().AsNoTracking() on watch.TenantId equals policy.TenantId
                      where policy.Enabled && watch.TenantId == client.TenantId && watch.AgentId == client.AgentId
                      orderby watch.Platform, watch.Name
                      select new MetricServiceWatch(watch.AgentId, watch.Platform, watch.Name))
            .Take(MetricHistoryLimits.MaximumWatchesPerAgent).ToArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<MetricHistoryPolicy?> SavePolicyAsync(int tenant, MetricHistoryPolicyWrite write, string actor, CancellationToken ct)
    {
        var watches = ValidatePolicy(write);
        _db.Database.SetCommandTimeout(5);
        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await TryLockAsync(tenant, ct).ConfigureAwait(false)) throw new MetricHistoryBusyException();
        var row = await GetOrAddPolicyAsync(tenant, ct).ConfigureAwait(false);
        if (row.Revision != write.ExpectedRevision) return null;
        if (!await _db.Tenants.AnyAsync(item => item.Id == tenant, ct).ConfigureAwait(false))
            throw new ArgumentException("metrics_tenant_not_found");
        var clients = watches.Select(watch => watch.AgentId).Distinct().ToArray();
        var eligible = await _db.Agents.AsNoTracking().CountAsync(agent => agent.TenantId == tenant && clients.Contains(agent.Id) &&
            agent.IsEnabled && agent.Status == NetRatel.Infrastructure.Persistence.AgentStatus.Active && agent.RevokedAtUtc == null, ct).ConfigureAwait(false);
        if (clients.Length != eligible) throw new ArgumentException("metrics_watch_target_not_found");

        var now = time.GetUtcNow();
        row.Revision = checked(row.Revision + 1);
        if (write.Enabled && !row.Enabled) row.EnabledSince = now;
        row.Enabled = write.Enabled;
        row.BaseDays = write.BaseDays;
        row.FiveMinuteDays = write.FiveMinuteDays;
        row.HourlyDays = write.HourlyDays;
        row.UpdatedAt = now;
        var previous = await _db.Set<MetricWatchRow>().AsTracking().Where(item => item.TenantId == tenant).ToArrayAsync(ct).ConfigureAwait(false);
        var keys = watches.ToHashSet();
        foreach (var existing in previous)
            if (!keys.Remove(new(existing.AgentId, existing.Platform, existing.Name))) _db.Remove(existing);
        foreach (var watch in keys)
            _db.Add(new MetricWatchRow { TenantId = tenant, AgentId = watch.AgentId, Platform = watch.Platform, Name = watch.ServiceName });
        var updated = Policy(row, watches);
        _db.Add(new MetricPolicyAuditRow
        {
            TenantId = tenant, Revision = row.Revision, ChangedAt = now, Actor = actor, Reason = write.Reason.Trim(),
            Policy = JsonSerializer.Serialize(updated, MetricHistoryJsonContext.Default.MetricHistoryPolicy)
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return updated;
    }

    public static MetricServiceWatch[] ValidatePolicy(MetricHistoryPolicyWrite write)
    {
        if (write.ExpectedRevision < 0) throw new ArgumentException("invalid_metrics_revision");
        if (write.BaseDays is < 1 or > MetricHistoryLimits.MaximumBaseDays) throw new ArgumentException("invalid_metrics_base_days");
        if (write.FiveMinuteDays < write.BaseDays || write.FiveMinuteDays > MetricHistoryLimits.MaximumFiveMinuteDays)
            throw new ArgumentException("invalid_metrics_five_minute_days");
        if (write.HourlyDays < write.FiveMinuteDays || write.HourlyDays > MetricHistoryLimits.MaximumHourlyDays)
            throw new ArgumentException("invalid_metrics_hourly_days");
        if (string.IsNullOrWhiteSpace(write.Reason) || write.Reason.Length > 512 || write.Reason.Any(char.IsControl))
            throw new ArgumentException("invalid_metrics_reason");
        if (write.Watches is null || write.Watches.Length > MetricHistoryLimits.MaximumWatches) throw new ArgumentException("invalid_metrics_watch_count");

        var keys = new HashSet<MetricServiceWatch>();
        foreach (var watch in write.Watches)
        {
            if (watch is null || watch.AgentId == Guid.Empty || watch.Platform is not (ClientServicePlatform.Windows or ClientServicePlatform.LinuxSystemd) ||
                !ClientServiceContractValidator.IsValidServiceName(watch.ServiceName, watch.Platform))
                throw new ArgumentException("invalid_metrics_watch");
            if (!keys.Add(watch with { ServiceName = MetricHistoryObservations.CanonicalServiceName(watch.Platform, watch.ServiceName) }))
                throw new ArgumentException("duplicate_metrics_watch");
        }
        if (keys.GroupBy(watch => watch.AgentId).Any(grouping => grouping.Count() > MetricHistoryLimits.MaximumWatchesPerAgent))
            throw new ArgumentException("invalid_metrics_watch_count");
        return keys.OrderBy(watch => watch.AgentId).ThenBy(watch => watch.Platform).ThenBy(watch => watch.ServiceName, StringComparer.Ordinal).ToArray();
    }

    public async Task<MetricCollectionStatus> StatusAsync(int tenant, CancellationToken ct) =>
        await StatusAsync(tenant, await ReadPolicyRowAsync(tenant, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    private async Task<MetricCollectionStatus> StatusAsync(int tenant, MetricPolicyRow? policy, CancellationToken ct)
    {
        var oldest = policy is null ? null : await OldestBucketAsync(tenant, 60, policy.FiveSealedBefore, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var disposition = policy?.Disposition ?? (oldest < now.Subtract(Lateness).AddMinutes(-5) ? "maintenance_lag" : null);
        var sealedBefore = Max(policy?.FiveSealedBefore ?? default, Floor(now.Subtract(Lateness), 60));
        return new(policy?.Enabled ?? true, policy?.LastAcceptedAt, policy?.LastPersistedAt, policy?.LastRollupAt,
            policy?.LastPurgeAt, oldest, policy?.RetainedRows ?? 0, TenantRowLimit, InstanceRowLimit,
            0, 0, null, 0, policy?.LateObservations ?? 0, policy?.PartialFrames ?? 0, disposition, sealedBefore);
    }

    private Task<MetricPolicyRow?> ReadPolicyRowAsync(int tenant, CancellationToken ct) => _db.Set<MetricPolicyRow>().AsNoTracking()
        .SingleOrDefaultAsync(item => item.TenantId == tenant, ct);

    private static MetricHistoryPolicy Policy(MetricPolicyRow policy, MetricServiceWatch[] watches) =>
        new(policy.Revision, policy.Enabled, policy.BaseDays, policy.FiveMinuteDays, policy.HourlyDays, watches, policy.UpdatedAt);
}
