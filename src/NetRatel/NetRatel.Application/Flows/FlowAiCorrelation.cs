using System.Security.Cryptography;
using System.Text;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>One deterministic automated turn per persisted action. Historical alert continuations retain their original message ID.</summary>
public static class FlowAiCorrelation
{
    public static Guid MessageId(FlowIncidentActionDraft draft) =>
        draft.Event.ExecutionEvent is null ? draft.Event.EventId : ExecutionMessageId(draft.IdempotencyKey);

    public static bool MatchesResult(FlowIncidentActionDraft? draft, FlowAiResultDto? result) =>
        draft?.Event is not null && result is not null && draft.Kind == FlowActionKind.NetclawInvestigation &&
        draft.TenantId > 0 && draft.RunId != Guid.Empty && draft.ActionNodeId != Guid.Empty &&
        draft.Event.TenantId == draft.TenantId && draft.Event.FlowVersionId != Guid.Empty &&
        FlowGraphValidator.IsBoundedText(draft.IdempotencyKey, 256) &&
        result.TenantId == draft.TenantId && result.FlowRunId == draft.RunId &&
        result.FlowVersionId == draft.Event.FlowVersionId && result.ActionNodeId == draft.ActionNodeId &&
        result.MessageId == MessageId(draft);

    public static Guid ExecutionMessageId(string actionKey)
    {
        if (!FlowGraphValidator.IsBoundedText(actionKey, 256))
            throw new ArgumentException("A stable persisted flow action key is required.", nameof(actionKey));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("netratel-flow-ai-turn-v1\0" + actionKey));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
