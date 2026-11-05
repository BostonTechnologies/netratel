using System.Text.Json;
using NetRatel.Application.Flows;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

/// <summary>Correlation only. The transactional source admission separately establishes current authority and trusted ingress.</summary>
internal static class FlowInvestigationBinding
{
    internal static FlowIncidentActionDraft? ReadDraft(FlowActionRecord? action)
    {
        if (action is null) return null;
        try { return JsonSerializer.Deserialize<FlowIncidentActionDraft>(action.DraftJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return null; }
    }

    internal static bool MatchesExecution(AiInvestigationRecord row, FlowActionRecord? action,
        FlowIncidentActionDraft? draft, NetclawRuntimeProfile? profile = null, string? expectedSessionId = null)
    {
        if (draft?.Event?.ExecutionEvent is not { } source || action is null || !row.Automated ||
            row.Id == Guid.Empty || row.TenantId <= 0 || row.FlowRunId is not { } runId || runId == Guid.Empty ||
            row.OccurrenceId is not null || row.State is not (InvestigationState.Queued or InvestigationState.Processing or InvestigationState.AwaitingApproval) ||
            !FlowGraphValidator.IsBoundedText(row.SourceActionKey, 256) ||
            action.TenantId != row.TenantId || action.RunId != runId || action.NodeId == Guid.Empty ||
            action.NodeId != draft.ActionNodeId || action.IdempotencyKey != row.SourceActionKey ||
            action.Status is not (FlowActionStatus.Dispatching or FlowActionStatus.WaitingForAi or FlowActionStatus.AwaitingApproval) ||
            draft.Kind != FlowActionKind.NetclawInvestigation || draft.TenantId != row.TenantId || draft.RunId != runId ||
            draft.IdempotencyKey != row.SourceActionKey || draft.ConnectorId == Guid.Empty || draft.ConnectorRevision <= 0 ||
            draft.Event.FlowVersionId == Guid.Empty || draft.Event.TenantId != row.TenantId || draft.Event.Data is not null ||
            source.TenantId != row.TenantId || source.EventId != draft.Event.EventId ||
            source.OccurrenceId != draft.Event.OccurrenceId || source.OccurredAtUtc != draft.Event.OccurredAtUtc ||
            !FlowExecutionEventValidator.Validate(source).Valid ||
            draft.Event.Authority is null || draft.Event.Authority.PrincipalId != row.CreatedBy ||
            draft.Event.Authority.IntegrationCredentialId != row.ActiveCredentialId ||
            row.ActiveMessageId != FlowAiCorrelation.MessageId(draft) ||
            source.Target.AgentId is not { } agent || agent == Guid.Empty ||
            row.AgentIds.Length != 1 || row.AgentIds[0] != agent)
            return false;
        if (expectedSessionId is not null && row.RemoteSessionId != expectedSessionId) return false;
        if (profile is not null && (profile.TenantId != row.TenantId || profile.Id != draft.ConnectorId ||
            profile.Revision != draft.ConnectorRevision ||
            row.ProfileFingerprint is not null && row.ProfileFingerprint != profile.ProfileFingerprint))
            return false;
        if (action.AiResultJson is null) return action.Status == FlowActionStatus.Dispatching;
        FlowAiResultDto? saved;
        try { saved = JsonSerializer.Deserialize<FlowAiResultDto>(action.AiResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return false; }
        return saved is not null && saved.TenantId == row.TenantId && saved.FlowRunId == runId &&
            saved.FlowVersionId == draft.Event.FlowVersionId && saved.ActionNodeId == action.NodeId &&
            saved.InvestigationId == row.Id && saved.MessageId == row.ActiveMessageId &&
            saved.Status is FlowAiResultStatus.Queued or FlowAiResultStatus.Processing or FlowAiResultStatus.AwaitingApproval;
    }
}
