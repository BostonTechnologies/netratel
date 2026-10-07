using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

/// <summary>Operation-scoped contexts and transaction/lease fences own durability; no browser or library model is stored.</summary>
public sealed partial class FlowPersistenceService(IServiceScopeFactory scopes, TimeProvider clock) : IFlowDefinitionService, IFlowEventIngress, IFlowExecutionStore, IFlowReceiverEvidenceStore, IFlowSourceIdentityResolver
{
    // PostgreSQL stores microseconds; normalize before returning lease timestamps that are compared
    // with a fresh scoped read during receipt commits.
    private DateTimeOffset Now => new(clock.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidOperationException("flow-state-invalid");
    private async Task<T> WithDb<T>(Func<OrchestratorDbContext, IServiceProvider, Task<T>> action)
    { await using var scope = scopes.CreateAsyncScope(); return await action(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(), scope.ServiceProvider).ConfigureAwait(false); }

    public Task<IReadOnlyList<FlowDefinitionDto>> ListAsync(int tenantId, CancellationToken ct = default) => WithDb<IReadOnlyList<FlowDefinitionDto>>(async (db, _) =>
    {
        var rows = await db.FlowDefinitions.AsNoTracking().Where(row => row.TenantId == tenantId).OrderBy(row => row.Name).ThenBy(row => row.Id).Take(FlowLimits.MaximumFlowsPerTenant).ToListAsync(ct).ConfigureAwait(false);
        var latest = await db.FlowRuns.AsNoTracking().Where(row => row.TenantId == tenantId).GroupBy(row => row.FlowId)
            .Select(group => group.OrderByDescending(row => row.CreatedAtUtc).ThenByDescending(row => row.Id).First()).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(row => Definition(row, latest.FirstOrDefault(run => run.FlowId == row.Id))).ToArray();
    });
    public Task<FlowDefinitionDto?> GetAsync(int tenantId, Guid flowId, CancellationToken ct = default) => WithDb<FlowDefinitionDto?>(async (db, _) =>
    {
        var row = await db.FlowDefinitions.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == flowId, ct).ConfigureAwait(false);
        if (row is null) return null;
        var run = await db.FlowRuns.AsNoTracking().Where(run => run.TenantId == tenantId && run.FlowId == flowId).OrderByDescending(run => run.CreatedAtUtc).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return Definition(row, run);
    });

    public Task<FlowDefinitionWriteResult> CreateAsync(int tenantId, FlowCreateRequest request, string actorId, CancellationToken ct = default) => WithDb(async (db, _) =>
    {
        if (!ValidName(request.Name) || !ValidActor(actorId) || tenantId <= 0) return new FlowDefinitionWriteResult(FlowWriteDisposition.Invalid, Code: "flow-name");
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false); await TenantLockAsync(db, tenantId, ct).ConfigureAwait(false);
        if (!await db.Tenants.AnyAsync(row => row.Id == tenantId, ct).ConfigureAwait(false)) return new(FlowWriteDisposition.NotFound);
        if (await db.FlowDefinitions.CountAsync(row => row.TenantId == tenantId, ct).ConfigureAwait(false) >= FlowLimits.MaximumFlowsPerTenant) return new(FlowWriteDisposition.CapacityExceeded);
        var now = clock.GetUtcNow(); var row = new FlowDefinitionRecord { Id = Guid.NewGuid(), TenantId = tenantId, Name = request.Name.Trim(), Revision = 1, Enabled = true,
            DraftJson = Serialize(request.UseTemplate ? FlowGraphTemplates.IncidentFromAlert() : new FlowGraphDto(1, [], [], new())), CreatedAtUtc = now, UpdatedAtUtc = now };
        db.FlowDefinitions.Add(row); Audit(db, row, actorId, "created"); await db.SaveChangesAsync(ct).ConfigureAwait(false); if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(FlowWriteDisposition.Stored, Definition(row));
    });

    public Task<FlowDefinitionWriteResult> SaveDraftAsync(int tenantId, Guid flowId, FlowSaveDraftRequest request, string actorId, CancellationToken ct = default)
    {
        var validation = FlowGraphValidator.ValidateDraft(request.Graph);
        if (!ValidName(request.Name) || !validation.Valid) return Task.FromResult(new FlowDefinitionWriteResult(FlowWriteDisposition.Invalid, Code: validation.Issues.FirstOrDefault()?.Code ?? "flow-name"));
        return MutateAsync(tenantId, flowId, request.ExpectedRevision, actorId, "draft-saved", row => { row.Name = request.Name.Trim(); row.DraftJson = Serialize(request.Graph); }, ct);
    }
    public Task<FlowDefinitionWriteResult> SetEnabledAsync(int tenantId, Guid flowId, FlowEnabledRequest request, string actorId, CancellationToken ct = default) =>
        MutateAsync(tenantId, flowId, request.ExpectedRevision, actorId, request.Enabled ? "enabled" : "disabled", row => row.Enabled = request.Enabled, ct);

    private Task<FlowDefinitionWriteResult> MutateAsync(int tenantId, Guid flowId, long expected, string actorId, string operation, Action<FlowDefinitionRecord> change, CancellationToken ct) => WithDb(async (db, _) =>
    {
        if (expected < 1 || !ValidActor(actorId)) return new FlowDefinitionWriteResult(FlowWriteDisposition.Invalid);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false); await TenantLockAsync(db, tenantId, ct).ConfigureAwait(false);
        var row = await db.FlowDefinitions.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == flowId, ct).ConfigureAwait(false);
        if (row is null) return new(FlowWriteDisposition.NotFound); if (row.Revision != expected) return new(FlowWriteDisposition.Conflict);
        change(row); row.Revision = checked(row.Revision + 1); row.UpdatedAtUtc = clock.GetUtcNow(); Audit(db, row, actorId, operation);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); } catch (DbUpdateConcurrencyException) { return new(FlowWriteDisposition.Conflict); }
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false); return new(FlowWriteDisposition.Stored, Definition(row));
    });

    public Task<FlowDefinitionWriteResult> CloneAsync(int tenantId, Guid flowId, FlowCloneRequest request, string actorId, CancellationToken ct = default) => WithDb(async (db, _) =>
    {
        if (!ValidName(request.Name) || !ValidActor(actorId)) return new FlowDefinitionWriteResult(FlowWriteDisposition.Invalid);
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false); await TenantLockAsync(db, tenantId, ct).ConfigureAwait(false);
        var source = await db.FlowDefinitions.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == flowId, ct).ConfigureAwait(false);
        if (source is null) return new(FlowWriteDisposition.NotFound); if (source.Revision != request.ExpectedRevision) return new(FlowWriteDisposition.Conflict);
        if (await db.FlowDefinitions.CountAsync(row => row.TenantId == tenantId, ct).ConfigureAwait(false) >= FlowLimits.MaximumFlowsPerTenant) return new(FlowWriteDisposition.CapacityExceeded);
        var graph = Parse<FlowGraphDto>(source.DraftJson); var ids = graph.Nodes.ToDictionary(node => node.Id, _ => Guid.NewGuid());
        graph = graph with { Nodes = graph.Nodes.Select(node => node with { Id = ids[node.Id] }).ToArray(),
            Edges = graph.Edges.Select(edge => edge with { SourceNodeId = ids[edge.SourceNodeId], TargetNodeId = ids[edge.TargetNodeId] }).ToArray() };
        var now = clock.GetUtcNow(); var row = new FlowDefinitionRecord { Id = Guid.NewGuid(), TenantId = tenantId, Name = request.Name.Trim(), Revision = 1, Enabled = false,
            DraftJson = Serialize(graph), CreatedAtUtc = now, UpdatedAtUtc = now };
        db.FlowDefinitions.Add(row); Audit(db, row, actorId, "cloned"); await db.SaveChangesAsync(ct).ConfigureAwait(false); if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(FlowWriteDisposition.Stored, Definition(row));
    });

    public Task<FlowPublishResult> PublishAsync(int tenantId, Guid flowId, FlowRevisionRequest request, FlowExecutionAuthorityDto authority, CancellationToken ct = default) => WithDb(async (db, provider) =>
    {
        if (!FlowContractValidation.ValidAuthority(authority)) return new FlowPublishResult(FlowWriteDisposition.Invalid, Code: "flow-authority");
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false); await TenantLockAsync(db, tenantId, ct).ConfigureAwait(false);
        var row = await db.FlowDefinitions.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == flowId, ct).ConfigureAwait(false);
        if (row is null) return new(FlowWriteDisposition.NotFound); if (row.Revision != request.ExpectedRevision) return new(FlowWriteDisposition.Conflict);
        if (row.PublishedVersionNumber >= FlowLimits.MaximumVersionsPerFlow) return new(FlowWriteDisposition.CapacityExceeded);
        var graph = Parse<FlowGraphDto>(row.DraftJson); var validation = FlowGraphValidator.ValidateComplete(graph);
        if (!validation.Valid) return new(FlowWriteDisposition.Invalid, Code: validation.Issues[0].Code);
        var action = graph.Nodes.Single(node => node.Kind == FlowNodeKind.CreateIncident);
        var connector = await provider.GetRequiredService<IFlowConnectorCatalog>().GetAsync(tenantId, action.ConnectorId!.Value, authority, ct).ConfigureAwait(false);
        if (connector is null || connector.TenantId != tenantId || connector.Id != action.ConnectorId || connector.Revision <= 0) return new(FlowWriteDisposition.ConnectorDenied, Code: "connector-authority");
        if (!connector.Enabled || !connector.CanExecute) return new(FlowWriteDisposition.ConnectorDenied, Code: connector.UnavailableReason ?? "connector-unavailable");
        if (action.ConnectorRevision is { } selectedRevision && selectedRevision != connector.Revision)
            return new(FlowWriteDisposition.Conflict, Code: "connector-revision-conflict");
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Id == action.Id ? node with { ConnectorRevision = connector.Revision } : node).ToArray() };
        if (!FlowGraphValidator.ValidatePublished(graph).Valid) return new(FlowWriteDisposition.Invalid, Code: "published-graph");
        var json = Serialize(graph); var version = new FlowVersionRecord { Id = Guid.NewGuid(), FlowId = flowId, TenantId = tenantId, VersionNumber = row.PublishedVersionNumber + 1,
            GraphJson = json, ConfigurationHash = FlowContractValidation.Hash(Encoding.UTF8.GetBytes(json)), PublishedBy = authority.PrincipalId, PublishedAtUtc = clock.GetUtcNow() };
        db.FlowVersions.Add(version); row.PublishedVersionId = version.Id; row.PublishedVersionNumber = version.VersionNumber; row.Revision++; row.UpdatedAtUtc = version.PublishedAtUtc;
        Audit(db, row, authority.PrincipalId, "version-published"); await db.SaveChangesAsync(ct).ConfigureAwait(false); if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(FlowWriteDisposition.Stored, Version(version));
    });

    public Task<IReadOnlyList<FlowVersionDto>> GetVersionsAsync(int tenantId, Guid flowId, CancellationToken ct = default) => WithDb<IReadOnlyList<FlowVersionDto>>(async (db, _) =>
        (await db.FlowVersions.AsNoTracking().Where(row => row.TenantId == tenantId && row.FlowId == flowId).OrderByDescending(row => row.VersionNumber).Take(FlowLimits.MaximumVersionsPerFlow).ToListAsync(ct).ConfigureAwait(false)).Select(Version).ToArray());
    public Task<IReadOnlyList<FlowRunSummaryDto>> GetRunsAsync(int tenantId, Guid flowId, CancellationToken ct = default) => WithDb<IReadOnlyList<FlowRunSummaryDto>>(async (db, _) =>
        (await db.FlowRuns.AsNoTracking().Where(row => row.TenantId == tenantId && row.FlowId == flowId).OrderByDescending(row => row.CreatedAtUtc).Take(FlowLimits.MaximumHistoryRows).ToListAsync(ct).ConfigureAwait(false)).Select(Summary).ToArray());
    public Task<FlowVersionDto?> GetVersionAsync(int tenantId, Guid versionId, CancellationToken ct = default) => WithDb<FlowVersionDto?>(async (db, _) =>
    {
        var row = await db.FlowVersions.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == versionId, ct).ConfigureAwait(false);
        return row is null ? null : Version(row);
    });
    public Task<FlowRunDetailDto?> GetRunByIdAsync(int tenantId, Guid runId, CancellationToken ct = default) => WithDb<FlowRunDetailDto?>((db, _) => ReadRunAsync(db, tenantId, null, runId, ct));
    public Task<FlowRunDetailDto?> GetRunAsync(int tenantId, Guid flowId, Guid runId, CancellationToken ct = default) => WithDb<FlowRunDetailDto?>(async (db, _) =>
        await ReadRunAsync(db, tenantId, flowId, runId, ct).ConfigureAwait(false));
    private static async Task<FlowRunDetailDto?> ReadRunAsync(OrchestratorDbContext db, int tenantId, Guid? flowId, Guid runId, CancellationToken ct)
    {
        var row = await db.FlowRuns.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && (flowId == null || row.FlowId == flowId) && row.Id == runId, ct).ConfigureAwait(false);
        if (row is null) return null;
        var actions = await db.FlowActions.AsNoTracking().Where(action => action.TenantId == tenantId && action.RunId == runId).ToListAsync(ct).ConfigureAwait(false);
        return new(Summary(row), Parse<FlowEventEnvelope>(row.EventJson).Data, actions.Select(action => new FlowActionSummaryDto(action.NodeId, action.IdempotencyKey, action.Status, action.Attempts,
            action.ConnectorRevision, action.Code, action.ReceiptJson is null ? null : Parse<FlowActionReceiptDto>(action.ReceiptJson))).ToArray());
    }

    public Task<FlowIngressResult> EnqueueAsync(FlowEventEnvelope input, CancellationToken ct = default) => WithDb(async (db, _) =>
    {
        if (!FlowContractValidation.ValidEnvelope(input)) return new FlowIngressResult(FlowIngressDisposition.Invalid, Code: "flow-event");
        var json = Serialize(input); var fingerprint = FlowContractValidation.Hash(Encoding.UTF8.GetBytes(json));
        await using var transaction = await BeginAsync(db, ct).ConfigureAwait(false); await TenantLockAsync(db, input.TenantId, ct).ConfigureAwait(false);
        var existing = await db.FlowRuns.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == input.TenantId && row.EventId == input.EventId && row.FlowVersionId == input.FlowVersionId, ct).ConfigureAwait(false);
        if (existing is not null) return new(existing.EventFingerprint == fingerprint ? FlowIngressDisposition.Duplicate : FlowIngressDisposition.Conflict, existing.Id);
        // Once bounded history is pruned, an expired event must never start a fresh side effect.
        if (input.OccurredAtUtc <= Now - FlowLimits.MaximumRetryAge || input.OccurredAtUtc > Now.AddMinutes(5))
            return new(FlowIngressDisposition.Invalid, Code: "flow-event-expired");
        var version = await db.FlowVersions.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == input.TenantId && row.Id == input.FlowVersionId, ct).ConfigureAwait(false);
        if (version is null || !FlowGraphValidator.ValidatePublished(Parse<FlowGraphDto>(version.GraphJson)).Valid) return new(FlowIngressDisposition.Invalid, Code: "flow-version");
        var enabled = await db.FlowDefinitions.AnyAsync(row => row.TenantId == input.TenantId && row.Id == version.FlowId && row.Enabled, ct).ConfigureAwait(false);
        var active = new[] { FlowRunStatus.Queued, FlowRunStatus.Running, FlowRunStatus.RetryWaiting };
        if (await db.FlowRuns.CountAsync(row => row.TenantId == input.TenantId, ct).ConfigureAwait(false) >= FlowLimits.MaximumRunsPerTenant ||
            await db.FlowRuns.CountAsync(row => row.TenantId == input.TenantId && active.Contains(row.Status), ct).ConfigureAwait(false) >= FlowLimits.MaximumQueuedRunsPerTenant) return new(FlowIngressDisposition.CapacityExceeded);
        var run = new FlowRunRecord { Id = Guid.NewGuid(), TenantId = input.TenantId, FlowId = version.FlowId, FlowVersionId = version.Id, EventId = input.EventId,
            OccurrenceId = input.OccurrenceId, EventJson = json, EventFingerprint = fingerprint, Status = enabled ? FlowRunStatus.Queued : FlowRunStatus.Failed, CreatedAtUtc = clock.GetUtcNow(),
            CompletedAtUtc = enabled ? null : clock.GetUtcNow(), Code = enabled ? null : "flow-disabled" };
        db.FlowRuns.Add(run); await db.SaveChangesAsync(ct).ConfigureAwait(false); if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(enabled ? FlowIngressDisposition.Enqueued : FlowIngressDisposition.Disabled, run.Id, run.Code);
    });

    private static bool ValidName(string name) => FlowGraphValidator.IsBoundedText(name, FlowLimits.MaximumNameLength);
    private static bool ValidActor(string id) => FlowGraphValidator.IsBoundedText(id, 256);
    private void Audit(OrchestratorDbContext db, FlowDefinitionRecord row, string actor, string operation) => db.FlowAudits.Add(new()
    { Id = Guid.NewGuid(), TenantId = row.TenantId, FlowId = row.Id, Revision = row.Revision, ActorId = actor, Operation = operation, AtUtc = clock.GetUtcNow() });
    private static FlowDefinitionDto Definition(FlowDefinitionRecord row, FlowRunRecord? run = null) => new(row.Id, row.TenantId, row.Name, row.Revision, row.Enabled,
        Parse<FlowGraphDto>(row.DraftJson), row.PublishedVersionId, row.PublishedVersionNumber, row.UpdatedAtUtc, run is null ? null : Summary(run));
    private static FlowVersionDto Version(FlowVersionRecord row) => new(row.Id, row.FlowId, row.TenantId, row.VersionNumber, Parse<FlowGraphDto>(row.GraphJson), row.ConfigurationHash, row.PublishedBy, row.PublishedAtUtc);
    private static FlowRunSummaryDto Summary(FlowRunRecord row) => new(row.Id, row.FlowId, row.FlowVersionId, row.EventId, row.OccurrenceId, row.Status, row.CreatedAtUtc, row.CompletedAtUtc, row.Code);
    private static async Task<IDbContextTransaction?> BeginAsync(OrchestratorDbContext db, CancellationToken ct) => db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false) : null;
    private static async Task TenantLockAsync(OrchestratorDbContext db, int tenantId, CancellationToken ct)
    { if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({733450001}, {tenantId})", ct).ConfigureAwait(false); }
}
