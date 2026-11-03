using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed partial class InvestigationStore
{
    private async Task<InvestigationRecoveryDto?> RecoveryStatusAsync(AiInvestigationRecord row, CancellationToken ct)
    {
        var saved = row.LastRecoveryAttemptAtUtc is null && row.RecoverySummary is null
            ? null : new InvestigationRecoveryDto(row.LastRecoveryAttemptAtUtc, row.RecoverySummary);
        if (row.State is not (InvestigationState.Idle or InvestigationState.DeliveryUnknown)
            || row.ActiveMessageId is null || row.RemoteSessionId is null) return saved;
        // Inspect the current turn even when its failure is beyond the first transcript page.
        var json = await db.Set<AiInvestigationEventRecord>().AsNoTracking()
            .Where(x => x.InvestigationId == row.Id && x.Type == "error" && x.Sequence <= row.LastSequence)
            .OrderByDescending(x => x.Sequence).Select(x => x.TraceJson).FirstOrDefaultAsync(ct);
        if (json is null) return saved;
        var trace = JsonSerializer.Deserialize<NetclawTraceDto>(json);
        if (trace?.SessionId != row.RemoteSessionId || trace.TurnMessageId != row.ActiveMessageId
            || trace.DetailsJson is null) return saved;
        using var details = JsonDocument.Parse(trace.DetailsJson);
        var root = details.RootElement;
        if (!root.TryGetProperty("errorCategory", out var category) || category.ValueKind != JsonValueKind.String
            || category.GetString() != "ProviderFailure"
            || !root.TryGetProperty("errorMessage", out var error) || error.ValueKind != JsonValueKind.String) return saved;
        var message = error.GetString()!;
        if (!message.Contains("HTTP 400", StringComparison.Ordinal)
            || !message.Contains("input[", StringComparison.Ordinal) || !message.Contains("].name", StringComparison.Ordinal)
            || !message.Contains("string does not match pattern", StringComparison.Ordinal)) return saved;
        return new(row.LastRecoveryAttemptAtUtc,
            "The model provider rejected a name in this session's saved history (HTTP 400). Reconnecting cannot rewrite that history. Archive this conversation and start a new chat, or have the daemon administrator repair the saved history before continuing. The transcript and diagnostic reference are preserved; nothing is resent.",
            "provider_history_invalid_name");
    }
}
