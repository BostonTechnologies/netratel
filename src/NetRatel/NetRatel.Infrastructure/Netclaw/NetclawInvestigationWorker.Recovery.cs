using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed partial class NetclawInvestigationWorker
{
    private sealed class NetclawAttachmentException() : Exception(
        "The daemon did not provide an attachment snapshot or supported text-admission version 1. This session cannot be safely reattached. Check the paired daemon's protocol compatibility before continuing.");

    private const string MissingSnapshotSummary = "The saved session was reattached, but this daemon's text-admission protocol does not return a history snapshot on an existing attachment. Admission and completion of this message remain unconfirmed. Another console can divert replies while the connection stays healthy. Review the daemon history and possible side effects; archive with the acknowledgement before starting a new conversation. Nothing was resent.";

    private async Task ConfirmAttachmentAsync(Guid id, SessionOwner owner, SessionEnsureResult attachment, CancellationToken ct)
    {
        if (owner.Joined.Task.IsCompleted || attachment.Created) return;
        if (attachment.TextAdmissionVersion != 1) throw new NetclawAttachmentException();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        if (row.ActiveMessageId != owner.MessageId || row.RemoteSessionId != owner.SessionId || row.ProfileFingerprint != owner.Profile.ProfileFingerprint ||
            row.State == InvestigationState.Archived)
            throw new InvestigationConflictException("The investigation changed while the session attached.");
        if (row.State == InvestigationState.DeliveryUnknown)
            SetRecoverySummary(db, row, "recovery_protocol_limited", MissingSnapshotSummary);
        else if (row.State == InvestigationState.Processing)
            SetRecoverySummary(db, row, "attachment_protocol_limited",
                "The session was attached without a fresh turn snapshot. A new explicit message can be sent, but a terminal event alone cannot confirm its exact outcome. Another console may have an older turn in progress. Check the daemon history if completion remains unconfirmed.");
        // EnsureSession with acknowledged admission permits a *new* explicit send.
        // It supplies neither historical admission nor a terminal outcome.
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        owner.Joined.TrySetResult();
    }

    public async Task ReconcileAsync(int tenantId, Guid id, string actor, CancellationToken ct)
    {
        Guid? messageId;
        DateTimeOffset attempted;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("Investigation not found.");
            if (row.State != InvestigationState.DeliveryUnknown || row.RemoteSessionId is null)
                throw new InvestigationConflictException("Only an unresolved turn with a saved remote session can be recovered.");
            if (row.LastRecoveryAttemptAtUtc is { } last && clock.GetUtcNow() - last < TimeSpan.FromSeconds(30)) return;
            if (row.LastRecoveryAttemptAtUtc is null)
                InvestigationStore.Append(db, row, "recovery_requested", "Operator requested session history without resending the message.", actor);
            // Keep the attempt identity stable across PostgreSQL timestamp precision.
            row.LastRecoveryAttemptAtUtc = attempted = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
            messageId = row.ActiveMessageId;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        SessionOwner? owner = null;
        var keepAttached = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            // GetAsync preserves a healthy attached stream, including a pending approval.
            owner = await GetAsync(tenantId, id, timeout.Token);
            await owner.Joined.Task.WaitAsync(timeout.Token);
            await using var resultScope = scopes.CreateAsyncScope();
            var resultDb = resultScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var state = await resultDb.Set<AiInvestigationRecord>().AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == id).Select(x => x.State).SingleAsync(timeout.Token);
            keepAttached = owner.AdmissionProven && state is InvestigationState.DeliveryUnknown or InvestigationState.Processing or InvestigationState.AwaitingApproval;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Netclaw session recovery unresolved for {InvestigationId}: {ExceptionType}", id, exception.GetType().Name);
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RecordRecoveryFailureAsync(tenantId, id, messageId, attempted,
                exception is NetclawAttachmentException ? exception.Message : ct.IsCancellationRequested || stopping.IsCancellationRequested
                    ? "Recovery was cancelled. The message outcome remains unconfirmed. Nothing was resent; retry recovery when the connection is available."
                    : exception is OperationCanceledException
                        ? "Recovery reached its time limit without a session snapshot. The message outcome remains unconfirmed. Check the paired daemon and its history before retrying. Nothing was resent."
                        : "The saved session could not be recovered with the current connection and protocol. The message outcome remains unconfirmed. Check the paired daemon and its history before retrying. Nothing was resent.", completion.Token);
            ct.ThrowIfCancellationRequested();
            return;
        }
        finally
        {
            // Only an exact admitted turn can still benefit from live terminal/approval output.
            if (owner is not null && !keepAttached) await RetireAsync(id, owner);
        }
    }

    private async Task RecordRecoveryFailureAsync(int tenantId, Guid id, Guid? messageId, DateTimeOffset attempted, string summary, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        if (row.State == InvestigationState.DeliveryUnknown && row.ActiveMessageId == messageId && row.LastRecoveryAttemptAtUtc == attempted)
            SetRecoverySummary(db, row, "recovery_unavailable", summary);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    private static void SetRecoverySummary(OrchestratorDbContext db, AiInvestigationRecord row, string type, string summary)
    {
        if (row.RecoverySummary == summary) return;
        InvestigationStore.Append(db, row, type, summary);
        row.RecoverySummary = summary;
    }

    public static int? TurnCount(JsonElement output) => output.TryGetProperty("turnCount", out var count)
        && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var value) && value >= 0 ? value : null;

    // A count advanced exactly once since the pre-send handshake proves a terminal turn, not its
    // success. The legacy joined contract cannot distinguish a failed terminal turn from success.
    public static bool ProvesHistoricalEnd(JsonElement output, Guid id, Guid? message, int? beforeSend) =>
        beforeSend is >= 0 and < int.MaxValue && TurnCount(output) == beforeSend + 1 && ProvesAdmission(output, id, message);

    public static bool ProvesLiveRecoveryEnd(JsonElement output, int? beforeSend) => beforeSend is >= 0 and < int.MaxValue &&
        output.TryGetProperty("turnNumber", out var number) && number.ValueKind == JsonValueKind.Number &&
        number.TryGetInt32(out var turn) && turn == beforeSend + 1;

    private async Task ReconcileHistoryAsync(OrchestratorDbContext db, AiInvestigationRecord row, JsonElement output, bool admitted, CancellationToken ct)
    {
        if (admitted && output.TryGetProperty("recentMessages", out var history))
        {
            var marker = MessageMarker(row.Id, row.ActiveMessageId!.Value);
            var messages = history.EnumerateArray().ToArray();
            var index = Array.FindIndex(messages, item => Value(item, "role") == "user" && Value(item, "content")?.StartsWith(marker, StringComparison.Ordinal) == true);
            var operatorSequence = await db.Set<AiInvestigationEventRecord>().Where(x => x.InvestigationId == row.Id && x.MessageId == row.ActiveMessageId)
                .Select(x => (long?)x.Sequence).SingleOrDefaultAsync(ct) ?? long.MaxValue;
            var saved = await db.Set<AiInvestigationEventRecord>().Where(x => x.InvestigationId == row.Id && x.Sequence > operatorSequence && (x.Type == "assistant" || x.Type == "assistant_recovered"))
                .Select(x => x.Text).ToListAsync(ct);
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
            await InvestigationStore.StageTurnResultAsync(db, row, FlowAiResultStatus.DeliveryUnknown, clock.GetUtcNow(), ct);
        }
        else summary = admitted
            ? "The session is connected and this exact message was admitted. Recent history excerpts were restored, but this daemon does not provide a durable outcome for that message. Completion remains unconfirmed. Wait for live evidence, or archive this conversation with the acknowledgement and start a new one after checking possible side effects."
            : "The session connected, but recent history did not identify this message as the latest admitted turn. Completion remains unconfirmed. Review the daemon history, or archive with the acknowledgement and start a new conversation after checking possible side effects.";
        if (row.RecoverySummary != summary)
        {
            var evidence = new NetclawTraceDto(null, row.State == InvestigationState.Idle ? "turn ended" : "outcome unconfirmed",
                NetclawTraceMapper.SourceTimestamp(output), row.RemoteSessionId!, row.ActiveMessageId, null, null, null,
                JsonSerializer.Serialize(new { admissionConfirmed = admitted, turnCountBeforeSend = row.RemoteTurnCountBeforeSend,
                    observedTurnCount = TurnCount(output), historicalOutcomeAvailable = false }), false, false);
            InvestigationStore.Append(db, row, row.State == InvestigationState.Idle ? "recovery_turn_ended" : admitted ? "recovery_admission_confirmed" : "recovery_inconclusive", summary, trace: evidence);
        }
        row.RecoverySummary = summary;
    }
}
