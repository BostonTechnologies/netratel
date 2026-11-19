using System.Globalization;
using System.Security.Claims;
using NetRatel.Application.Agents;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Metrics;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.API.Services.Monitoring;

public sealed class MetricHistoryApiService(MetricHistoryStore store, MetricHistoryWorker worker, IAgentManagementService agents,
    IMonitoringResourceAuthorizer authorization, IMonitoringClientDirectory directory, IMonitoringConfigurationStore configurations,
    IClientServicesRouter services, IClientPresenceReadModel presence, TimeProvider time)
{
    public async Task<MetricCatalogue> CatalogueAsync(int tenant, Guid agent, int count, string? cursor, ClaimsPrincipal user, CancellationToken ct)
    {
        await RequireAgentAsync(tenant, agent, NetRatelPermissions.MonitoringRead, user, ct).ConfigureAwait(false);
        if (count is < 1 or > MetricHistoryLimits.MaximumCataloguePage) throw new MonitoringApiException(400, "invalid_metrics_page");
        var after = ReadCursor(cursor, tenant, agent);
        var catalogue = await store.CatalogueAsync(tenant, agent, after, count, ct).ConfigureAwait(false);
        var current = await presence.GetClientSnapshotAsync(new ClientKey(tenant, agent), ct).ConfigureAwait(false);
        await RequireAgentAsync(tenant, agent, NetRatelPermissions.MonitoringRead, user, ct).ConfigureAwait(false);
        return catalogue with { Status = worker.IncludeQueue(tenant, catalogue.Status),
            ClientOnline = current is null ? null : current.Status == ClientPresenceStatus.Online };
    }

    public async Task<MetricHistoryResponse> QueryAsync(int tenant, Guid agent, MetricHistoryRange range, ClaimsPrincipal user, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await RequireAgentAsync(tenant, agent, NetRatelPermissions.MonitoringRead, user, ct).ConfigureAwait(false);
        if (range.SeriesIds is null || range.SeriesIds.Length is < 1 or > MetricHistoryLimits.MaximumSeriesPerQuery ||
            range.SeriesIds.Any(id => id <= 0) || range.SeriesIds.Distinct().Count() != range.SeriesIds.Length)
            throw new MonitoringApiException(400, "invalid_metrics_series_selection");
        if (range.FromUtc < now.AddDays(-MetricHistoryLimits.MaximumHourlyDays - 1) || range.ToExclusiveUtc > now.AddSeconds(10) ||
            range.FromUtc >= range.ToExclusiveUtc || range.ToExclusiveUtc - range.FromUtc > TimeSpan.FromDays(MetricHistoryLimits.MaximumHourlyDays))
            throw new MonitoringApiException(400, "invalid_metrics_range");
        if (range.ResolutionSeconds is < 60 or > 86_400 || range.MaximumPointsPerSeries is < 2 or > MetricHistoryLimits.MaximumPointsPerSeries)
            throw new MonitoringApiException(400, "invalid_metrics_resolution");

        var series = await store.ScopeAsync(tenant, agent, range.SeriesIds, ct).ConfigureAwait(false);
        if (series.Count != range.SeriesIds.Length) throw new MonitoringApiException(404, "metrics_series_not_found");
        foreach (var item in series)
            await RequireAsync(user, NetRatelPermissions.MonitoringRead, new(tenant, MonitoringResourceKind.Series, AgentId: agent,
                ResourceKey: item.ResourceKey), ct).ConfigureAwait(false);
        try
        {
            var response = await store.QueryAsync(tenant, range.ForAgent(agent), ct).ConfigureAwait(false);
            await RequireAgentAsync(tenant, agent, NetRatelPermissions.MonitoringRead, user, ct).ConfigureAwait(false);
            return MonitoringApiService.Bounded(response with { Status = worker.IncludeQueue(tenant, response.Status) });
        }
        catch (KeyNotFoundException) { throw new MonitoringApiException(404, "metrics_series_not_found"); }
        catch (MetricHistoryBusyException) { throw new MonitoringApiException(429, "metrics_query_capacity"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MonitoringApiException(503, "metrics_query_timeout"); }
    }

    public async Task<MetricHistorySettings> SettingsAsync(int tenant, Guid? agent, ClaimsPrincipal user, CancellationToken ct)
    {
        var target = new MonitoringResource(tenant, MonitoringResourceKind.Tenant);
        var permission = await authorization.AuthorizeAsync(user, NetRatelPermissions.MonitoringRead, target, ct).ConfigureAwait(false)
            ? NetRatelPermissions.MonitoringRead : NetRatelPermissions.MonitoringManage;
        await RequireAsync(user, permission, target, ct).ConfigureAwait(false);
        if (agent is { } selected) await RequireAgentAsync(tenant, selected, permission, user, ct).ConfigureAwait(false);
        var policy = await store.ReadPolicyAsync(tenant, ct).ConfigureAwait(false);
        var status = worker.IncludeQueue(tenant, await store.StatusAsync(tenant, ct).ConfigureAwait(false));
        var activations = await ActivationsAsync(tenant, agent, policy, ct).ConfigureAwait(false);
        await RequireAsync(user, permission, target, ct).ConfigureAwait(false);
        return new(policy, status, activations);
    }

    public async Task<MetricHistorySettings> SaveAsync(int tenant, MetricHistoryPolicyWrite body, ClaimsPrincipal user, CancellationToken ct)
    {
        var target = new MonitoringResource(tenant, MonitoringResourceKind.Tenant);
        await RequireAsync(user, NetRatelPermissions.MonitoringManage, target, ct).ConfigureAwait(false);
        if (!Guid.TryParse(user.FindFirstValue("netratel_principal_id"), out var actor) || actor == Guid.Empty)
            throw new MonitoringApiException(401, "monitoring_operator_identity_required");

        var watches = Validate(body);
        var configuration = await configurations.GetAsync(tenant, ct).ConfigureAwait(false);
        if (configuration.TenantId != tenant) throw new InvalidOperationException("Unexpected monitoring configuration tenant.");
        foreach (var group in watches.GroupBy(watch => watch.AgentId))
        {
            await RequireAgentAsync(tenant, group.Key, NetRatelPermissions.MonitoringManage, user, ct).ConfigureAwait(false);
            var client = new ClientKey(tenant, group.Key);
            if (!await directory.IsEligibleAsync(client, ct).ConfigureAwait(false))
                throw new MonitoringApiException(404, "metrics_watch_target_not_found");
            var names = MonitoringServiceWatchPolicySource.SelectNames(configuration, group.Key, true)
                .Concat(group.Select(watch => watch.ServiceName)).Distinct(StringComparer.Ordinal).ToArray();
            if (!MonitoringServiceWatchPolicySource.Valid(client, names, time.GetUtcNow()))
                throw new MonitoringApiException(400, "service_watch_capacity_exceeded");
        }
        await RequireAsync(user, NetRatelPermissions.MonitoringManage, target, ct).ConfigureAwait(false);
        MetricHistoryPolicy? policy;
        try { policy = await store.SavePolicyAsync(tenant, body, actor.ToString("D"), ct).ConfigureAwait(false); }
        catch (MetricHistoryBusyException) { throw new MonitoringApiException(409, "metrics_settings_busy"); }
        catch (ArgumentException exception) when (exception.Message is "metrics_watch_target_not_found" or "metrics_tenant_not_found")
        { throw new MonitoringApiException(404, exception.Message); }
        if (policy is null) throw new MonitoringApiException(409, "metrics_revision_conflict");
        return new(policy, worker.IncludeQueue(tenant, await store.StatusAsync(tenant, ct).ConfigureAwait(false)),
            await ActivationsAsync(tenant, null, policy, ct).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<MetricWatchActivation>> ActivationsAsync(int tenant, Guid? agent, MetricHistoryPolicy policy, CancellationToken ct)
    {
        if (agent is null && policy.Watches.Length == 0) return [];
        var configuration = await configurations.GetAsync(tenant, ct).ConfigureAwait(false);
        if (configuration.TenantId != tenant) throw new InvalidOperationException("Unexpected monitoring configuration tenant.");
        var keys = agent is { } selected ? [selected] : policy.Watches.Select(watch => watch.AgentId).Distinct().ToArray();
        var eligible = (await directory.GetEligibleAgentsAsync(tenant, ct).ConfigureAwait(false)).ToHashSet();
        var result = new List<MetricWatchActivation>();
        foreach (var agentId in keys)
        {
            var client = new ClientKey(tenant, agentId);
            var required = MonitoringServiceWatchPolicySource.SelectAlertWatches(configuration, agentId, eligible.Contains(agentId));
            var selectedWatches = policy.Watches.Where(watch => watch.AgentId == agentId).ToArray();
            var assigned = MonitoringServiceWatchPolicySource.Combine(client, required,
                policy.Enabled && eligible.Contains(agentId) ? selectedWatches : [], time.GetUtcNow()).ToHashSet(StringComparer.Ordinal);
            var state = agent.HasValue && eligible.Contains(agentId) ? await services.GetSnapshotAsync(client, ct).ConfigureAwait(false) : null;
            foreach (var watch in selectedWatches.Union(agent.HasValue ? required : []))
            {
                var active = assigned.Contains(watch.ServiceName);
                var comparer = ClientServiceContractValidator.GetServiceNameComparer(watch.Platform);
                var fresh = state?.LatestAttempt is { Kind: ServiceSnapshotKind.Watch, Status: ServiceCollectionStatus.Complete, IsFinal: true } attempt &&
                    attempt.WatchPolicyRevision == state.WatchPolicyRevision && time.GetUtcNow() - attempt.ReceivedAtUtc < TimeSpan.FromSeconds(60) &&
                    state.WatchedServices.Any(item => item.Platform == watch.Platform && comparer.Equals(item.Name, watch.ServiceName) && item.ObservedAtUtc == attempt.ObservedAtUtc);
                result.Add(new(watch, selectedWatches.Contains(watch), required.Contains(watch), active, active && !fresh));
            }
        }
        return result;
    }

    private async Task RequireAgentAsync(int tenant, Guid agent, string permission, ClaimsPrincipal user, CancellationToken ct)
    {
        if (agent == Guid.Empty) throw new MonitoringApiException(400, "invalid_metrics_agent");
        await RequireAsync(user, permission, new(tenant, MonitoringResourceKind.Agent, AgentId: agent), ct).ConfigureAwait(false);
        var current = await agents.GetAsync(tenant, agent, ct).ConfigureAwait(false);
        if (current is null || current.TenantId != tenant || current.AgentId != agent || current.DeletedAtUtc is not null)
            throw new MonitoringApiException(404, "metrics_agent_not_found");
    }

    private async Task RequireAsync(ClaimsPrincipal user, string permission, MonitoringResource resource, CancellationToken ct)
    {
        if (!await authorization.AuthorizeAsync(user, permission, resource, ct).ConfigureAwait(false))
            throw new MonitoringApiException(403, "monitoring_permission_required");
    }

    private static MetricServiceWatch[] Validate(MetricHistoryPolicyWrite body)
    {
        try { return MetricHistoryStore.ValidatePolicy(body); }
        catch (ArgumentException exception) { throw new MonitoringApiException(400, exception.Message); }
    }

    private static long ReadCursor(string? cursor, int tenant, Guid agent)
    {
        if (cursor is null) return 0;
        var prefix = FormattableString.Invariant($"{tenant}:{agent:N}:");
        if (cursor.Length > prefix.Length + 19 || !cursor.StartsWith(prefix, StringComparison.Ordinal) ||
            !long.TryParse(cursor.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var after) || after <= 0)
            throw new MonitoringApiException(400, "metrics_cursor_context_mismatch");
        return after;
    }
}
