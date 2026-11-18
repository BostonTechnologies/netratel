using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed partial class NetclawInvestigationWorker
{
    private const int MaximumFollowUpIds = 128;
    private static readonly TimeSpan FollowUpObservationLimit = TimeSpan.FromHours(1);
    private const string FollowUpExpired = "The follow-up observation window expired. Remote work may continue.";

    private sealed record ScheduledTerminal(NetclawScheduledReportDto Identity, NetclawReminderReference Reminder, string? FinalText)
    {
        public string EventKey => $"{Identity.RegistrationId:D}/{Identity.OccurrenceId}";
    }

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

    internal static bool MatchesChildTerminal(JsonElement output, NetclawFollowUpDto followUp)
    {
        if (followUp.ChildRunIds.Count == 0 || ClaimsScheduledOrigin(output) ||
            !ProvesLiveRecoveryEnd(output, followUp.LastTurnNumber) || ReadOpenChildRuns(output) is not { } children)
            return false;
        // In a mixed stream, an unlabelled terminal needs positive evidence of known child work advancing.
        return followUp.Reminders.Count == 0 || followUp.ChildRunIds.Except(children, StringComparer.Ordinal).Any();
    }

    private static bool ClaimsScheduledOrigin(JsonElement output) =>
        HasValue(output, "sourceReminderId") || HasValue(output, "scheduledReport");

    private static bool HasValue(JsonElement output, string property) =>
        output.ValueKind == JsonValueKind.Object && output.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null;

    private static bool MatchesRegistrationOrigin(JsonElement receipt, Guid? messageId, out Guid registrationId)
    {
        registrationId = Guid.Empty;
        return messageId is not null && Guid.TryParseExact(Value(receipt, "messageId"), "D", out var origin) && origin == messageId &&
            Guid.TryParseExact(Value(receipt, "registrationId"), "D", out registrationId) && registrationId != Guid.Empty;
    }

    private static NetclawReminderReference? ReadRegistration(JsonElement output, SessionOwner owner)
    {
        if (Value(output, "toolName") != "set_reminder" || Value(output, "toolFailureCode") is not null) return null;
        if (owner.ScheduledReportVersion == 1 && HasValue(output, "scheduledReminder"))
        {
            var receipt = output.GetProperty("scheduledReminder");
            if (NativeTurnNumber(output) is not null && receipt.ValueKind == JsonValueKind.Object &&
                MatchesRegistrationOrigin(receipt, owner.MessageId, out var registrationId) &&
                Value(receipt, "id") is { Length: <= 50 } id && IsFollowUpId(id) &&
                receipt.TryGetProperty("recurring", out var recurring) && recurring.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return new(id, recurring.GetBoolean()) { RegistrationId = registrationId };
            return null;
        }

        return ReadScheduledReminder(output) is { } observed ? observed with { Unconfirmed = true } : null;
    }

    private static ScheduledTerminal? ReadScheduledTerminal(JsonElement output, SessionOwner owner, NetclawFollowUpDto followUp)
    {
        if (owner.ScheduledReportVersion != 1 || NativeTurnNumber(output) is not { } turn ||
            !ProvesLiveRecoveryEnd(output, followUp.LastTurnNumber) ||
            !output.TryGetProperty("scheduledReport", out var receipt) || receipt.ValueKind != JsonValueKind.Object ||
            !MatchesRegistrationOrigin(receipt, owner.MessageId, out var registrationId) ||
            Value(output, "sourceReminderId") is not { Length: > 2 and <= 66 } occurrence ||
            Value(output, "turnOutcome") is not { Length: > 0 and <= 32 }) return null;

        var separator = occurrence.IndexOf(':');
        if (separator <= 0 || !long.TryParse(occurrence.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture,
                out var fireTime) || fireTime is <= 0 or > 253402300799999 ||
            occurrence != string.Create(CultureInfo.InvariantCulture, $"{occurrence[..separator]}:{fireTime}")) return null;

        var reminder = followUp.Reminders.FirstOrDefault(candidate => !candidate.Unconfirmed &&
            candidate.RegistrationId == registrationId && candidate.Id.AsSpan().SequenceEqual(occurrence.AsSpan(0, separator)));
        if (reminder is null) return null;
        var text = Value(receipt, "finalText");
        return new(new(registrationId, occurrence, turn), reminder,
            text is { Length: <= 128000 } && !string.IsNullOrWhiteSpace(text) ? text : null);
    }

    private static bool RecordScheduledReminder(OrchestratorDbContext db, AiInvestigationRecord row, SessionOwner owner, JsonElement output)
    {
        if (owner.FollowUp is not null || !(owner.SendStarted || owner.AdmissionProven) || ReadRegistration(output, owner) is not { } reminder)
            return false;
        if (owner.ScheduledReminders.TryGetValue(reminder.Id, out var saved) && saved == reminder) return false;
        if (saved is null && owner.ScheduledReminders.Count >= MaximumFollowUpIds)
            throw new InvalidDataException("Native scheduled reports exceeded the observation budget.");
        owner.ScheduledReminders[reminder.Id] = reminder;
        InvestigationStore.Append(db, row, "scheduled_report_registered", "Netclaw registered a report for this session.",
            trace: FollowUpTrace(row, new(0, default, [], owner.ScheduledReminders.Values.ToArray()) { ParentCompleted = false },
                NetclawTraceMapper.SourceTimestamp(output)));
        return true;
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

    private NetclawFollowUpDto? ObserveFollowUp(SessionOwner owner, JsonElement terminal, bool failedOrUnknown,
        ScheduledTerminal? report, bool reportSaved)
    {
        var previous = owner.FollowUp;
        var children = ReadOpenChildRuns(terminal);
        if (previous is not null && (children is null || failedOrUnknown || report is not null))
            children = previous.ChildRunIds.Union(children ?? [], StringComparer.Ordinal).ToArray();

        var reminders = previous?.Reminders ?? owner.ScheduledReminders.Values.ToArray();
        if (report is { Reminder.Recurring: false })
            reminders = reportSaved ? reminders.Where(x => x != report.Reminder).ToArray() :
                reminders.Select(x => x == report.Reminder ? x with { Unconfirmed = true } : x).ToArray();
        if (owner.ScheduledReportVersion != 1 || NativeTurnNumber(terminal) is null)
            reminders = reminders.Select(x => x with { Unconfirmed = true }).ToArray();
        if (NativeTurnNumber(terminal) is null && children is { Length: > 0 })
            throw new InvalidDataException("Native child follow-up lacks a bounded ordered identity.");
        if ((children?.Length ?? 0) > MaximumFollowUpIds)
            throw new InvalidDataException("Too many native child continuations.");
        if ((children?.Length ?? 0) + reminders.Count == 0) return null;
        return new(NativeTurnNumber(terminal) ?? 0, previous?.ObserveUntilUtc ?? clock.GetUtcNow() + FollowUpObservationLimit,
            children ?? [], reminders);
    }

    private async Task<bool> CanSaveScheduledReportAsync(IServiceProvider provider, OrchestratorDbContext db,
        AiInvestigationRecord row, SessionOwner owner, CancellationToken ct)
    {
        var actor = await db.AiInvestigationEvents.AsNoTracking().Where(x => x.InvestigationId == row.Id &&
            x.Type == "operator" && x.MessageId == row.ActiveMessageId).Select(x => x.Actor).SingleOrDefaultAsync(ct);
        if (actor is null || !await AuthorizedAsync(provider, row, actor, ct)) return false;
        // Keep the current profile stable until the same transaction commits the result.
        var profile = await db.NetclawConnections.FromSqlInterpolated(
            $"SELECT * FROM \"NetclawConnections\" WHERE \"TenantId\" = {row.TenantId} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct);
        return profile is { Enabled: true } && profile.Id == owner.Profile.Id && profile.Revision == owner.Profile.Revision &&
            profile.ProfileFingerprint == owner.Profile.ProfileFingerprint && !owner.Retired && await StillOwnAsync(ct);
    }

    private static NetclawTraceDto FollowUpTrace(AiInvestigationRecord row, NetclawFollowUpDto followUp, DateTimeOffset? sourceTimestamp = null) =>
        new(null, null, sourceTimestamp, row.RemoteSessionId!, row.ActiveMessageId, null, null, null, null, false, false) { FollowUp = followUp };

    private static bool CanObserveFollowUp(NetclawFollowUpDto followUp) =>
        followUp.ChildRunIds.Count > 0 || followUp.Reminders.Any(reminder => !reminder.Unconfirmed && reminder.RegistrationId is not null);

    private static void RecordUnconfirmedFollowUp(OrchestratorDbContext db, AiInvestigationRecord row, NetclawFollowUpDto followUp, string reason)
    {
        var summary = $"{reason} {InvestigationStore.ScheduledResultUnavailable}";
        if (row.RecoverySummary == summary) return;
        row.RecoverySummary = summary;
        InvestigationStore.Append(db, row, "scheduled_report_unconfirmed", summary,
            trace: FollowUpTrace(row, InvestigationStore.MarkScheduledResultsUnconfirmed(followUp)));
    }

    private async Task<bool> PreserveSavedApprovalAsync(OrchestratorDbContext db, AiInvestigationRecord row, CancellationToken ct)
    {
        return row.State == InvestigationState.AwaitingApproval && row.RemoteSessionId is not null &&
            await db.NetclawConnections.AnyAsync(x => x.TenantId == row.TenantId && x.Enabled && x.ProfileFingerprint == row.ProfileFingerprint, ct) &&
            await db.Set<AiInvestigationApprovalRecord>().AnyAsync(x => x.InvestigationId == row.Id &&
                x.MessageId == row.ActiveMessageId && x.SelectedKey == null, ct);
    }

    private static string FollowUpStatus(NetclawFollowUpDto followUp) =>
        $"The parent turn ended. Observing {followUp.ChildRunIds.Count} delegated follow-up(s) and " +
        $"{followUp.Reminders.Count(reminder => !reminder.Unconfirmed)} scheduled reminder(s) until {followUp.ObserveUntilUtc:O}." +
        (followUp.Reminders.Any(reminder => reminder.Unconfirmed) ? $" {InvestigationStore.ScheduledResultUnavailable}" : "");
}
