using System.Security.Cryptography;
using System.Text;
using NetRatel.Application.Flows;

namespace NetRatel.Infrastructure.RatelDesk;

public static class RatelDeskReceiverKey
{
    public const string Algorithm = "netratel-flow-receiver-sha256.v1";
    public static bool IsConforming(string? key) => key is { Length: > 0 and <= 256 } &&
        key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '~' or '-');

    public static string Prepare(FlowIncidentActionDraft draft, string? alreadyPersistedWireKey)
    {
        // Never change an already durable key, including '.' and '..'.
        if (alreadyPersistedWireKey is not null)
            return IsConforming(alreadyPersistedWireKey) ? alreadyPersistedWireKey :
                throw new InvalidOperationException("historical-wire-key-invalid-readonly-recovery");
        if (draft.SourceInstanceId == Guid.Empty || draft.Event.OccurrenceId == Guid.Empty ||
            draft.Event.EventId == Guid.Empty || draft.Event.FlowVersionId == Guid.Empty ||
            draft.ActionNodeId == Guid.Empty || draft.TenantId != draft.Event.TenantId)
            throw new ArgumentException("invalid-logical-action-identity");
        var original = $"{draft.SourceInstanceId:N}:{draft.Event.OccurrenceId:N}:{draft.Event.EventId:N}:{draft.Event.FlowVersionId:N}:{draft.ActionNodeId:N}";
        if (draft.IdempotencyKey != original && !IsConforming(draft.IdempotencyKey))
            throw new ArgumentException("invalid-local-action-key");
        if (IsConforming(draft.IdempotencyKey)) return draft.IdempotencyKey;
        var projection = string.Join('\n', "NetRatel.Flow.RatelDeskReceiverKey.v1",
            draft.SourceInstanceId.ToString("D"), draft.Event.OccurrenceId.ToString("D"),
            draft.Event.EventId.ToString("D"), draft.Event.FlowVersionId.ToString("D"),
            draft.ActionNodeId.ToString("D"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(projection)));
    }

    public static string ReceiptEndpoint(string template, string key)
    {
        if (!IsConforming(key) || template.IndexOf("{key}", StringComparison.Ordinal) < 0 ||
            template.IndexOf("{key}", StringComparison.Ordinal) != template.LastIndexOf("{key}", StringComparison.Ordinal))
            throw new ArgumentException("invalid-receipt-endpoint-or-key");
        // Every permitted character is URI-unreserved. No percent escaping is necessary.
        return template.Replace("{key}", key, StringComparison.Ordinal);
    }
}
