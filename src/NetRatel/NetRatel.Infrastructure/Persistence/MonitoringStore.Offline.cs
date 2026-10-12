using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Infrastructure.Persistence;

public sealed partial class MonitoringStore
{
    public async Task<ImmutableArray<MonitoringSeriesState>> LoadClientOfflineAsync(ClientKey client, CancellationToken cancellationToken)
    {
        if (!client.IsValid) throw new ArgumentException("invalid_client");
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var rows = await db.MonitoringSeries.AsNoTracking().Where(row => row.TenantId == client.TenantId &&
            row.AgentId == Guid.Empty && row.ResourceKey == MonitoringOfflinePresentation.ResourceKey)
            .OrderBy(row => row.RuleId).Take(MonitoringLimits.MaximumRulesPerTenant).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ReadState).Where(state => !state.OfflineAgentIds.IsDefault && state.OfflineAgentIds.Contains(client.AgentId)).ToImmutableArray();
    }
    /// <summary>The tenant configuration lock serializes scans across API replicas, including state/event/outbox insertion.</summary>
    public async Task<int> EvaluateOfflineAsync(int tenantId, CancellationToken cancellationToken)
    {
        RequireTenant(tenantId);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var configRow = await LockConfigurationAsync(db, tenantId, cancellationToken).ConfigureAwait(false);
        var config = await ReadConfigurationAsync(db, tenantId, configRow.Revision, cancellationToken).ConfigureAwait(false);
        var rules = config.Rules.Where(rule => rule.Enabled && rule.AlertIfOffline).ToArray();
        if (rules.Length == 0) return 0;
        var now = await EvidenceNowAsync(db, timeProvider, cancellationToken).ConfigureAwait(false);
        var evaluator = new MonitoringSeriesEvaluator(new AdmissionClock(now));
        var evaluated = 0;
        foreach (var rule in rules)
        {
            var clients = await ReadOfflineClientsAsync(db, rule, config.Groups, now, cancellationToken).ConfigureAwait(false);
            var key = MonitoringOfflinePresentation.Series(tenantId, rule.RuleId);
            var row = await LockSeriesAsync(db, key, cancellationToken).ConfigureAwait(false);
            var state = row is null ? evaluator.CreateInitial(key, rule) : ReadState(row);
            // Overlapping replica scans never manufacture another observation at the same instant.
            if (state.LatestEvidence?.ReceivedAtUtc >= now) continue;
            var observation = MonitoringOfflineEvaluation.Observe(rule, config.Groups, clients, config.Bypasses, now);
            if (row is null && observation.Summary.MonitoredCount == 0) continue;
            if (row is null && await db.MonitoringSeries.CountAsync(item => item.TenantId == tenantId, cancellationToken).ConfigureAwait(false) >= MaximumSeriesPerTenant)
                throw new InvalidOperationException("series_capacity_exceeded");
            var result = MonitoringOfflineEvaluation.Evaluate(evaluator, state, rule, observation, now, config.Bypasses, config.Groups);
            if (result.State.StateRevision == result.ExpectedStateRevision) continue;
            await ApplyEvaluationAsync(db, result, config.Revision, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            evaluated++;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return evaluated;
    }

    private static async Task<MonitoringOfflineClient[]> ReadOfflineClientsAsync(OrchestratorDbContext db, MonitoringRuleDto rule,
        IReadOnlyList<MonitoringGroupDto> groups, DateTimeOffset now, CancellationToken ct)
    {
        // This reads the same committed owner deadlines used by the presence API, never a process-local projection.
        // Pending uninstall operations are deliberately outside monitoring eligibility while offboarding completes.
        var agents = db.Agents.AsNoTracking().Where(agent => agent.TenantId == rule.TenantId && agent.IsEnabled &&
            agent.Status == AgentStatus.Active && agent.RevokedAtUtc == null && agent.DeletedAtUtc == null && agent.SupersededAtUtc == null &&
            !db.ClientOffboardingOperations.Any(operation => operation.TenantId == rule.TenantId && operation.AgentId == agent.Id &&
                (operation.State == ClientOffboardingState.Requested || operation.State == ClientOffboardingState.Accepted)));
        if (rule.Targets.Mode == MonitoringTargetMode.Selected)
        {
            var selected = rule.Targets.AgentIds.Concat(groups.Where(group => group.TenantId == rule.TenantId &&
                rule.Targets.GroupIds.Contains(group.GroupId)).SelectMany(group => group.AgentIds)).Distinct().ToArray();
            agents = agents.Where(agent => selected.Contains(agent.Id));
        }
        // Apply the rule scope before its capacity limit; unrelated clients must not block a selected rule in a large tenant.
        var rows = await (from agent in agents
            join owner in db.ClientConnectionOwners.AsNoTracking() on new { agent.TenantId, AgentId = agent.Id }
                equals new { owner.TenantId, owner.AgentId } into owners
            from owner in owners.DefaultIfEmpty()
            orderby agent.Id
            select new
            {
                agent.Id, agent.Name, agent.FriendlyName, agent.DeviceInfoJson,
                Online = owner != null && owner.AcceptanceGuardAtUtc != null && owner.Active &&
                    owner.PresenceExpiresAtUtc > now && owner.AuthenticationExpiresAtUtc > now
            }).Take(MonitoringLimits.MaximumTargetClients + 1).ToArrayAsync(ct).ConfigureAwait(false);
        if (rows.Length > MonitoringLimits.MaximumTargetClients) throw new InvalidOperationException("target_capacity_exceeded");
        return rows.Select(row =>
        {
            var identity = MonitoringIdentityPresentation.Create(row.Id, row.Name, row.DeviceInfoJson);
            var name = identity.Hostname ?? identity.DisplayName;
            if (!string.IsNullOrWhiteSpace(row.FriendlyName)) name = $"{row.FriendlyName} ({name})";
            return new MonitoringOfflineClient(row.Id, name, !row.Online);
        }).ToArray();
    }

    private static async Task<bool> CanDispatchOfflineAsync(OrchestratorDbContext db, MonitoringSeriesState state,
        MonitoringRuleDto rule, MonitoringOutboxIntent intent, MonitoringConfigurationSnapshot configuration,
        TimeProvider clock, CancellationToken ct)
    {
        if (!MonitoringOfflinePresentation.IsSeries(state.Series) || !rule.Enabled || !rule.AlertIfOffline) return false;
        var now = await EvidenceNowAsync(db, clock, ct).ConfigureAwait(false);
        var clients = await ReadOfflineClientsAsync(db, rule, configuration.Groups, now, ct).ConfigureAwait(false);
        var current = MonitoringOfflineEvaluation.Observe(rule, configuration.Groups, clients, configuration.Bypasses, now);
        if (current.Summary.OfflineCount == 0) return false;
        // Recheck current membership/bypasses before an effect, without changing the immutable raised-event payload.
        return new MonitoringSeriesEvaluator(new AdmissionClock(now)).CanDispatch(state with { OfflineAgentIds = current.AgentIds },
            rule, intent, rule.RuleId, configuration.Bypasses, configuration.Groups);
    }
}
