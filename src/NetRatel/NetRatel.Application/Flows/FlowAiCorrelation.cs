using System.Security.Cryptography;
using System.Text;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>One deterministic automated turn per persisted action. Historical alert continuations retain their original message ID.</summary>
public static class FlowAiCorrelation
{
    public static Guid MessageId(FlowIncidentActionDraft draft) =>
        draft.Event.ExecutionEvent is null ? draft.Event.EventId : ExecutionMessageId(draft.IdempotencyKey);

    public static Guid ExecutionMessageId(string actionKey)
    {
        if (!FlowGraphValidator.IsBoundedText(actionKey, 256))
            throw new ArgumentException("A stable persisted flow action key is required.", nameof(actionKey));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("netratel-flow-ai-turn-v1\0" + actionKey));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
