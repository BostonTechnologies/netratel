using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutomationTrace;
using AutomationTrace.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.RatelDesk;

public sealed record AutomationReferenceProjectionEvent(int TenantId, string PeerInstanceId, string PeerOrganizationId,
    AutomationOriginContext? Origin, int? NativeRequestId, string? ExecutionId, string? DefinitionId,
    string? FlowRunId, string? ActionId, string? IncidentId, string? IncidentReference, string? IncidentUrl)
{
    public string? ResourceLabel { get; init; }
}

/// <summary>Existing append-only outbox plus idempotent normalized rows. Projection failure never dispatches an incident.</summary>
public static class AutomationReferenceProjection
{
    public const string OutboxType = "Flow.ExecutionExternalReferences.V1";
    private static Guid Id(string identity) => new(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    public static void CaptureBindings(OrchestratorDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<ManagedOrchestrationRequestBinding>().Where(x =>
            x.State is EntityState.Added or EntityState.Modified && x.Entity.OriginContextJson != null).ToArray())
        {
            var binding = entry.Entity;
            var origin = JsonSerializer.Deserialize<AutomationOriginContext>(binding.OriginContextJson!, AutomationTraceValidation.Json);
            if (!AutomationTraceValidation.Origin(origin) || origin!.SourceInstanceId != binding.PeerInstanceId ||
                origin.Organization.Id != binding.PeerTenantId || origin.Request.Id != binding.ParentRequestId ||
                origin.Task.Id != binding.RequestTaskId || origin.AttemptId != binding.CorrelationId)
                throw new InvalidOperationException("reference-origin-binding-invalid");
            if (entry.State == EntityState.Modified && entry.Property(x => x.OriginContextJson).OriginalValue != binding.OriginContextJson)
                throw new InvalidOperationException("Admitted origin snapshots are immutable.");
            Queue(db, new(binding.TenantId, binding.PeerInstanceId, binding.PeerTenantId, origin, binding.RequestId,
                binding.ExecutionId, binding.JobDefinitionId, null, null, null, null, null));
        }
    }
    public static void Queue(OrchestratorDbContext db, AutomationReferenceProjectionEvent value)
    {
        var json = JsonSerializer.Serialize(value, AutomationTraceValidation.Json);
        var id = Id(OutboxType + "|" + json);
        if (db.OutboxMessages.Local.Any(x => x.Id == id) || db.OutboxMessages.AsNoTracking().Any(x => x.Id == id)) return;
        db.OutboxMessages.Add(new() { Id = id, Type = OutboxType, Source = "automation-reference",
            TenantId = value.TenantId.ToString(CultureInfo.InvariantCulture), OccurredUtc = DateTimeOffset.UtcNow,
            EntityId = value.NativeRequestId?.ToString(CultureInfo.InvariantCulture) ?? value.FlowRunId,
            PayloadJson = json, CorrelationId = value.Origin?.CorrelationId ?? value.FlowRunId ?? "reference-projection" });
    }
    public static void QueueReceipt(OrchestratorDbContext db, RatelDeskReceiverPreparationV2 prepared,
        RatelDeskVerifiedReceipt receipt, FlowIncidentActionRequest action)
    {
        var context = action.PresentationPayload?.Context;
        var source = action.Event.ExecutionEvent;
        var origin = context?.Origin ?? source?.OriginContext;
        var native = context?.NativeRequestId ?? source?.NativeRequestId ?? (source?.SourceKind == FlowExecutionSourceKind.Request ? source.ExecutionId : null);
        var execution = context?.JobExecution?.RunId ?? (context?.SourceKind != "script" ? context?.ExecutionId : null) ?? (source?.SourceKind == FlowExecutionSourceKind.Job ? source.ExecutionId : source?.ParentExecutionId);
        var definition = context?.JobExecution?.DefinitionId ?? (context?.SourceKind != "script" ? context?.Definition?.Id : null) ?? source?.Definition.Id;
        Queue(db, new(action.TenantId, prepared.Peer.ReceiverInstanceId, prepared.Peer.OrganizationId, origin,
            int.TryParse(native, NumberStyles.None, CultureInfo.InvariantCulture, out var requestId) ? requestId : null,
            execution, definition, action.RunId.ToString("D"), action.ActionNodeId.ToString("D"), receipt.IncidentId,
            receipt.TrackingId, receipt.PresentationReceipt?.PublicLink) { ResourceLabel = context?.Definition?.Name ?? origin?.Request.Title });
    }
    public static IReadOnlyList<ExternalResourceReferenceRecord> Rows(AutomationReferenceProjectionEvent value)
    {
        if (value.TenantId <= 0 || !AutomationTraceValidation.GuidId(value.PeerInstanceId) || !AutomationTraceValidation.Text(value.PeerOrganizationId, 64))
            throw new InvalidDataException("reference-projection-scope-invalid");
        var resources = new List<(string Kind, string Id)>();
        if (value.NativeRequestId is > 0) resources.Add(("request", value.NativeRequestId.Value.ToString(CultureInfo.InvariantCulture)));
        var hasRun = long.TryParse(value.ExecutionId, NumberStyles.None, CultureInfo.InvariantCulture, out var run) && run > 0;
        var hasDefinition = long.TryParse(value.DefinitionId, NumberStyles.None, CultureInfo.InvariantCulture, out var definition) && definition > 0;
        if (hasRun && hasDefinition) resources.Add(("job-run", run.ToString(CultureInfo.InvariantCulture)));
        var flow = Guid.TryParseExact(value.FlowRunId, "D", out var flowId) && flowId != Guid.Empty;
        if (flow) resources.Add(("flow-run", flowId.ToString("D")));
        var references = new List<(string Kind, string Id, string Reference, string? Url, string Title)>();
        if (value.Origin is { } origin && AutomationTraceValidation.Origin(origin) && origin.SourceInstanceId == value.PeerInstanceId && origin.Organization.Id == value.PeerOrganizationId)
            references.Add(("source-request", origin.Request.Id, origin.Request.Reference, origin.Links.FirstOrDefault(x => x.Kind == "source-request")?.Url, origin.Request.Title));
        if (value.IncidentId is { } incident && value.IncidentReference is { } reference)
            references.Add(("incident", incident, reference, value.IncidentUrl, value.ResourceLabel ?? "Automation incident " + reference));
        var rows = new List<ExternalResourceReferenceRecord>();
        foreach (var external in references)
        {
            if (!AutomationTraceValidation.Text(external.Reference, 128) || !AutomationTraceValidation.Text(external.Id, 256)) throw new InvalidDataException("reference-projection-identity-invalid");
            foreach (var resource in resources)
            {
                var identity = JsonSerializer.Serialize(new { value.TenantId, value.PeerInstanceId, value.PeerOrganizationId,
                    ReferenceKind = external.Kind, ExternalId = external.Id, ResourceKind = resource.Kind, ResourceId = resource.Id, value.ActionId }, AutomationTraceValidation.Json);
                rows.Add(new() { Id = Id(identity), TenantId = value.TenantId, PeerInstanceId = value.PeerInstanceId,
                    PeerOrganizationId = value.PeerOrganizationId, ReferenceKind = external.Kind, ExternalId = external.Id,
                    Reference = external.Reference, NormalizedReference = external.Reference.ToUpperInvariant(), ExternalUrl = external.Url,
                    SafeTitle = external.Title[..Math.Min(256, external.Title.Length)], ResourceKind = resource.Kind, ResourceId = resource.Id,
                    NativeRequestId = value.NativeRequestId, JobRunId = hasRun ? run : null, JobDefinitionId = hasDefinition ? definition : null,
                    FlowRunId = flow ? flowId : null, ActionId = Guid.TryParse(value.ActionId, out var actionId) ? actionId : null,
                    EvidenceKind = external.Kind == "incident" ? "verified-receipt" : "trusted-admission" });
            }
        }
        return rows;
    }
    public static async Task<int> ProcessBatchAsync(OrchestratorDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(710749)", ct);
        var batch = await db.OutboxMessages.Where(x => x.Type == OutboxType && x.Status == OutboxStatuses.Pending)
            .OrderBy(x => x.OccurredUtc).ThenBy(x => x.Id).Take(32).ToArrayAsync(ct);
        foreach (var message in batch)
        {
            var value = JsonSerializer.Deserialize<AutomationReferenceProjectionEvent>(message.PayloadJson, AutomationTraceValidation.Json)
                ?? throw new InvalidDataException("reference-outbox-invalid");
            foreach (var row in Rows(value))
                if (!db.Set<ExternalResourceReferenceRecord>().Local.Any(x => x.Id == row.Id) &&
                    !await db.Set<ExternalResourceReferenceRecord>().AnyAsync(x => x.Id == row.Id, ct)) db.Add(row);
            message.Status = OutboxStatuses.Published;
        }
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return batch.Length;
    }

    public static async Task BackfillBatchAsync(OrchestratorDbContext db, IRatelDeskReceiverFingerprint fingerprint, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(710750)", ct);
        var cursor = await db.Set<AutomationReferenceBackfillCheckpoint>().SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (cursor?.Completed == true) { await transaction.CommitAsync(ct); return; }
        if (cursor is null) { cursor = new(); db.Add(cursor); }
        if (!cursor.AdmissionsCompleted)
        {
            var bindings = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().Where(x => x.RequestId > cursor.LastRequestId)
                .OrderBy(x => x.RequestId).Take(100).ToArrayAsync(ct);
            foreach (var binding in bindings)
            {
                // Opaque historical meta is not authority. Missing typed admitted context stays unavailable.
                if (binding.OriginContextJson is not null)
                {
                    try
                    {
                        var origin = JsonSerializer.Deserialize<AutomationOriginContext>(binding.OriginContextJson, AutomationTraceValidation.Json);
                        if (AutomationTraceValidation.Origin(origin) && origin!.SourceInstanceId == binding.PeerInstanceId &&
                            origin.Organization.Id == binding.PeerTenantId && origin.Request.Id == binding.ParentRequestId &&
                            origin.Task.Id == binding.RequestTaskId && origin.AttemptId == binding.CorrelationId)
                            Queue(db, new(binding.TenantId, binding.PeerInstanceId, binding.PeerTenantId, origin, binding.RequestId,
                                binding.ExecutionId, binding.JobDefinitionId, null, null, null, null, null));
                        else cursor.UnavailableAdmissions++;
                    }
                    catch (JsonException) { cursor.UnavailableAdmissions++; }
                }
                else cursor.UnavailableAdmissions++;
                cursor.LastRequestId = binding.RequestId;
            }
            cursor.AdmissionsCompleted = bindings.Length < 100;
        }
        else
        {
            var receipts = await db.FlowReceiverEvidence.AsNoTracking().Where(x => x.FullReceiptJson != null)
                .OrderBy(x => x.TenantId).ThenBy(x => x.RunId).ThenBy(x => x.NodeId).Skip(cursor.ReceiptOffset).Take(50).ToArrayAsync(ct);
            foreach (var record in receipts)
            {
                try
                {
                    var prepared = JsonSerializer.Deserialize<RatelDeskReceiverPreparationV2>(record.PreparationJson, AutomationTraceValidation.Json)!;
                    var accepted = JsonSerializer.Deserialize<RatelDeskVerifiedReceipt>(record.FullReceiptJson!, AutomationTraceValidation.Json)!;
                    var action = await db.FlowActions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == record.TenantId && x.RunId == record.RunId && x.NodeId == record.NodeId, ct);
                    var run = await db.FlowRuns.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == record.TenantId && x.Id == record.RunId, ct);
                    var request = action?.PreparedJson is null ? null : JsonSerializer.Deserialize<FlowIncidentActionRequest>(action.PreparedJson, AutomationTraceValidation.Json);
                    var draft = action is null ? null : JsonSerializer.Deserialize<FlowIncidentActionDraft>(action.DraftJson, AutomationTraceValidation.Json);
                    if (run is null || request is null || draft is null || !ReceiverPreparedBinding.Valid(prepared, request, draft, run.CreatedAtUtc, fingerprint))
                        throw new InvalidDataException("legacy-reference-proof-unavailable");
                    var verified = ReceiverWireValidation.Receipt(Encoding.UTF8.GetBytes(accepted.ExactAcceptedBodyJson), accepted.Location, prepared);
                    QueueReceipt(db, prepared, verified, request);
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
                { cursor.UnavailableReceipts++; }
                cursor.ReceiptOffset++;
            }
            cursor.Completed = receipts.Length < 50;
        }
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    }
}

public sealed class AutomationReferenceProjectionWorker(IServiceScopeFactory scopes, ILogger<AutomationReferenceProjectionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await AutomationReferenceProjection.ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(), stoppingToken);
                await AutomationReferenceProjection.BackfillBatchAsync(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IRatelDeskReceiverFingerprint>(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { log.LogWarning("Automation reference projection deferred ({ErrorType}).", error.GetType().Name); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
