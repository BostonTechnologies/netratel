using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Netclaw;
using Xunit;

namespace NetRatel.Tests.Netclaw;

public sealed partial class NetclawPersistencePostgresTests
{
    [Fact]
    public async Task Prepared_alert_is_durable_unsent_context_and_first_Start_is_idempotent()
    {
        var options = await DatabaseAsync(); var agent = Guid.NewGuid(); var ruleId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var rule = new MonitoringRuleDto(17, ruleId, 1, 1, "CPU warning", true, MonitoringSeverity.Warning,
            new(MonitoringTargetMode.Selected, [agent], []), new(MonitoringMetricKind.CpuUsagePercent, MonitoringNumericUnit.Percent, 95, 90, null, null, []),
            TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
        var evidence = new MonitoringEvidenceDto(new(1, 1), Guid.NewGuid(), now, now, MonitoringEvidenceQuality.Fresh, MonitoringClassification.Breach, 97, 0, null);
        var occurrence = new MonitoringOccurrenceDto(Guid.NewGuid(), Guid.NewGuid(), now, now.AddMinutes(-3), rule, evidence, MonitoringFlowDispatchDisposition.NoFlowSelected);
        var state = new MonitoringSeriesState(new(17, ruleId, agent, "cpu"), 1, 1, MonitoringPhase.Firing, MonitoringEvidenceQuality.Fresh, LatestEvidence: evidence, Occurrence: occurrence, ApplicableBypassIds: []);
        var request = new InvestigationCreateRequest(Guid.NewGuid(), "CPU warning · demo-host", [agent], occurrence.OccurrenceId) { PreparedAlert = new(ruleId, "cpu") };
        Guid id;
        await using (var db = new OrchestratorDbContext(options))
        {
            db.Agents.Add(new Agent { Id = agent, TenantId = 17, Name = "demo-host", CreatedAtUtc = now });
            db.Add(new MonitoringRuleRecord { TenantId = 17, RuleId = ruleId, Revision = 1, DefinitionJson = JsonSerializer.Serialize(rule) });
            db.Add(new MonitoringSeriesRecord { TenantId = 17, RuleId = ruleId, AgentId = agent, ResourceKey = "cpu", StateJson = JsonSerializer.Serialize(state), UpdatedAtUtc = now });
            db.Add(new MonitoringOccurrenceRecord { TenantId = 17, RuleId = ruleId, AgentId = agent, ResourceKey = "cpu", OccurrenceId = occurrence.OccurrenceId, RaisedEventId = occurrence.RaisedEventId, RaisedAtUtc = now, OccurrenceJson = JsonSerializer.Serialize(occurrence) });
            await db.SaveChangesAsync();
            var store = new InvestigationStore(db, TimeProvider.System);
            id = (await store.CreateAsync(17, request, "operator", default)).Id;
            Assert.Equal(id, (await store.CreateAsync(17, request, "operator", default)).Id);
            var snapshot = await store.LoadAsync(17, id, 0, default);
            Assert.Equal(InvestigationState.Idle, snapshot.Investigation.State);
            Assert.Null(snapshot.ActiveMessageId); Assert.NotNull(snapshot.Prepared); Assert.False(snapshot.Prepared.Started);
            Assert.Contains("97% CPU", snapshot.Prepared.Message); Assert.Contains("UTC", snapshot.Prepared.Message);
            Assert.DoesNotContain(snapshot.Events, item => item.Type == "operator");
            Assert.Single(await db.AiInvestigations.ToListAsync());
            await Assert.ThrowsAsync<InvestigationConflictException>(() => store.CreateAsync(17, request with { PreparedAlert = new(ruleId, "tampered") }, "operator", default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.CreateAsync(17, request with { RequestId = Guid.NewGuid(), OccurrenceId = Guid.NewGuid() }, "operator", default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.LoadAsync(18, id, 0, default));
        }
        await using (var db = new OrchestratorDbContext(options))
        {
            var store = new InvestigationStore(db, TimeProvider.System);
            var snapshot = await store.LoadAsync(17, id, 1, default);
            var prepared = Assert.IsType<PreparedInvestigationDto>(snapshot.Prepared);
            var start = new InvestigationMessageRequest(prepared.FirstMessageId, prepared.Message + " Please prioritize read-only evidence.");
            Assert.True(await store.AcceptMessageAsync(17, id, start, "operator", null, false, default));
            Assert.False(await store.AcceptMessageAsync(17, id, start, "operator", null, false, default));
            snapshot = await store.LoadAsync(17, id, 0, default);
            Assert.True(snapshot.Prepared!.Started); Assert.Equal(InvestigationState.Queued, snapshot.Investigation.State);
            Assert.Single(snapshot.Events, item => item.Type == "operator");
            await Assert.ThrowsAsync<InvestigationConflictException>(() => store.AcceptMessageAsync(17, id, start with { Text = "Changed unknown outcome" }, "operator", null, false, default));
        }
    }
}
