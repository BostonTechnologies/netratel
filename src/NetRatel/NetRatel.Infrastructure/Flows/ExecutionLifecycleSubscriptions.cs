using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

/// <summary>Source state, immutable fact and selected immutable subscription commit in one transaction.</summary>
public static class ExecutionLifecycleSubscriptions
{
    internal static bool NeedsTransaction(OrchestratorDbContext db)
    {
        db.ChangeTracker.DetectChanges();
        return db.ChangeTracker.Entries<OutboxMessage>().Any(x => x.State == EntityState.Added &&
                x.Entity.Type == FlowExecutionEventLimits.OutboxType) ||
            db.ChangeTracker.Entries<RequestRecord>().Any(x => x.State == EntityState.Added ||
                x.State == EntityState.Modified && x.Property(row => row.Status).IsModified) ||
            db.ChangeTracker.Entries<JobTaskActivityRecord>().Any(ExecutionLifecycleOutbox.HasScriptTransition);
    }

    internal static void Capture(OrchestratorDbContext db)
    {
        var facts = PendingFacts(db);
        if (facts.Count == 0) return;
        RequireTransaction(db);
        var tenants = facts.Select(x => x.Event.TenantId).Distinct().Order().ToArray();
        foreach (var tenant in tenants) FlowTenantTransactionLock.Acquire(db, tenant);
        var definitions = Definitions(db, tenants).ToArray();
        var linearizedAt = db.Database.IsNpgsql()
            ? db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").Single()
            : DateTimeOffset.UtcNow;
        StampNewFacts(facts, linearizedAt);
        var ids = definitions.Where(x => x.PublishedVersionId != null).Select(x => x.PublishedVersionId!.Value).ToArray();
        var versions = db.FlowVersions.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArray();
        Append(db, facts, definitions, versions);
    }

    internal static async Task CaptureAsync(OrchestratorDbContext db, CancellationToken ct)
    {
        var facts = PendingFacts(db);
        if (facts.Count == 0) return;
        RequireTransaction(db);
        var tenants = facts.Select(x => x.Event.TenantId).Distinct().Order().ToArray();
        foreach (var tenant in tenants) await FlowTenantTransactionLock.AcquireAsync(db, tenant, ct);
        var definitions = await Definitions(db, tenants).ToArrayAsync(ct);
        var linearizedAt = db.Database.IsNpgsql()
            ? await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct)
            : DateTimeOffset.UtcNow;
        StampNewFacts(facts, linearizedAt);
        var ids = definitions.Where(x => x.PublishedVersionId != null).Select(x => x.PublishedVersionId!.Value).ToArray();
        var versions = await db.FlowVersions.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync(ct);
        Append(db, facts, definitions, versions);
    }

    // The source transition, selected version and enable interval linearize under the ordered SHARE
    // locks. The tenant fence also covers disabled, unpublished and not-yet-created definitions.
    // Only new, uncommitted facts are stamped here; persisted duplicate facts are never rebuilt.
    private static void StampNewFacts(List<(OutboxMessage Row, FlowExecutionEventDto Event)> facts, DateTimeOffset at)
    {
        for (var index = 0; index < facts.Count; index++)
        {
            var (row, fact) = facts[index];
            var stamped = fact with { OccurredAtUtc = at, ObservedAtUtc = at };
            row.PayloadJson = JsonSerializer.Serialize(stamped, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto);
            row.OccurredUtc = at;
            row.NextAttemptUtc = at;
            facts[index] = (row, stamped);
        }
    }

    private static IQueryable<FlowDefinitionRecord> Definitions(OrchestratorDbContext db, int[] tenants) =>
        db.Database.IsNpgsql()
            ? db.FlowDefinitions.FromSqlInterpolated($@"SELECT * FROM ""FlowDefinitions""
                WHERE ""TenantId"" = ANY ({tenants})
                ORDER BY ""Id"" FOR SHARE").AsNoTracking()
            : db.FlowDefinitions.AsNoTracking().Where(x => tenants.Contains(x.TenantId)).OrderBy(x => x.Id);

    private static void RequireTransaction(OrchestratorDbContext db)
    {
        if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Lifecycle subscription selection requires the source transition transaction.");
    }

    private static List<(OutboxMessage Row, FlowExecutionEventDto Event)> PendingFacts(OrchestratorDbContext db)
    {
        var rows = db.ChangeTracker.Entries<OutboxMessage>()
            .Where(x => x.State == EntityState.Added && x.Entity.Type == FlowExecutionEventLimits.OutboxType)
            .Select(x => x.Entity).ToArray();
        var facts = new List<(OutboxMessage, FlowExecutionEventDto)>(rows.Length);
        foreach (var row in rows)
        {
            if (Encoding.UTF8.GetByteCount(row.PayloadJson) > FlowExecutionEventLimits.MaximumEnvelopeBytes)
                throw new InvalidOperationException("Lifecycle source fact exceeds its immutable envelope budget.");
            var fact = JsonSerializer.Deserialize(row.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto);
            if (fact is null || !FlowExecutionEventValidator.Validate(fact).Valid || !FlowExecutionEventProvenance.IsNative(fact) ||
                fact.EventId != row.Id || row.TenantId != fact.TenantId.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Lifecycle subscription selection requires a native immutable source fact.");
            facts.Add((row, fact));
        }
        // A failed source save may be retried in the same context. Rebuild tentative selection under
        // the new transaction's locks rather than retaining a pointer from an aborted transaction.
        var sourceIds = rows.Select(x => x.Id).ToHashSet();
        foreach (var entry in db.ChangeTracker.Entries<OutboxMessage>().Where(x => x.State == EntityState.Added &&
                     x.Entity.Type == FlowExecutionSubscriptionLimits.OutboxType).ToArray())
        {
            var delivery = ReadDelivery(entry.Entity.PayloadJson);
            if (delivery is not null && sourceIds.Contains(delivery.SourceEventId)) entry.State = EntityState.Detached;
        }
        return facts;
    }

    private static void Append(OrchestratorDbContext db, IReadOnlyList<(OutboxMessage Row, FlowExecutionEventDto Event)> facts,
        IReadOnlyList<FlowDefinitionRecord> definitions, IReadOnlyList<FlowVersionRecord> versions)
    {
        foreach (var definition in definitions.Where(x => x.Enabled && x.PublishedVersionId is not null))
        {
            var version = versions.SingleOrDefault(x => x.Id == definition.PublishedVersionId &&
                x.TenantId == definition.TenantId && x.FlowId == definition.Id);
            if (version is null || string.IsNullOrWhiteSpace(version.ExecutionAuthorityJson)) continue;
            FlowGraphDto? graph;
            try { graph = JsonSerializer.Deserialize<FlowGraphDto>(version.GraphJson, FlowPersistenceService.Json); }
            catch (JsonException) { continue; }
            if (graph?.SchemaVersion != FlowGraphV2Limits.SchemaVersion ||
                !FlowGraphV2Validator.ValidatePublished(graph).Valid) continue;
            var activeSince = definition.EnabledSinceUtc > version.PublishedAtUtc
                ? definition.EnabledSinceUtc.Value : version.PublishedAtUtc;
            foreach (var node in graph.Nodes.Where(x => x.Kind == FlowNodeKind.ExecutionEvent && x.ExecutionTrigger != null))
            foreach (var (row, fact) in facts)
            {
                var trigger = node.ExecutionTrigger!;
                if (!FlowExecutionEventMatching.Matches(definition.TenantId, trigger, fact, activeSince)) continue;
                var key = FlowExecutionEventMatching.AdmissionKey(version.Id, node.Id, trigger, fact);
                var id = DeliveryId(version.Id, node.Id, fact.EventId);
                var delivery = new FlowExecutionSubscriptionDeliveryDto(id, fact.TenantId, version.Id, node.Id, fact.EventId,
                    activeSince, key, PayloadHash(row.PayloadJson), version.ConfigurationHash);
                var json = JsonSerializer.Serialize(delivery, FlowExecutionSubscriptionJsonContext.Default.FlowExecutionSubscriptionDeliveryDto);
                if (Encoding.UTF8.GetByteCount(json) > FlowExecutionSubscriptionLimits.MaximumIntentBytes)
                    throw new InvalidOperationException("Lifecycle subscription intent exceeds its immutable envelope budget.");
                if (db.OutboxMessages.Local.Any(x => x.Id == id)) continue;
                db.OutboxMessages.Add(new()
                {
                    Id = id, Type = FlowExecutionSubscriptionLimits.OutboxType, PayloadJson = json,
                    OccurredUtc = fact.OccurredAtUtc, Source = "flow-subscription:" + (int)fact.SourceKind,
                    CorrelationId = key.ToString("N"), TenantId = fact.TenantId.ToString(CultureInfo.InvariantCulture),
                    EntityId = version.Id.ToString("N"), Status = OutboxStatuses.Pending, NextAttemptUtc = fact.ObservedAtUtc
                });
            }
        }
    }

    public static Guid DeliveryId(Guid versionId, Guid triggerNodeId, Guid eventId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"NetRatel.Flow.Subscription.V1|{versionId:N}|{triggerNodeId:N}|{eventId:N}")).AsSpan(0, 16));

    public static string PayloadHash(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    public static FlowExecutionSubscriptionDeliveryDto? ReadDelivery(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > FlowExecutionSubscriptionLimits.MaximumIntentBytes) return null;
        try { return JsonSerializer.Deserialize(json, FlowExecutionSubscriptionJsonContext.Default.FlowExecutionSubscriptionDeliveryDto); }
        catch (JsonException) { return null; }
    }
}
