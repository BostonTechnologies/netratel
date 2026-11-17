using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed partial class NetclawInvestigationWorker
{
    // The 0.28.2 flat DTO writes turnNumber:null for non-terminal output.
    // Null carries no turn identity. Malformed or mismatched numbers still fail closed.
    internal static bool MatchesOptionalTurn(JsonElement output, int? beforeSend) =>
        !output.TryGetProperty("turnNumber", out var number) || number.ValueKind == JsonValueKind.Null ||
        number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var turn) && turn > 0 &&
        (beforeSend is null || ProvesLiveRecoveryEnd(output, beforeSend));

    private static void PersistAssistant(OrchestratorDbContext db, AiInvestigationRecord row, SessionOwner owner, string? text)
    {
        // Full text is the authoritative snapshot of its preceding deltas, not an additional reply.
        if (string.IsNullOrWhiteSpace(text)) text = owner.Draft;
        if (!string.IsNullOrWhiteSpace(text) && text != owner.LastAssistantText)
        {
            InvestigationStore.Append(db, row, "assistant", text, trace: new NetclawTraceDto(null, null, null,
                owner.SessionId, row.ActiveMessageId, null, null, null, null, false, false));
            owner.LastAssistantText = text;
        }
        owner.ClearDraft();
    }

    private async Task CompleteTurnAsync(OrchestratorDbContext db, AiInvestigationRecord row, SessionOwner owner,
        JsonElement output, CancellationToken ct, bool reconciled = false)
    {
        PersistAssistant(db, row, owner, null);
        var outcome = Value(output, "turnOutcome");
        var failed = outcome is not null && !string.Equals(outcome, "completed", StringComparison.OrdinalIgnoreCase);
        var quiet = output.TryGetProperty("quietEnd", out var quietEnd) && quietEnd.ValueKind == JsonValueKind.True;
        var start = owner.FollowUp is not null
            ? await db.AiInvestigationEvents.Where(x => x.InvestigationId == row.Id && x.Type == "turn_completed")
                .MaxAsync(x => x.Sequence, ct)
            : await db.AiInvestigationEvents.Where(x => x.InvestigationId == row.Id &&
                x.MessageId == row.ActiveMessageId && x.Type == "operator").Select(x => x.Sequence).SingleAsync(ct);
        var followUp = ObserveFollowUp(owner, output, failed || outcome is null);
        var hasOutput = db.ChangeTracker.Entries<AiInvestigationEventRecord>().Any(x => x.State == EntityState.Added &&
            x.Entity.InvestigationId == row.Id && x.Entity.Sequence > start && x.Entity.Type == "assistant" &&
            !string.IsNullOrWhiteSpace(x.Entity.Text)) ||
            await db.Set<AiInvestigationEventRecord>().AnyAsync(x => x.InvestigationId == row.Id && x.Sequence > start &&
                (x.Type == "assistant" || x.Type == "assistant_recovered") && x.Text.Trim() != "", ct);
        reconciled |= owner.PendingCompletion is not null && hasOutput;
        if (!reconciled && owner.PendingCompletion is null)
            InvestigationStore.Append(db, row, "turn_completed", outcome is null ? "The remote turn ended; its success/failure outcome was not provided." :
                failed ? "The remote turn ended with a non-success outcome." : "The remote turn reported completion.",
                trace: NetclawTraceMapper.Map(output, owner.SessionId, row.ActiveMessageId, owner.Profile.DeviceToken) with { FollowUp = followUp });
        owner.LastTerminalOrdinal = NativeTurnNumber(output) ?? owner.LastTerminalOrdinal;
        if (followUp is not null)
        {
            owner.PendingCompletion = null;
            owner.FollowUp = followUp;
            owner.ScheduledReminders.Clear();
            owner.LastAssistantText = null;
            row.State = await HasPendingApprovalAsync(db, row, ct) ? InvestigationState.AwaitingApproval : InvestigationState.WaitingForFollowUp;
            SetRecoverySummary(db, row, "follow_up_waiting", FollowUpStatus(followUp));
            return;
        }
        if (!hasOutput && !quiet && !failed)
        {
            row.State = InvestigationState.DeliveryUnknown;
            owner.PendingCompletion ??= output.Clone();
            SetRecoverySummary(db, row, "missing_output",
                "Netclaw reported a completed turn without assistant text. The answer is missing and this conversation is not confirmed as answered. The receiver remains attached for delayed output. Use Recover session to check available history; if this daemon cannot replay it, review its transcript and possible side effects before archiving. Nothing was resent.");
            await InvestigationStore.StageTurnResultAsync(db, row, FlowAiResultStatus.DeliveryUnknown, clock.GetUtcNow(), ct);
            return;
        }
        owner.PendingCompletion = null;
        owner.FollowUp = null;
        row.State = await HasPendingApprovalAsync(db, row, ct) ? InvestigationState.AwaitingApproval : InvestigationState.Idle;
        if (row.RecoverySummary?.StartsWith("The parent turn ended.", StringComparison.Ordinal) == true)
            row.RecoverySummary = null;
        if (quiet && !hasOutput)
            SetRecoverySummary(db, row, "quiet_completion", "Netclaw explicitly ended this turn quietly without an assistant reply. Review any recorded tool outcomes before continuing.");
        else if (reconciled)
            SetRecoverySummary(db, row, "output_reconciled", "Delayed assistant text was saved after the remote completion. Review the answer and recorded tool outcomes before continuing. Nothing was resent.");
        else if (row.LastRecoveryAttemptAtUtc is not null || outcome is null)
            row.RecoverySummary = outcome?.ToLowerInvariant() switch
            {
                "completed" => "The exact resumed turn reported completion. Review the recorded evidence for individual tool outcomes before continuing.",
                "interrupted" => "The exact resumed turn was interrupted. Check possible side effects before starting another turn.",
                null => "The remote turn ended, but its success/failure outcome is unavailable. Check possible side effects before continuing.",
                _ => "The exact resumed turn reported a non-success terminal event. Review the recorded outcome before continuing."
            };
        await InvestigationStore.StageTurnResultAsync(db, row, outcome is null ? FlowAiResultStatus.DeliveryUnknown :
            failed ? FlowAiResultStatus.Failed : FlowAiResultStatus.Completed, clock.GetUtcNow(), ct,
            Value(output, "summary"), Value(output, "evidence"), Value(output, "recommendations"));
    }

    private static Task<bool> HasPendingApprovalAsync(OrchestratorDbContext db, AiInvestigationRecord row, CancellationToken ct) =>
        db.Set<AiInvestigationApprovalRecord>().AnyAsync(x => x.InvestigationId == row.Id && x.MessageId == row.ActiveMessageId && x.SelectedKey == null, ct);

    private static async Task CloseApprovalAsync(OrchestratorDbContext db, AiInvestigationRecord row, SessionOwner owner,
        JsonElement output, CancellationToken ct)
    {
        var callId = Value(output, "callId");
        var reason = Value(output, "interactionCloseKind");
        if (callId is not { Length: > 0 and <= 256 } || reason is not ("answered" or "withdrawn")) return;
        var approval = await db.Set<AiInvestigationApprovalRecord>().SingleOrDefaultAsync(x => x.InvestigationId == row.Id &&
            x.MessageId == row.ActiveMessageId && x.CallId == callId, ct);
        if (approval is null || await db.AiInvestigationEvents.AnyAsync(x => x.InvestigationId == row.Id && x.Type == "approval_closed" && x.CallId == callId, ct)) return;
        approval.SelectedKey ??= "closed";
        if (row.State == InvestigationState.AwaitingApproval && !await db.Set<AiInvestigationApprovalRecord>().AnyAsync(x =>
            x.InvestigationId == row.Id && x.MessageId == row.ActiveMessageId && x.CallId != callId && x.SelectedKey == null, ct))
            row.State = owner.FollowUp is null ? InvestigationState.Processing : InvestigationState.WaitingForFollowUp;
        InvestigationStore.Append(db, row, "approval_closed", reason == "answered" ? "Netclaw closed the original approval as answered."
            : "Netclaw withdrew the original approval.", callId: callId,
            trace: NetclawTraceMapper.Map(output, owner.SessionId, row.ActiveMessageId, owner.Profile.DeviceToken));
    }

    private void TraceOutput(Guid id, SessionOwner owner, JsonElement output, string disposition, string reason)
    {
        // Cap each receiver's diagnostics, and never log content, tokens, raw IDs supplied
        // by a foreign event, or unbounded event discriminators.
        var type = Value(output, "type");
        if (type == "text_delta" && owner.DeltaDiagnosticCount++ >= 16 || owner.DiagnosticCount++ >= 128) return;
        var safeType = type is "text" or "text_delta" or "session_joined" or "turn_completed" or "buffer_flush" or
            "tool_call" or "tool_result" or "tool_interaction" or "tool_interaction_closed" or "error" or "file" or "usage" or "compaction" or
            "subagent" or "processing_state" ? type : "unknown";
        logger.LogInformation(new EventId(2, "NetclawOutput"),
            "Netclaw output {Disposition}: investigation {InvestigationId}, tenant {TenantId}, session {SessionId}, message {MessageId}, type {OutputType}, turn {TurnNumber}, text length {TextLength}, reason {Reason}",
            disposition, id, owner.Profile.TenantId, owner.SessionId, owner.MessageId, safeType,
            output.TryGetProperty("turnNumber", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var turn) ? turn : (int?)null,
            Value(output, "text")?.Length ?? 0, reason);
    }
}
