using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>Build final source fields once from raw mapping and exact recorded results before persisting the incident action.</summary>
public static class FlowIncidentEnrichment
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static FlowIncidentActionDraft Prepare(FlowIncidentActionDraft draft,
        FlowIncidentActionDraft? savedInvestigation = null, FlowAiResultDto? investigation = null,
        IReadOnlyList<FlowRecoveryAttemptEvidence>? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Event is null) throw new ArgumentException("The incident event is missing.", nameof(draft));
        if (draft.Event.ExecutionEvent is not { } source) return draft;
        if (draft.Kind != FlowActionKind.RatelDeskIncident || draft.Event.Data is not null ||
            draft.Event.TenantId != draft.TenantId || draft.Event.EventId != source.EventId ||
            draft.Event.OccurrenceId != source.OccurrenceId || draft.Event.OccurredAtUtc != source.OccurredAtUtc)
            throw new ArgumentException("The incident does not match its immutable execution source.", nameof(draft));
        if ((savedInvestigation is null) != (investigation is null))
            throw new ArgumentException("Final investigation evidence requires the exact saved action and result.");
        if (investigation is not null)
        {
            if (savedInvestigation is null || !FlowAiCorrelation.MatchesResult(savedInvestigation, investigation) ||
                savedInvestigation.RunId != draft.RunId ||
                !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(savedInvestigation.Event, Json),
                    JsonSerializer.SerializeToElement(draft.Event, Json)) ||
                !Enum.IsDefined(investigation.Status) ||
                investigation.Status == FlowAiResultStatus.Completed && investigation.InvestigationId == Guid.Empty)
                throw new ArgumentException("The findings do not match this incident's saved investigation.");
            if (investigation.Status is FlowAiResultStatus.Queued or FlowAiResultStatus.Processing or FlowAiResultStatus.AwaitingApproval ||
                investigation.CompletedAtUtc is null)
                throw new InvalidOperationException("The saved investigation has no terminal local disposition.");
        }
        var fields = FlowExecutionEvidenceFormatter.Enrich(draft.Fields, source, draft.TenantId, draft.RunId,
            draft.Event.FlowVersionId, investigation, recovery);
        return draft with { Fields = fields };
    }
}
