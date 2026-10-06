using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

// Scoped to a disposable fixture's actual tenant/rule/agent/resource/Flow version.
// This probe has no mutation, seeding, dispatch or receipt-recovery operation.
internal sealed class PhysicalIncidentDurableReadProbe(IServiceProvider netRatelServices,
    ServiceLinkPublishedRatelDeskPeer publishedPeer, int tenantId, Guid ruleId, Guid agentId,
    string resourceKey, Guid flowVersionId)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PhysicalReceiptIdentity> ReadCommittedAsync(
        PhysicalIncidentForwarding forwarded, JsonElement acceptedResponse, CancellationToken ct)
    {
        RatelDeskReceiverPreparationV2 captured;
        await using (var scope = netRatelServices.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            if (!db.Database.IsNpgsql())
                throw new InvalidOperationException("Physical acceptance requires the actual PostgreSQL provider.");
            await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            var row = await db.Set<FlowReceiverEvidenceRecord>().AsNoTracking().SingleAsync(x =>
                x.TenantId == tenantId && x.ReceiverIdempotencyKey == forwarded.Key, ct);
            var action = await db.FlowActions.AsNoTracking().SingleAsync(x =>
                x.TenantId == tenantId && x.RunId == row.RunId && x.NodeId == row.NodeId, ct);
            var run = await db.FlowRuns.AsNoTracking().SingleAsync(x =>
                x.TenantId == tenantId && x.Id == row.RunId && x.FlowVersionId == flowVersionId, ct);
            var occurrence = await db.MonitoringOccurrences.AsNoTracking().SingleAsync(x =>
                x.TenantId == tenantId && x.RuleId == ruleId && x.AgentId == agentId &&
                x.ResourceKey == resourceKey && x.OccurrenceId == run.OccurrenceId, ct);
            var dispatch = await db.MonitoringFlowOutbox.AsNoTracking().SingleAsync(x =>
                x.TenantId == tenantId && x.OccurrenceId == occurrence.OccurrenceId &&
                x.EventId == occurrence.RaisedEventId, ct);
            captured = Parse<RatelDeskReceiverPreparationV2>(row.PreparationJson);
            var prepared = Parse<FlowIncidentActionRequest>(action.PreparedJson);
            var draft = Parse<FlowIncidentActionDraft>(action.DraftJson);
            var canonical = MonitoringCanonicalFlowEvent.Create(Parse<MonitoringOutboxIntent>(dispatch.IntentJson));
            var recordedEvent = Parse<FlowEventEnvelope>(run.EventJson);
            if (!row.MayHaveCommitted || row.FirstPostAttemptAtUtc is null ||
                row.LastPostLeaseFence != action.LeaseFence || action.Attempts < 1 ||
                action.Attempts > FlowLimits.MaximumActionAttempts || row.SchemaVersion != 2 ||
                captured.SchemaVersion != 2 || captured.Peer.SourceInstanceId != forwarded.SourceInstanceId ||
                captured.Peer.SourceNamespaceId == Guid.Empty || captured.Peer.LocalTenantId != tenantId ||
                captured.ReceiverIdempotencyKey != forwarded.Key ||
                captured.ReceiverIdempotencyKey != row.ReceiverIdempotencyKey ||
                captured.ReceiverFingerprint != row.ReceiverFingerprint ||
                captured.EvidenceFingerprint != row.EvidenceFingerprint ||
                run.EventId != occurrence.RaisedEventId || canonical is null || recordedEvent != canonical ||
                dispatch.FlowRunId is { } handedOffRun && handedOffRun != run.Id ||
                dispatch.StableFlowDispatchKey != $"monitoring:v1:{tenantId}:{run.OccurrenceId:D}:{run.EventId:D}:{flowVersionId:D}" ||
                captured.OriginalActionCreatedAtUtc != run.CreatedAtUtc ||
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(captured.ExactCreateBodyJson))) !=
                    forwarded.RequestBytesSha256 ||
                !ReceiverPreparedBinding.Valid(captured, prepared, draft, run.CreatedAtUtc,
                    scope.ServiceProvider.GetRequiredService<IRatelDeskReceiverFingerprint>()))
                throw new InvalidOperationException("The sent body did not match the durable physical action and occurrence.");
            // Release the independent sender snapshot before reading the other product.
            // Neither application transaction is ever held across peer HTTP.
            await snapshot.CommitAsync(ct);
        }
        var committed = await publishedPeer.ReadPhysicalIncidentCommitAsync(forwarded.SourceInstanceId,
            captured.Peer.SourceNamespaceId, forwarded.Key, ct);
        var receipt = acceptedResponse.GetProperty("integrationReceipt");
        if (committed.Fingerprint != captured.ReceiverFingerprint ||
            committed.OrganizationId != captured.Peer.OrganizationId || committed.CustomerId != captured.Peer.CustomerId ||
            committed.AcceptedJson != acceptedResponse.GetRawText() ||
            receipt.GetProperty("receiverInstanceId").GetString() != captured.Peer.ReceiverInstanceId ||
            receipt.GetProperty("fingerprint").GetString() != committed.Fingerprint ||
            receipt.GetProperty("incidentId").GetString() != committed.IncidentId ||
            NormalizePostgresTime(receipt.GetProperty("committedAtUtc").GetDateTimeOffset()) != committed.CommittedAtUtc)
            throw new InvalidOperationException("The actual accepted response did not match the independent published receiver rows.");
        return new(committed.SourceInstanceId, committed.NamespaceId, committed.Key, committed.Fingerprint,
            committed.IncidentId, committed.ReceiptId, committed.ConfirmationIds.Single());
    }

    // Npgsql stores timestamptz at microsecond precision; AcceptedJson above is
    // still compared byte for byte, preserving the original complete receipt.
    private static DateTimeOffset NormalizePostgresTime(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    private static T Parse<T>(string? raw)
    {
        try
        {
            if (string.IsNullOrEmpty(raw) || Encoding.UTF8.GetByteCount(raw) > 131_072)
                throw new InvalidOperationException("The durable physical action body was absent or oversized.");
            return JsonSerializer.Deserialize<T>(raw, Json)
                ?? throw new InvalidOperationException("The durable physical action body was absent.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The durable physical action body was malformed.");
        }
    }
}
