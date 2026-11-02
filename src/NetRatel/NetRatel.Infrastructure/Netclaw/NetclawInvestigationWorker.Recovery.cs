using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed partial class NetclawInvestigationWorker
{
    public static int? TurnCount(JsonElement output) => output.TryGetProperty("turnCount", out var count)
        && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var value) && value >= 0 ? value : null;

    // A count advanced exactly once since the pre-send handshake proves a terminal turn, not its
    // success. The legacy joined contract cannot distinguish a failed terminal turn from success.
    public static bool ProvesHistoricalEnd(JsonElement output, Guid id, Guid? message, int? beforeSend) =>
        beforeSend is >= 0 and < int.MaxValue && TurnCount(output) == beforeSend + 1 && ProvesAdmission(output, id, message);

    private async Task ReconcileHistoryAsync(OrchestratorDbContext db, AiInvestigationRecord row, JsonElement output, bool admitted)
    {
        if (admitted && output.TryGetProperty("recentMessages", out var history))
        {
            var marker = MessageMarker(row.Id, row.ActiveMessageId!.Value);
            var messages = history.EnumerateArray().ToArray();
            var index = Array.FindIndex(messages, item => Value(item, "role") == "user" && Value(item, "content")?.StartsWith(marker, StringComparison.Ordinal) == true);
            var operatorSequence = await db.Set<AiInvestigationEventRecord>().Where(x => x.InvestigationId == row.Id && x.MessageId == row.ActiveMessageId)
                .Select(x => (long?)x.Sequence).SingleOrDefaultAsync(stopping) ?? long.MaxValue;
            var saved = await db.Set<AiInvestigationEventRecord>().Where(x => x.InvestigationId == row.Id && x.Sequence > operatorSequence && (x.Type == "assistant" || x.Type == "assistant_recovered"))
                .Select(x => x.Text).ToListAsync(stopping);
            foreach (var item in messages.Skip(index + 1).Take(20))
            {
                if (Value(item, "role") != "assistant" || Value(item, "content") is not { Length: > 0 and <= 2000 } content) continue;
                var prefix = content.EndsWith("...", StringComparison.Ordinal) ? content[..^3] : content;
                if (saved.Any(x => x == content || x.StartsWith(prefix, StringComparison.Ordinal))) continue;
                // The remote contract returns bounded excerpts with no original timestamp or terminal marker.
                InvestigationStore.Append(db, row, "assistant_recovered", content);
                saved.Add(content);
            }
        }
        string summary;
        if (ProvesHistoricalEnd(output, row.Id, row.ActiveMessageId, row.RemoteTurnCountBeforeSend))
        {
            row.State = InvestigationState.Idle;
            summary = "The saved pre-send turn count and exact message marker confirm this remote turn ended. Recent history excerpts were restored. The daemon does not provide the historical success/failure outcome; verify any side effects before continuing.";
            // A recovered terminal count must never manufacture a successful automated business result.
            await InvestigationStore.StageTurnResultAsync(db, row, FlowAiResultStatus.DeliveryUnknown, clock.GetUtcNow(), stopping);
        }
        else summary = admitted
            ? "The session is connected and this exact message was admitted. Recent history excerpts were restored, but this daemon does not provide a durable outcome for that message. Completion remains unconfirmed. Wait for live evidence, or archive this conversation with the acknowledgement and start a new one after checking possible side effects."
            : "The session connected, but recent history did not identify this message as the latest admitted turn. Completion remains unconfirmed. Review the daemon history, or archive with the acknowledgement and start a new conversation after checking possible side effects.";
        if (row.RecoverySummary != summary)
            InvestigationStore.Append(db, row, row.State == InvestigationState.Idle ? "recovery_turn_ended" : admitted ? "recovery_admission_confirmed" : "recovery_inconclusive", summary);
        row.RecoverySummary = summary;
    }
}
