using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.API.Services.Monitoring;

/// <summary>A scoped exact-intent and current monitoring gate; the flow runtime separately verifies flow/connector execution grants.</summary>
public sealed class MonitoringFlowDispatchGuard(OrchestratorDbContext db, IMonitoringStore store,
    IMonitoringConfigurationStore configurations, IMonitoringClientDirectory directory,
    IFlowExecutionAuthorityVerifier authority, TimeProvider clock) : IFlowDispatchGuard
{
    private readonly MonitoringSeriesEvaluator _evaluator = new(clock);
    private static readonly JsonSerializerOptions IntentJson = new() { MaxDepth = 32 };

    public async Task<FlowDispatchDecision> CanDispatchAsync(FlowEventEnvelope input, CancellationToken cancellationToken = default)
    {
        if (!FlowContractValidation.ValidEnvelope(input)) return new(false, "monitoring-event-invalid");
        try
        {
            var key = $"monitoring:v1:{input.TenantId}:{input.OccurrenceId:D}:{input.EventId:D}:{input.FlowVersionId:D}";
            var json = await db.MonitoringFlowOutbox.AsNoTracking().Where(row => row.TenantId == input.TenantId &&
                row.EventId == input.EventId && row.OccurrenceId == input.OccurrenceId && row.StableFlowDispatchKey == key)
                .Select(row => row.IntentJson).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (json is null || JsonSerializer.Deserialize<MonitoringOutboxIntent>(json, IntentJson) is not { } intent ||
                !MonitoringFlowEventFactory.Matches(input, intent)) return new(false, "monitoring-intent-mismatch");
            var client = new ClientKey(input.TenantId, intent.Series.AgentId);
            if (!await directory.IsEligibleAsync(client, cancellationToken).ConfigureAwait(false)) return new(false, "monitoring-target-unavailable");
            var configuration = await configurations.GetAsync(input.TenantId, cancellationToken).ConfigureAwait(false);
            if (configuration.TenantId != input.TenantId) return new(false, "monitoring-tenant-mismatch");
            var rule = configuration.Rules.SingleOrDefault(item => item.RuleId == intent.Series.RuleId && item.TenantId == input.TenantId);
            if (rule is null || !TargetsClient(rule, configuration, client.AgentId)) return new(false, "monitoring-target-removed");
            var state = await store.LoadSeriesAsync(intent.Series, cancellationToken).ConfigureAwait(false);
            var evidence = await directory.GetCurrentEvidenceAsync(client, cancellationToken).ConfigureAwait(false);
            if (state is null || evidence is null || state.Cursor?.ConnectionEpoch != evidence.ConnectionEpoch ||
                !_evaluator.CanDispatch(state, rule, intent, evidence.EvidenceStreamId, configuration.Bypasses, configuration.Groups))
                return new(false, "monitoring-evidence-or-occurrence-unavailable");
            // A replacement or configuration write during the awaited checks must not admit the old command.
            if (!await authority.AuthorizeAsync(input.TenantId, input.Authority, cancellationToken).ConfigureAwait(false) ||
                (await configurations.GetAsync(input.TenantId, cancellationToken).ConfigureAwait(false)).Revision != configuration.Revision ||
                await directory.GetCurrentEvidenceAsync(client, cancellationToken).ConfigureAwait(false) != evidence)
                return new(false, "monitoring-authority-or-registration-changed");
            var finalState = await store.LoadSeriesAsync(intent.Series, cancellationToken).ConfigureAwait(false);
            if (finalState is null || finalState.StateRevision != state.StateRevision ||
                !_evaluator.CanDispatch(finalState, rule, intent, evidence.EvidenceStreamId, configuration.Bypasses, configuration.Groups) ||
                !await directory.IsEligibleAsync(client, cancellationToken).ConfigureAwait(false))
                return new(false, "monitoring-occurrence-or-target-changed");
            // Allocation can advance before the presence read model receives
            // the replacement. Require the durable shared epoch and monitoring
            // registration boundary as well as the live registry identity.
            var durableFence = await (from stream in db.MonitoringEvidenceStreams.AsNoTracking()
                join epoch in db.ClientConnectionEpochs.AsNoTracking()
                    on new { stream.TenantId, stream.AgentId } equals new { epoch.TenantId, epoch.AgentId }
                where stream.TenantId == client.TenantId && stream.AgentId == client.AgentId && stream.Active &&
                    stream.ConnectionId == evidence.ConnectionId && stream.ConnectionEpoch == evidence.ConnectionEpoch &&
                    stream.EvidenceStreamId == evidence.EvidenceStreamId && epoch.LastIssuedEpoch == evidence.ConnectionEpoch
                select stream).AnyAsync(cancellationToken).ConfigureAwait(false);
            if (!durableFence || await directory.GetCurrentEvidenceAsync(client, cancellationToken).ConfigureAwait(false) != evidence)
                return new(false, "monitoring-durable-evidence-fenced");
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, "monitoring-dispatch-admitted");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(false, "monitoring-dispatch-unavailable"); }
    }

    private static bool TargetsClient(MonitoringRuleDto rule, MonitoringConfigurationSnapshot configuration, Guid agentId) =>
        rule.Targets.Mode == MonitoringTargetMode.AllEligible || rule.Targets.AgentIds.Contains(agentId) ||
        rule.Targets.GroupIds.Any(groupId => configuration.Groups.Any(group => group.TenantId == rule.TenantId &&
            group.GroupId == groupId && group.AgentIds.Contains(agentId)));
}
