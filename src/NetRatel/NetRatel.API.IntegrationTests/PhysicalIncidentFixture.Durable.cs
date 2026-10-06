using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.Monitoring;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

internal sealed partial class PhysicalIncidentFixture
{
    public async Task<PhysicalDurableProof> ReadScopedDurableProofAsync(PhysicalRule rule, CancellationToken ct)
    {
        List<MonitoringOccurrenceRecord> occurrences;
        List<MonitoringFlowOutboxRecord> outbox;
        List<FlowRunRecord> runs;
        List<FlowActionRecord> actions;
        List<FlowReceiverEvidenceRecord> evidence;
        List<MonitoringEventRecord> events;
        var identities = new List<PhysicalAction>();
        var capturedRows = new List<RatelDeskReceiverPreparationV2>();
        await using (var scope = pair.NetRatel.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            occurrences = await db.MonitoringOccurrences.AsNoTracking().Where(x => x.TenantId == Tenant &&
                x.RuleId == rule.RuleId && x.AgentId == agent && x.ResourceKey == rule.ResourceKey).OrderBy(x => x.RaisedAtUtc).ThenBy(x => x.OccurrenceId).ToListAsync(ct);
            var occurrenceIds = occurrences.Select(x => x.OccurrenceId).ToArray();
            outbox = await db.MonitoringFlowOutbox.AsNoTracking().Where(x => x.TenantId == Tenant && occurrenceIds.Contains(x.OccurrenceId)).ToListAsync(ct);
            runs = await db.FlowRuns.AsNoTracking().Where(x => x.TenantId == Tenant && x.FlowVersionId == rule.FlowVersionId).ToListAsync(ct);
            var runIds = runs.Select(x => x.Id).ToArray();
            actions = await db.FlowActions.AsNoTracking().Where(x => x.TenantId == Tenant && runIds.Contains(x.RunId)).ToListAsync(ct);
            evidence = await db.Set<FlowReceiverEvidenceRecord>().AsNoTracking().Where(x => x.TenantId == Tenant && runIds.Contains(x.RunId)).ToListAsync(ct);
            events = await db.MonitoringEvents.AsNoTracking().Where(x => x.TenantId == Tenant && occurrenceIds.Contains(x.OccurrenceId)).ToListAsync(ct);
            if (outbox.Count < occurrences.Count || runs.Count < occurrences.Count || actions.Count < occurrences.Count || evidence.Count < actions.Count)
                throw new PhysicalProofNotReadyException();
            Require(outbox.Count == occurrences.Count && runs.Count == occurrences.Count && actions.Count == occurrences.Count && evidence.Count == actions.Count,
                "The scoped occurrence/action/outbox/run/evidence counts contain extra durable effects.");
            foreach (var occurrence in occurrences)
            {
                var dispatch = outbox.Single(x => x.OccurrenceId == occurrence.OccurrenceId && x.EventId == occurrence.RaisedEventId);
                var run = runs.Single(x => x.OccurrenceId == occurrence.OccurrenceId && x.EventId == occurrence.RaisedEventId);
                var action = actions.Single(x => x.RunId == run.Id);
                var receiver = evidence.Single(x => x.RunId == run.Id && x.NodeId == action.NodeId);
                var captured = Parse<RatelDeskReceiverPreparationV2>(receiver.PreparationJson);
                var raised = Parse<MonitoringEventIntent>(events.Single(x => x.EventId == occurrence.RaisedEventId).EventJson);
                var intent = Parse<MonitoringOutboxIntent>(dispatch.IntentJson);
                var canonical = MonitoringCanonicalFlowEvent.Create(intent);
                var prepared = Parse<FlowIncidentActionRequest>(action.PreparedJson);
                var draft = Parse<FlowIncidentActionDraft>(action.DraftJson);
                Require(raised.Kind == MonitoringEventKind.AlertRaised && raised.Series.RuleId == rule.RuleId && raised.Series.AgentId == agent &&
                    raised.Series.ResourceKey == rule.ResourceKey && raised.PinnedRule.PublishedFlowVersionId == rule.FlowVersionId &&
                    canonical is not null && Parse<FlowEventEnvelope>(run.EventJson) == canonical &&
                    intent.PinnedEvidence == raised.Evidence && System.Text.Json.JsonSerializer.Serialize(intent.PinnedRule, json) == System.Text.Json.JsonSerializer.Serialize(raised.PinnedRule, json) &&
                    dispatch.StableFlowDispatchKey == $"monitoring:v1:{Tenant}:{run.OccurrenceId:D}:{run.EventId:D}:{rule.FlowVersionId:D}" &&
                    (dispatch.FlowRunId is null || dispatch.FlowRunId == run.Id) && receiver.SchemaVersion == 2 &&
                    captured.Peer.SourceInstanceId == pair.NetRatel.SourceInstanceId && captured.Peer.SourceNamespaceId == sourceNamespace &&
                    captured.Peer.LocalTenantId == Tenant && captured.ConnectorId == rule.ConnectorId && captured.ConnectorRevision == rule.SemanticRevision &&
                    captured.Peer.ReceiverInstanceId == pair.RatelDesk.InstanceId.ToString("D") &&
                    captured.ReceiverIdempotencyKey == receiver.ReceiverIdempotencyKey && captured.ReceiverFingerprint == receiver.ReceiverFingerprint &&
                    captured.EvidenceFingerprint == receiver.EvidenceFingerprint && captured.OriginalActionCreatedAtUtc == run.CreatedAtUtc &&
                    ReceiverPreparedBinding.Valid(captured, prepared, draft, run.CreatedAtUtc,
                        scope.ServiceProvider.GetRequiredService<IRatelDeskReceiverFingerprint>()),
                    "The actual durable occurrence, canonical event and immutable action identity disagree.");
                if (action.Attempts == 0 || receiver.FirstPostAttemptAtUtc is null) throw new PhysicalProofNotReadyException();
                Require(action.Attempts is >= 1 and <= FlowLimits.MaximumActionAttempts && receiver.FirstPostAttemptAtUtc is not null &&
                    receiver.AutomaticReplayUntilUtc == run.CreatedAtUtc.Add(FlowLimits.MaximumRetryAge),
                    "The actual durable physical action did not preserve bounded automatic replay/POST evidence.");
                identities.Add(new(occurrence.OccurrenceId, run.Id, run.FlowVersionId, action.IdempotencyKey,
                    captured.ReceiverIdempotencyKey, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(captured.ExactCreateBodyJson))),
                    captured.ReceiverFingerprint, sourceNamespace, captured.Peer.SourceInstanceId, captured.ConnectorRevision));
                capturedRows.Add(captured);
            }
            await snapshot.CommitAsync(ct);
        }
        var counts = await pair.RatelDesk.ReadPhysicalNamespaceCountsAsync(pair.NetRatel.SourceInstanceId, sourceNamespace, ct);
        if (counts.Receipts < identities.Count) throw new PhysicalProofNotReadyException();
        Require(counts.Receipts == identities.Count && counts.Incidents == identities.Count && counts.Confirmations == identities.Count,
            "The actual receiver namespace contains missing or duplicate incidents, receipts or confirmation enqueues.");
        var receipts = new List<Guid>(); var incidents = new List<string>(); var confirmations = new List<Guid>();
        for (var index = 0; index < identities.Count; index++)
        {
            var action = identities[index]; var captured = capturedRows[index];
            var remote = await pair.RatelDesk.ReadPhysicalIncidentCommitAsync(action.SourceInstanceId, action.SourceNamespaceId, action.ReceiverKey, ct);
            Require(remote.Fingerprint == action.ReceiverFingerprint && remote.OrganizationId == captured.Peer.OrganizationId &&
                remote.CustomerId == captured.Peer.CustomerId, "The actual receipt changed its immutable target or fingerprint.");
            var row = evidence.Single(x => x.RunId == action.FlowRunId);
            if (row.FullReceiptJson is not null)
            {
                var stored = Parse<RatelDeskVerifiedReceipt>(row.FullReceiptJson);
                Require(stored.Key == action.ReceiverKey && stored.Fingerprint == action.ReceiverFingerprint &&
                    stored.IncidentId == remote.IncidentId && stored.ExactAcceptedBodyJson == remote.AcceptedJson &&
                    stored.SourceNamespaceId == sourceNamespace && stored.SourceInstanceId == action.SourceInstanceId &&
                    !row.MayHaveCommitted, "The durably verified original receipt differs from the actual published receiver rows.");
            }
            receipts.Add(remote.ReceiptId); incidents.Add(remote.IncidentId); confirmations.Add(remote.ConfirmationIds.Single());
        }
        return new(occurrences.Select(x => x.OccurrenceId).ToArray(), occurrences.Select(x => x.RaisedEventId).ToArray(),
            occurrences.Select(x => outbox.Single(y => y.OccurrenceId == x.OccurrenceId).StableFlowDispatchKey).ToArray(),
            identities.Select(x => x.FlowRunId).ToArray(), identities.ToArray(), receipts.ToArray(), incidents.ToArray(), confirmations.ToArray(),
            evidence.Any(x => x.MayHaveCommitted), actions.Count > 0 && actions.All(x => x.Status == FlowActionStatus.Succeeded) &&
            evidence.All(x => x.FullReceiptJson is not null) && runs.All(x => x.Status == FlowRunStatus.Succeeded));
    }

    public Task<PhysicalDurableProof> WaitDurableMayHaveCommittedActionAsync(PhysicalRule rule, CancellationToken ct) =>
        WaitProofAsync(rule, x => x.Actions.Length == 1 && x.MayHaveCommitted && !x.ActionSucceeded, ct);
    public Task<PhysicalDurableProof> WaitRealVerifiedReceiptAndActionSuccessAsync(PhysicalRule rule, CancellationToken ct) =>
        WaitProofAsync(rule, x => x.Actions.Length == 1 && !x.MayHaveCommitted && x.ActionSucceeded, ct);
    public Task<PhysicalDurableProof> WaitSecondActualOccurrenceAndVerifiedIncidentAsync(PhysicalRule rule, CancellationToken ct) =>
        WaitProofAsync(rule, x => x.Actions.Length == 2 && !x.MayHaveCommitted && x.ActionSucceeded, ct);

    private async Task<PhysicalDurableProof> WaitProofAsync(PhysicalRule rule, Func<PhysicalDurableProof, bool> predicate, CancellationToken ct)
    {
        while (true)
        {
            await disk.AssertAliveAsync(ct);
            try { var proof = await ReadScopedDurableProofAsync(rule, ct); if (predicate(proof)) return proof; }
            catch (PhysicalProofNotReadyException) { } // Only incomplete actual handoff/receipt is retryable.
            await Task.Delay(250, ct);
        }
    }

    public async Task WaitActualOccurrenceResolvedOnceAsync(PhysicalRule rule, Guid occurrence, CancellationToken ct)
    {
        while (true)
        {
            await disk.AssertAliveAsync(ct);
            await using var scope = pair.NetRatel.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var row = await db.MonitoringOccurrences.AsNoTracking().SingleAsync(x => x.TenantId == Tenant && x.OccurrenceId == occurrence && x.RuleId == rule.RuleId, ct);
            var events = await db.MonitoringEvents.AsNoTracking().Where(x => x.TenantId == Tenant && x.OccurrenceId == occurrence).Select(x => x.EventJson).ToArrayAsync(ct);
            var resolved = events.Select(Parse<MonitoringEventIntent>).Count(x => x.Kind == MonitoringEventKind.AlertResolved);
            Require(resolved <= 1, "The same actual occurrence resolved more than once.");
            if (row.EndedAtUtc is not null && resolved == 1)
            {
                var state = Parse<MonitoringOccurrenceDto>(row.OccurrenceJson);
                Require(state.ClosureDisposition == MonitoringClosureDisposition.Recovered, "The actual occurrence ended without sustained physical recovery.");
                return;
            }
            await Task.Delay(250, ct);
        }
    }

    private sealed class PhysicalProofNotReadyException : Exception { }
}
