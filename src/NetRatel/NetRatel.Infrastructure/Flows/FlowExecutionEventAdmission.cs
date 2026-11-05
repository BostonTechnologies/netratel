using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using Npgsql;

namespace NetRatel.Infrastructure.Flows;

internal sealed record FlowSourceProof(FlowDefinitionRecord Definition, FlowVersionRecord Version, FlowGraphDto Graph,
    FlowNodeDto Trigger, FlowExecutionAuthorityDto Authority, FlowExecutionEventAggregateDto Aggregate,
    Guid DeliveryId, IReadOnlyList<FlowExecutionEventDto> Facts);
internal sealed record FlowSourceProofResult(FlowDispatchDecision Decision, FlowSourceProof? Proof = null, bool Pending = false);

/// <summary>
/// Admits server-owned lifecycle facts, not browser payloads or Monitoring occurrences. The caller owns the
/// PostgreSQL transaction. Long accepted operations recheck these facts without borrowing an expired Flow lease.
/// </summary>
public sealed class FlowExecutionEventAdmission(IFlowExecutionAuthorityVerifier verifier,
    IEffectiveAccessService access, TimeProvider clock) : IFlowExecutionEventAdmission
{
    public async Task<FlowDispatchDecision> LockAndAdmitInvestigationAsync(OrchestratorDbContext db,
        FlowEventEnvelope input, Guid runId, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null || input.ExecutionEvent is null || input.Data is not null || input.SourceDeliveryId is not { } delivery ||
            input.SourceEventIds is not { Count: > 0 } ids || runId == Guid.Empty)
            return new(false, "execution-source-envelope-invalid");
        var deliveryRow = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(row =>
            row.Id == delivery && row.Type == FlowExecutionSubscriptionLimits.OutboxType, cancellationToken).ConfigureAwait(false);
        var selection = deliveryRow is null ? null : ExecutionLifecycleSubscriptions.ReadDelivery(deliveryRow.PayloadJson);
        if (selection is null) return new(false, "execution-source-selection-unavailable");
        var result = await LoadAsync(db, new(input.TenantId, input.FlowVersionId, selection.SourceEventId, ids, delivery),
            cancellationToken).ConfigureAwait(false);
        if (!result.Decision.Allowed || result.Proof is not { } proof) return result.Decision;
        if (proof.Aggregate.AdmissionKey != input.EventId ||
            proof.Aggregate.Representative.OccurrenceId != input.OccurrenceId ||
            proof.Aggregate.Representative.OccurredAtUtc != input.OccurredAtUtc ||
            !Same(proof.Aggregate.Representative, input.ExecutionEvent) || proof.Authority != input.Authority)
            return new(false, "execution-source-envelope-conflict");
        var run = await db.FlowRuns.FromSqlInterpolated($@"SELECT * FROM ""FlowRuns""
            WHERE ""TenantId"" = {input.TenantId} AND ""Id"" = {runId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run is null || run.FlowVersionId != input.FlowVersionId || run.EventId != input.EventId ||
            run.EventFingerprint != FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(input, FlowPersistenceService.Json)) ||
            run.Status is not (FlowRunStatus.Running or FlowRunStatus.RetryWaiting or FlowRunStatus.WaitingForAi or
                FlowRunStatus.AwaitingApproval or FlowRunStatus.WaitingForExecution))
            return new(false, "execution-source-run-inactive");
        return new(true, "execution-source-admitted");
    }

    internal async Task<FlowSourceProofResult> LoadAsync(OrchestratorDbContext db,
        FlowExecutionEventAdmissionRequest request, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null || request.TenantId <= 0 ||
            request.FlowVersionId == Guid.Empty || request.SourceEventId == Guid.Empty || request.DeliveryId is not { } primaryDelivery ||
            primaryDelivery == Guid.Empty)
            return Deny("execution-source-admission-invalid");
        var ids = (request.ContributingEventIds ?? []).Prepend(request.SourceEventId).Distinct().ToArray();
        if (ids.Length is < 1 or > FlowExecutionEventLimits.MaximumReferences || ids.Any(id => id == Guid.Empty))
            return Deny("execution-source-reference-limit");
        var version = await db.FlowVersions.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == request.TenantId && row.Id == request.FlowVersionId, ct).ConfigureAwait(false);
        if (version is null) return Deny("execution-source-version-unavailable");
        var definition = await db.FlowDefinitions.FromSqlInterpolated($@"SELECT * FROM ""FlowDefinitions""
            WHERE ""TenantId"" = {request.TenantId} AND ""Id"" = {version.FlowId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (definition is null) return Deny("execution-source-flow-unavailable");
        FlowGraphDto? graph;
        FlowExecutionAuthorityDto? authority;
        try
        {
            graph = JsonSerializer.Deserialize<FlowGraphDto>(version.GraphJson, FlowPersistenceService.Json);
            authority = version.ExecutionAuthorityJson is null ? null :
                JsonSerializer.Deserialize<FlowExecutionAuthorityDto>(version.ExecutionAuthorityJson, FlowPersistenceService.Json);
        }
        catch (JsonException) { return Deny("execution-source-version-invalid"); }
        if (graph is null || !FlowGraphV2Validator.ValidatePublished(graph).Valid ||
            version.ConfigurationHash != FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(graph, FlowPersistenceService.Json)) ||
            !FlowContractValidation.ValidAuthority(authority) || authority!.PrincipalId != version.PublishedBy)
            return Deny("execution-source-version-invalid");
        var trigger = graph.Nodes.SingleOrDefault(node => node.Kind == FlowNodeKind.ExecutionEvent);
        if (trigger?.ExecutionTrigger is not { } settings) return Deny("execution-source-trigger-unavailable");
        var activeSince = definition.EnabledSinceUtc is { } enabled && enabled > version.PublishedAtUtc
            ? enabled : version.PublishedAtUtc;
        var now = await EffectiveNowAsync(db, ct).ConfigureAwait(false);
        var facts = new List<FlowExecutionEventDto>(ids.Length);
        Guid? admissionKey = null;
        foreach (var id in ids.Order())
        {
            var source = await db.OutboxMessages.FromSqlInterpolated($@"SELECT * FROM ""OutboxMessages""
                WHERE ""Id"" = {id} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
            var fact = ReadFact(source, request.TenantId);
            if (fact is null || !FlowExecutionEventMatching.Matches(request.TenantId, settings, fact, activeSince) ||
                fact.OccurredAtUtc <= now - FlowLimits.MaximumRetryAge || fact.OccurredAtUtc > now.AddMinutes(5) ||
                fact.ObservedAtUtc > now.AddMinutes(5))
                return Deny("execution-source-fact-invalid");
            var intentId = ExecutionLifecycleSubscriptions.DeliveryId(version.Id, trigger.Id, fact.EventId);
            var intentRow = await db.OutboxMessages.FromSqlInterpolated($@"SELECT * FROM ""OutboxMessages""
                WHERE ""Id"" = {intentId} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
            var intent = intentRow?.Type == FlowExecutionSubscriptionLimits.OutboxType
                ? ExecutionLifecycleSubscriptions.ReadDelivery(intentRow.PayloadJson) : null;
            var key = FlowExecutionEventMatching.AdmissionKey(version.Id, trigger.Id, settings, fact);
            if (intent is null || intent.DeliveryId != intentId || intent.TenantId != request.TenantId ||
                intent.FlowVersionId != version.Id || intent.TriggerNodeId != trigger.Id || intent.SourceEventId != fact.EventId ||
                intent.ConfigurationHash != version.ConfigurationHash || intent.SourcePayloadHash != ExecutionLifecycleSubscriptions.PayloadHash(source!.PayloadJson) ||
                intent.ActiveSinceUtc != activeSince || intent.AdmissionKey != key ||
                intentRow!.TenantId != request.TenantId.ToString(CultureInfo.InvariantCulture) ||
                intentRow.EntityId != version.Id.ToString("N") || intentRow.CorrelationId != key.ToString("N") ||
                admissionKey is { } prior && prior != key)
                return Deny("execution-source-selection-invalid");
            if (id == request.SourceEventId && intentId != primaryDelivery)
                return Deny("execution-source-selection-conflict");
            admissionKey = key;
            if (await ExecutionLifecycleSubscriptionWorker.AwaitsKnownParentAsync(db, settings, fact, ct).ConfigureAwait(false))
                return new(new(false, "execution-parent-result-pending"), Pending: true);
            facts.Add(fact);
        }
        var aggregate = FlowExecutionEventMatching.Aggregate(version.Id, trigger.Id, settings, facts);
        var proof = new FlowSourceProof(definition, version, graph, trigger, authority!, aggregate, primaryDelivery, facts);
        if (!definition.Enabled) return new(new(false, "flow-disabled"), proof);
        if (!await db.Tenants.AsNoTracking().AnyAsync(row => row.Id == request.TenantId && row.DeletedAtUtc == null, ct).ConfigureAwait(false))
            return new(new(false, "execution-source-tenant-unavailable"), proof);
        if (!await verifier.AuthorizeAsync(request.TenantId, authority!, ct).ConfigureAwait(false))
            return new(new(false, "execution-authority-revoked"), proof);
        var principal = Principal(authority!);
        foreach (var permission in facts.Select(fact => fact.SourceKind == FlowExecutionSourceKind.Script
            ? NetRatelPermissions.ScriptExecute : NetRatelPermissions.JobManagement).Distinct())
            if (!await access.AuthorizeAsync(principal, permission, request.TenantId, ct).ConfigureAwait(false))
                return new(new(false, "execution-source-read-denied"), proof);
        foreach (var fact in facts)
        {
            if (fact.Causation?.HopCount >= FlowExecutionEventLimits.MaximumHopCount)
                return new(new(false, "execution-causation-budget-exhausted"), proof);
            if (await HasAncestorAsync(db, request.TenantId, definition.Id, fact.Causation, ct).ConfigureAwait(false))
                return new(new(false, "execution-self-caused-event-suppressed"), proof);
        }
        now = await EffectiveNowAsync(db, ct).ConfigureAwait(false);
        if (facts.Any(fact => fact.OccurredAtUtc <= now - FlowLimits.MaximumRetryAge))
            return new(new(false, "execution-source-event-expired"), proof);
        return new(new(true, "execution-source-admitted"), proof);
    }

    private async Task<bool> HasAncestorAsync(OrchestratorDbContext db, int tenantId, Guid definitionId,
        FlowExecutionCausationDto? cause, CancellationToken ct)
    {
        var seen = new HashSet<Guid>();
        while (cause?.FlowRunId is { } runId)
        {
            if (seen.Count >= FlowExecutionEventLimits.MaximumHopCount || !seen.Add(runId)) return true;
            var ancestor = await db.FlowRuns.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == tenantId && row.Id == runId, ct).ConfigureAwait(false);
            if (ancestor is null || ancestor.FlowId == definitionId) return true;
            try { cause = JsonSerializer.Deserialize<FlowEventEnvelope>(ancestor.EventJson, FlowPersistenceService.Json)?.ExecutionEvent?.Causation; }
            catch (JsonException) { return true; }
        }
        return false;
    }
    private async Task<DateTimeOffset> EffectiveNowAsync(OrchestratorDbContext db, CancellationToken ct) =>
        await ClientConnectionEpochStore.EffectiveNowAsync((NpgsqlConnection)db.Database.GetDbConnection(),
            (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction(), clock, ct).ConfigureAwait(false);
    private static FlowExecutionEventDto? ReadFact(OutboxMessage? row, int tenantId)
    {
        if (row?.Type != FlowExecutionEventLimits.OutboxType ||
            Encoding.UTF8.GetByteCount(row.PayloadJson) > FlowExecutionEventLimits.MaximumEnvelopeBytes) return null;
        try
        {
            var fact = JsonSerializer.Deserialize(row.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto);
            return fact is not null && fact.EventId == row.Id && fact.TenantId == tenantId &&
                row.TenantId == tenantId.ToString(CultureInfo.InvariantCulture) && row.EntityId == fact.ExecutionId &&
                row.Source == fact.Provenance && FlowExecutionEventProvenance.IsNative(fact) &&
                FlowExecutionEventValidator.Validate(fact).Valid ? fact : null;
        }
        catch (JsonException) { return null; }
    }
    private static bool Same(FlowExecutionEventDto left, FlowExecutionEventDto right) =>
        JsonSerializer.Serialize(left, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto) ==
        JsonSerializer.Serialize(right, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto);
    private static ClaimsPrincipal Principal(FlowExecutionAuthorityDto authority)
    {
        var claims = new List<Claim> { new("netratel_principal_id", authority.PrincipalId) };
        if (authority.IntegrationCredentialId is not null) claims.Add(new("netratel_integration_credential_id", authority.IntegrationCredentialId));
        return new(new ClaimsIdentity(claims, "FlowDispatch"));
    }
    private static FlowSourceProofResult Deny(string code) => new(new(false, code));
}
