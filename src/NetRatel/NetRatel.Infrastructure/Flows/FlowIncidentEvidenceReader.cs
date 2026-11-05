using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

public enum FlowIncidentEvidenceDisposition { Ready = 1, Waiting = 2, Unavailable = 3 }
public sealed record FlowIncidentEvidenceReadResult(FlowIncidentEvidenceDisposition Disposition,
    FlowIncidentActionDraft? Draft = null, string? Code = null);

/// <summary>
/// Reads the exact persisted automated action, never the latest conversation or an in-memory UI turn.
/// The caller supplies raw map fields and recorded canonical attempts, then persists this final draft
/// through the existing fenced action store before receiver preparation freezes its semantic bytes.
/// </summary>
public sealed class FlowIncidentEvidenceReader(OrchestratorDbContext db)
{
    public async Task<FlowIncidentEvidenceReadResult> ReadAsync(FlowIncidentActionDraft draft,
        Guid? investigationNodeId, IReadOnlyList<FlowRecoveryAttemptEvidence>? recovery = null,
        CancellationToken cancellationToken = default)
    {
        if (draft.Event.ExecutionEvent is null) return new(FlowIncidentEvidenceDisposition.Ready, draft);
        FlowIncidentActionDraft? savedDraft = null;
        FlowAiResultDto? result = null;
        if (investigationNodeId is { } nodeId)
        {
            if (nodeId == Guid.Empty) return Unavailable("incident-investigation-node-invalid");
            var action = await db.FlowActions.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == draft.TenantId && row.RunId == draft.RunId && row.NodeId == nodeId, cancellationToken);
            if (action is null) return Unavailable("incident-investigation-record-missing");
            if (action.Status is FlowActionStatus.Pending or FlowActionStatus.Dispatching or FlowActionStatus.WaitingForAi or
                FlowActionStatus.AwaitingApproval or FlowActionStatus.RetryWaiting)
                return new(FlowIncidentEvidenceDisposition.Waiting, Code: "incident-investigation-pending");
            if (action.Status is not (FlowActionStatus.Succeeded or FlowActionStatus.Failed or FlowActionStatus.DeliveryUnknown))
                return Unavailable("incident-investigation-status-invalid");
            savedDraft = FlowInvestigationBinding.ReadDraft(action);
            if (savedDraft is null || savedDraft.ActionNodeId != nodeId || savedDraft.IdempotencyKey != action.IdempotencyKey ||
                action.AiResultJson is null) return Unavailable("incident-investigation-record-invalid");
            try { result = JsonSerializer.Deserialize<FlowAiResultDto>(action.AiResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { return Unavailable("incident-investigation-result-invalid"); }
            if (result is null) return Unavailable("incident-investigation-result-missing");
        }
        try
        {
            return new(FlowIncidentEvidenceDisposition.Ready,
                FlowIncidentEnrichment.Prepare(draft, savedDraft, result, recovery));
        }
        catch (ArgumentException) { return Unavailable("incident-evidence-context-invalid"); }
        catch (InvalidOperationException) { return Unavailable("incident-investigation-not-terminal"); }
    }

    private static FlowIncidentEvidenceReadResult Unavailable(string code) =>
        new(FlowIncidentEvidenceDisposition.Unavailable, Code: code);
}
