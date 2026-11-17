using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed partial class NetclawInvestigationWorker
{
    private const int MaximumFollowUpIds = 128;
    private static readonly TimeSpan FollowUpObservationLimit = TimeSpan.FromHours(1);

    internal static int? NativeTurnNumber(JsonElement output) =>
        output.TryGetProperty("turnNumber", out var number) && number.ValueKind == JsonValueKind.Number &&
        number.TryGetInt32(out var ordinal) && ordinal is > 0 and < int.MaxValue ? ordinal : null;

    internal static NetclawReminderReference? ReadScheduledReminder(JsonElement output)
    {
        if (Value(output, "toolName") != "set_reminder" || Value(output, "toolFailureCode") is not null ||
            Value(output, "result") is not { Length: > 0 and <= 8192 } result ||
            !result.StartsWith("Reminder '", StringComparison.Ordinal))
            return null;
        var registration = result.LastIndexOf("' scheduled. ", StringComparison.Ordinal);
        var marker = result.LastIndexOf(" ID: ", StringComparison.Ordinal);
        if (registration < 0 || marker <= registration + 13) return null;
        var receipt = result[(registration + 13)..marker];
        const string sessionDelivery = "Delivery: reply to this session.";
        if (!receipt.EndsWith(sessionDelivery, StringComparison.Ordinal) &&
            !receipt.Contains(sessionDelivery + " Expires: ", StringComparison.Ordinal))
            return null;
        var id = result[(marker + 5)..];
        return id.Length <= 50 && IsFollowUpId(id)
            ? new(id, !receipt.StartsWith("Runs once. Next execution: ", StringComparison.Ordinal))
            : null;
    }

    internal static bool MatchesFollowUpTerminal(JsonElement output, NetclawFollowUpDto followUp)
    {
        if (!ProvesLiveRecoveryEnd(output, followUp.LastTurnNumber)) return false;
        var reminder = Value(output, "sourceReminderId");
        if (reminder is not null) return followUp.Reminders.Any(candidate => candidate.Id == reminder);
        return followUp.ChildRunIds.Count > 0 && output.TryGetProperty("openChildRunIds", out var children)
            && children.ValueKind == JsonValueKind.Array;
    }

    private static bool IsFollowUpId(string? id) => id is { Length: > 0 and <= 128 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string[]? ReadOpenChildRuns(JsonElement output)
    {
        if (!output.TryGetProperty("openChildRunIds", out var list) || list.ValueKind == JsonValueKind.Null) return null;
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaximumFollowUpIds)
            throw new InvalidDataException("Invalid native child continuation snapshot.");
        var ids = list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToArray();
        if (ids.Any(id => !IsFollowUpId(id)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidDataException("Invalid or duplicate native child identity.");
        return ids.Select(id => id!).ToArray();
    }

    private NetclawFollowUpDto? ObserveFollowUp(SessionOwner owner, JsonElement terminal, bool failedOrUnknown)
    {
        var previous = owner.FollowUp;
        var children = ReadOpenChildRuns(terminal);
        if (previous is not null && (children is null || failedOrUnknown))
            children = previous.ChildRunIds.Union(children ?? [], StringComparer.Ordinal).ToArray();

        var sourceReminder = Value(terminal, "sourceReminderId");
        var reminders = (previous?.Reminders ?? []).Where(x => x.Recurring || x.Id != sourceReminder)
            .Concat(owner.ScheduledReminders.Values).DistinctBy(x => x.Id).ToArray();
        if ((children?.Length ?? 0) + reminders.Length == 0) return null;
        if (NativeTurnNumber(terminal) is not { } turn || (children?.Length ?? 0) > MaximumFollowUpIds || reminders.Length > MaximumFollowUpIds)
            throw new InvalidDataException("Native follow-up lacks a bounded ordered identity.");
        return new(turn, previous?.ObserveUntilUtc ?? clock.GetUtcNow() + FollowUpObservationLimit, children ?? [], reminders);
    }

    private static async Task<NetclawFollowUpDto?> ReadFollowUpAsync(OrchestratorDbContext db, AiInvestigationRecord row, CancellationToken ct)
    {
        var json = await db.AiInvestigationEvents.AsNoTracking().Where(x => x.InvestigationId == row.Id && x.Type == "turn_completed")
            .OrderByDescending(x => x.Sequence).Select(x => x.TraceJson).FirstOrDefaultAsync(ct);
        var trace = json is null ? null : JsonSerializer.Deserialize<NetclawTraceDto>(json);
        return trace?.SessionId == row.RemoteSessionId && trace?.TurnMessageId == row.ActiveMessageId ? trace?.FollowUp : null;
    }

    private async Task<bool> PreserveSavedApprovalAsync(OrchestratorDbContext db, AiInvestigationRecord row, CancellationToken ct)
    {
        return row.State == InvestigationState.AwaitingApproval && row.RemoteSessionId is not null &&
            await db.NetclawConnections.AnyAsync(x => x.TenantId == row.TenantId && x.Enabled && x.ProfileFingerprint == row.ProfileFingerprint, ct) &&
            await db.Set<AiInvestigationApprovalRecord>().AnyAsync(x => x.InvestigationId == row.Id &&
                x.MessageId == row.ActiveMessageId && x.SelectedKey == null, ct);
    }

    private static string FollowUpStatus(NetclawFollowUpDto followUp) =>
        $"The parent turn ended. Waiting for {followUp.ChildRunIds.Count} delegated follow-up(s) and " +
        $"{followUp.Reminders.Count} scheduled report(s) until {followUp.ObserveUntilUtc:O}. " +
        "Netclaw's attached stream can deliver follow-ups. A disconnected or diverted stream cannot replay an exact result.";
}
