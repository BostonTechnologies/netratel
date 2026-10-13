using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Netclaw;

/// <summary>One immutable terminal disposition for one admitted automated message. Idle is never completion evidence.</summary>
public sealed class AiInvestigationTurnResultRecord
{
    public int TenantId { get; set; }
    public Guid InvestigationId { get; set; }
    public Guid MessageId { get; set; }
    public string ResultJson { get; set; } = "";
}

public sealed partial class InvestigationStore
{
    public static async Task StageTurnResultAsync(OrchestratorDbContext db, AiInvestigationRecord row,
        FlowAiResultStatus status, DateTimeOffset observedAt, CancellationToken ct, string? summary = null, string? references = null, string? recommendations = null)
    {
        if (!row.Automated || row.ActiveMessageId is not { } message || row.FlowRunId is not { } runId || row.SourceActionKey is null) return;
        if (await db.Set<AiInvestigationTurnResultRecord>().AnyAsync(x => x.TenantId == row.TenantId && x.InvestigationId == row.Id && x.MessageId == message, ct)) return;
        var action = await db.FlowActions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == row.TenantId && x.RunId == runId && x.IdempotencyKey == row.SourceActionKey, ct);
        if (action is null) return;
        var draft = JsonSerializer.Deserialize<FlowIncidentActionDraft>(action.DraftJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (draft is null || draft.Kind != FlowActionKind.NetclawInvestigation || draft.Event.EventId != message || draft.Event.TenantId != row.TenantId || draft.RunId != runId) return;
        var result = new FlowAiResultDto(row.TenantId, runId, draft.Event.FlowVersionId, action.NodeId, row.Id, message, status, observedAt, observedAt);
        if (status == FlowAiResultStatus.Completed)
        {
            var start = await db.Set<AiInvestigationEventRecord>().AsNoTracking().Where(x => x.InvestigationId == row.Id && x.MessageId == message && x.Type == "operator")
                .Select(x => (long?)x.Sequence).SingleOrDefaultAsync(ct);
            if (start is null) return;
            var text = await db.Set<AiInvestigationEventRecord>().AsNoTracking().Where(x => x.InvestigationId == row.Id && x.Sequence > start && x.Type == "assistant")
                .OrderByDescending(x => x.Sequence).Take(4).Select(x => x.Text).ToListAsync(ct);
            // Include assistant content currently staged in this completion transaction as well.
            var staged = db.ChangeTracker.Entries<AiInvestigationEventRecord>().Where(x => x.State == EntityState.Added &&
                x.Entity.InvestigationId == row.Id && x.Entity.Sequence > start && x.Entity.Type == "assistant").OrderBy(x => x.Entity.Sequence).Select(x => x.Entity.Text);
            var evidence = await db.Set<AiInvestigationEventRecord>().AsNoTracking().Where(x => x.InvestigationId == row.Id && x.Sequence > start && x.Type == "tool_result")
                .OrderBy(x => x.Sequence).Take(12).Select(x => x.Text).ToListAsync(ct);
            result = result with { Summary = SafeResultText(summary ?? string.Join("\n\n", text.AsEnumerable().Reverse().Concat(staged)), 3000),
                Evidence = SafeResultText(references ?? string.Join("\n", evidence), 1000), Recommendations = SafeResultText(recommendations, 1000) };
        }
        db.Add(new AiInvestigationTurnResultRecord { TenantId = row.TenantId, InvestigationId = row.Id, MessageId = message,
            ResultJson = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
    }

    public static string SafeResultText(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var safe = new string(value.Where(x => !char.IsControl(x) || x is '\n' or '\r' or '\t').ToArray());
        // Return plain bounded discovery data. It is never rendered as HTML or interpreted as graph instructions.
        if (safe.Length > maximum) safe = safe[..maximum];
        if (safe.Length > 0 && char.IsHighSurrogate(safe[^1])) safe = safe[..^1];
        return safe;
    }
}
