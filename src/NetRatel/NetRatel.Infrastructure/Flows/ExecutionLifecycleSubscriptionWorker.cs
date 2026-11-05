using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

/// <summary>
/// Delivers server-selected immutable subscription intents. This service remains inert until the trusted
/// execution ingress is registered; it never constructs browser envelopes or borrows a source principal.
/// </summary>
public sealed class ExecutionLifecycleSubscriptionWorker(IServiceScopeFactory scopes,
    ILogger<ExecutionLifecycleSubscriptionWorker> logger, TimeProvider clock) : BackgroundService
{
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Execution lifecycle subscription delivery failed"); }
            await Task.Delay(Poll, clock, stoppingToken);
        }
    }

    internal async Task ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var ingress = scope.ServiceProvider.GetService<IFlowExecutionEventIngress>();
        if (ingress is null) return;
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = clock.GetUtcNow();
        var candidates = await db.OutboxMessages.AsNoTracking().Where(x =>
                x.Type == FlowExecutionSubscriptionLimits.OutboxType && x.Status == OutboxStatuses.Pending &&
                (x.NextAttemptUtc == null || x.NextAttemptUtc <= now) &&
                (x.LockedUntilUtc == null || x.LockedUntilUtc <= now))
            .OrderBy(x => x.OccurredUtc).ThenBy(x => x.Id)
            .Take(FlowExecutionSubscriptionLimits.MaximumBatch).Select(x => x.Id).ToArrayAsync(ct);
        foreach (var id in candidates)
        {
            now = clock.GetUtcNow();
            var owner = _owner + ":" + Guid.NewGuid().ToString("N");
            var expiry = now.Add(Lease);
            var claimed = await db.OutboxMessages.Where(x => x.Id == id &&
                    x.Type == FlowExecutionSubscriptionLimits.OutboxType && x.Status == OutboxStatuses.Pending &&
                    (x.NextAttemptUtc == null || x.NextAttemptUtc <= now) &&
                    (x.LockedUntilUtc == null || x.LockedUntilUtc <= now))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.LockOwner, owner).SetProperty(x => x.LockedUntilUtc, expiry), ct);
            if (claimed != 1) continue;
            now = clock.GetUtcNow();
            var row = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id &&
                x.Status == OutboxStatuses.Pending && x.LockOwner == owner && x.LockedUntilUtc > now, ct);
            if (row is null) continue;
            var owned = db.OutboxMessages.Where(x => x.Id == id && x.Status == OutboxStatuses.Pending && x.LockOwner == owner);
            try
            {
                var delivery = ExecutionLifecycleSubscriptions.ReadDelivery(row.PayloadJson);
                if (!ValidDelivery(row, delivery))
                {
                    await FinishAsync(owned, OutboxStatuses.Failed, "execution-subscription-invalid", ct);
                    continue;
                }
                var intent = delivery!;
                var source = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == intent.SourceEventId && x.Type == FlowExecutionEventLimits.OutboxType, ct);
                var fact = ReadFact(source);
                if (fact is null || source is null ||
                    intent.SourcePayloadHash != ExecutionLifecycleSubscriptions.PayloadHash(source.PayloadJson))
                {
                    await FinishAsync(owned, OutboxStatuses.Failed, "execution-source-fact-invalid", ct);
                    continue;
                }
                var version = await db.FlowVersions.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == intent.FlowVersionId && x.TenantId == intent.TenantId, ct);
                FlowGraphDto? graph = version is null ? null : JsonSerializer.Deserialize<FlowGraphDto>(version.GraphJson, FlowPersistenceService.Json);
                var trigger = graph?.Nodes.SingleOrDefault(x => x.Id == intent.TriggerNodeId)?.ExecutionTrigger;
                if (version is null || version.ConfigurationHash != intent.ConfigurationHash || trigger is null ||
                    !FlowExecutionEventMatching.Matches(intent.TenantId, trigger, fact, intent.ActiveSinceUtc) ||
                    FlowExecutionEventMatching.AdmissionKey(version.Id, intent.TriggerNodeId, trigger, fact) != intent.AdmissionKey)
                {
                    await FinishAsync(owned, OutboxStatuses.Failed, "execution-subscription-version-invalid", ct);
                    continue;
                }
                if (await AwaitsKnownParentAsync(db, trigger, fact, ct))
                {
                    await DeferAsync(owned, row.Attempts, "execution-parent-result-pending", Poll, ct);
                    continue;
                }
                var references = await ContributorsAsync(db, intent, ct);
                // The handoff proof is this exact claimed source fact. Ingress chooses a deterministic
                // aggregate representative and records late provenance without changing accepted EventJson.
                var result = await ingress.EnqueueExecutionAsync(new(intent.TenantId, intent.FlowVersionId,
                    intent.SourceEventId, references, intent.DeliveryId), ct);
                if (result.Disposition is FlowIngressDisposition.Enqueued or FlowIngressDisposition.Duplicate)
                    await FinishAsync(owned, OutboxStatuses.Published, null, ct);
                else if (result.Disposition == FlowIngressDisposition.CapacityExceeded)
                    await DeferAsync(owned, row.Attempts + 1, result.Code ?? "execution-capacity", TimeSpan.FromSeconds(5), ct);
                else
                    await FinishAsync(owned, result.Disposition == FlowIngressDisposition.Disabled
                        ? OutboxStatuses.Disabled : OutboxStatuses.Failed, result.Code ?? "execution-admission-rejected", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Execution lifecycle delivery {DeliveryId} will be retried", id);
                var attempt = row.Attempts + 1;
                await DeferAsync(owned, attempt, "execution-subscription-retry",
                    TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(8, attempt)))), ct);
            }
        }
    }

    private static bool ValidDelivery(OutboxMessage row, FlowExecutionSubscriptionDeliveryDto? value) =>
        value is not null && value.DeliveryId == row.Id && value.TenantId > 0 &&
        value.FlowVersionId != Guid.Empty && value.TriggerNodeId != Guid.Empty && value.SourceEventId != Guid.Empty &&
        value.AdmissionKey != Guid.Empty && value.ActiveSinceUtc != default &&
        value.SourcePayloadHash is { Length: 64 } && value.SourcePayloadHash.All(char.IsAsciiHexDigit) &&
        value.ConfigurationHash is { Length: 64 } && value.ConfigurationHash.All(char.IsAsciiHexDigit) &&
        row.TenantId == value.TenantId.ToString(CultureInfo.InvariantCulture) &&
        row.EntityId == value.FlowVersionId.ToString("N") && row.CorrelationId == value.AdmissionKey.ToString("N") &&
        value.DeliveryId == ExecutionLifecycleSubscriptions.DeliveryId(value.FlowVersionId, value.TriggerNodeId, value.SourceEventId);

    private static FlowExecutionEventDto? ReadFact(OutboxMessage? row)
    {
        if (row is null || Encoding.UTF8.GetByteCount(row.PayloadJson) > FlowExecutionEventLimits.MaximumEnvelopeBytes) return null;
        try
        {
            var fact = JsonSerializer.Deserialize(row.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto);
            return fact is not null && fact.EventId == row.Id && FlowExecutionEventProvenance.IsNative(fact) &&
                FlowExecutionEventValidator.Validate(fact).Valid ? fact : null;
        }
        catch (JsonException) { return null; }
    }

    internal static async Task<bool> AwaitsKnownParentAsync(OrchestratorDbContext db,
        FlowExecutionTriggerSettingsDto trigger, FlowExecutionEventDto fact, CancellationToken ct)
    {
        if (!trigger.AggregateCorrelatedEvents || trigger.Sources.Count < 2 ||
            !trigger.Sources.Contains(FlowExecutionSourceKind.Job) || fact.SourceKind == FlowExecutionSourceKind.Job ||
            fact.CorrelationId is not { } correlation || !correlation.StartsWith("job-run:", StringComparison.Ordinal) ||
            !long.TryParse(correlation.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var runId) || runId <= 0)
            return false;
        // Correlation has already been proved against the accepted parent at source commit. A missing
        // parent result remains pending; latest mutable JobRun.Status is never sufficient terminal evidence.
        return !await (from control in db.Set<JobRunControlRecord>().AsNoTracking()
            join run in db.JobRuns.AsNoTracking() on control.RunId equals run.Id
            where control.RunId == runId && run.TenantId == fact.TenantId &&
                control.TerminalReadyAtUtc != null && control.TerminalResultHash != null
            select control.RunId).AnyAsync(ct);
    }

    private static async Task<IReadOnlyList<Guid>> ContributorsAsync(OrchestratorDbContext db,
        FlowExecutionSubscriptionDeliveryDto delivery, CancellationToken ct)
    {
        var tenant = delivery.TenantId.ToString(CultureInfo.InvariantCulture);
        var version = delivery.FlowVersionId.ToString("N");
        var key = delivery.AdmissionKey.ToString("N");
        var rows = await db.OutboxMessages.AsNoTracking().Where(x => x.Type == FlowExecutionSubscriptionLimits.OutboxType &&
                x.TenantId == tenant && x.EntityId == version && x.CorrelationId == key)
            .OrderBy(x => x.Source).ThenBy(x => x.Id).Take(FlowExecutionEventLimits.MaximumReferences + 1)
            .Select(x => x.PayloadJson).ToArrayAsync(ct);
        return rows.Select(ExecutionLifecycleSubscriptions.ReadDelivery).Where(x => x is not null &&
                x.FlowVersionId == delivery.FlowVersionId && x.TriggerNodeId == delivery.TriggerNodeId &&
                x.AdmissionKey == delivery.AdmissionKey).Select(x => x!.SourceEventId)
            .Prepend(delivery.SourceEventId).Distinct().Take(FlowExecutionEventLimits.MaximumReferences).ToArray();
    }

    private static Task<int> FinishAsync(IQueryable<OutboxMessage> owned, string status, string? code, CancellationToken ct) =>
        owned.ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, status).SetProperty(x => x.LastError, code)
            .SetProperty(x => x.LockOwner, (string?)null).SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null), ct);

    private Task<int> DeferAsync(IQueryable<OutboxMessage> owned, int attempts, string code, TimeSpan delay, CancellationToken ct)
    {
        var next = clock.GetUtcNow().Add(delay);
        return owned.ExecuteUpdateAsync(set => set.SetProperty(x => x.Attempts, attempts).SetProperty(x => x.LastError, code)
            .SetProperty(x => x.NextAttemptUtc, next).SetProperty(x => x.LockOwner, (string?)null)
            .SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null), ct);
    }
}
