using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

/// <summary>PostgreSQL owns message admission and transcript ordering. Transport work never starts inside a read.</summary>
public sealed class InvestigationStore(OrchestratorDbContext db, TimeProvider clock)
{
    public async Task<IReadOnlyList<InvestigationSummaryDto>> ListAsync(int tenantId, int skip, CancellationToken ct, bool includeAlerts = true)
    {
        if (skip < 0 || skip > 100000) throw new ArgumentException("Invalid history offset.");
        var rows = await db.Set<AiInvestigationRecord>().AsNoTracking().Where(x => x.TenantId == tenantId && (includeAlerts || x.OccurrenceId == null))
            .OrderByDescending(x => x.LastActivityUtc).ThenBy(x => x.Id).Skip(skip).Take(100).ToListAsync(ct);
        return rows.Select(Summary).ToArray();
    }

    public async Task<AiInvestigationRecord> CreateAsync(int tenantId, InvestigationCreateRequest request, string actor,
        CancellationToken ct, string? sourceActionKey = null, Guid? flowRunId = null)
    {
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200 ||
            request.Title.Any(char.IsControl) || request.AgentIds is not { Count: > 0 and <= 20 } ||
            request.AgentIds.Any(x => x == Guid.Empty) || request.AgentIds.Distinct().Count() != request.AgentIds.Count)
            throw new ArgumentException("Provide a request ID, a title of up to 200 characters and 1–20 unique client IDs.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(822, {tenantId})", ct);
        var existing = await db.Set<AiInvestigationRecord>().SingleOrDefaultAsync(x => x.TenantId == tenantId &&
            (x.RequestId == request.RequestId || sourceActionKey != null && x.SourceActionKey == sourceActionKey), ct);
        if (existing is not null)
        {
            if (existing.Title != request.Title.Trim() || existing.CreatedBy != actor || existing.OccurrenceId != request.OccurrenceId ||
                !existing.AgentIds.Order().SequenceEqual(request.AgentIds.Order()))
                throw new InvestigationConflictException("This request ID already belongs to another investigation request.");
            return existing;
        }
        if (await db.Set<AiInvestigationRecord>().CountAsync(x => x.TenantId == tenantId && x.State != InvestigationState.Archived, ct) >= 256)
            throw new InvestigationConflictException("Archive completed investigations before opening more.");
        await ValidateClientsAsync(tenantId, request.AgentIds, ct);
        if (request.OccurrenceId is { } occurrence &&
            !await db.MonitoringOccurrences.AnyAsync(x => x.TenantId == tenantId && x.OccurrenceId == occurrence && (request.AgentIds.Contains(x.AgentId) || sourceActionKey != null && x.AgentId == Guid.Empty), ct))
            throw new KeyNotFoundException("The alert does not belong to the selected tenant and clients.");
        var row = new AiInvestigationRecord { TenantId = tenantId, RequestId = request.RequestId, Title = request.Title.Trim(),
            AgentIds = request.AgentIds.ToArray(), OccurrenceId = request.OccurrenceId, CreatedBy = actor,
            SourceActionKey = sourceActionKey, FlowRunId = flowRunId, LastActivityUtc = clock.GetUtcNow() };
        db.Add(row); Append(db, row, "created", sourceActionKey is null ? "Investigation opened." : "Investigation opened by a published monitoring flow.", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return row;
    }

    public async Task<InvestigationSnapshotDto> LoadAsync(int tenantId, Guid id, long after, CancellationToken ct)
    {
        var row = await db.Set<AiInvestigationRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct)
            ?? throw new KeyNotFoundException("Investigation not found.");
        if (after < 0 || after > row.LastSequence) throw new ArgumentException("Invalid transcript cursor.");
        var events = await db.Set<AiInvestigationEventRecord>().AsNoTracking()
            .Where(x => x.InvestigationId == id && x.Sequence > after && x.Sequence <= row.LastSequence)
            .OrderBy(x => x.Sequence).Take(200).ToListAsync(ct);
        var approvals = await db.Set<AiInvestigationApprovalRecord>().AsNoTracking()
            .Where(x => x.InvestigationId == id && row.State == InvestigationState.AwaitingApproval && x.MessageId == row.ActiveMessageId && x.SelectedKey == null).Take(32).ToListAsync(ct);
        return new(Summary(row), row.LastSequence, row.ActiveMessageId,
            events.Select(x => new InvestigationEventDto(x.Sequence, x.Type, x.Text, x.Actor, x.CallId,
                x.OptionsJson is null ? null : Options(x.OptionsJson), x.CreatedAtUtc, x.MessageId)).ToArray(),
            await ClientsAsync(tenantId, row.AgentIds, ct),
            approvals.Select(x => new InvestigationApprovalDto(x.CallId, Options(x.OptionsJson))).ToArray());
    }

    public async Task<bool> AcceptMessageAsync(int tenantId, Guid id, InvestigationMessageRequest request, string actor,
        string? credential, bool automated, CancellationToken ct)
    {
        if (request.MessageId == Guid.Empty || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 16000)
            throw new ArgumentException("A message ID and 1–16000 characters are required.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockAsync(tenantId, id, ct);
        var existing = await db.Set<AiInvestigationEventRecord>().SingleOrDefaultAsync(x => x.InvestigationId == id && x.MessageId == request.MessageId, ct);
        if (existing is not null)
        {
            if (existing.Type != "operator" || existing.Text != request.Text || existing.Actor != actor)
                throw new InvestigationConflictException("Message ID already used.");
            return false;
        }
        if (row.State != InvestigationState.Idle) throw new InvestigationConflictException("The investigation already has an active or unresolved turn.");
        if (row.LastSequence >= 10000) throw new InvestigationConflictException("This transcript is full. Start a new investigation.");
        await ValidateClientsAsync(tenantId, row.AgentIds, ct);
        row.State = InvestigationState.Queued; row.ActiveMessageId = request.MessageId; row.Automated = automated;
        row.ActiveCredentialId = credential; row.LastTransportActivityAtUtc = clock.GetUtcNow();
        Append(db, row, "operator", request.Text, actor, request.MessageId);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }

    public async Task<Guid> AcceptApprovalAsync(int tenantId, Guid id, string callId, string selectedKey, string actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockAsync(tenantId, id, ct);
        if (row.State != InvestigationState.AwaitingApproval) throw new InvestigationConflictException("No approval is currently available.");
        var approval = await db.Set<AiInvestigationApprovalRecord>().SingleOrDefaultAsync(x => x.InvestigationId == id && x.CallId == callId, ct)
            ?? throw new KeyNotFoundException("Approval not found.");
        if (approval.SelectedKey is not null) throw new InvestigationConflictException("This approval has already been answered.");
        if (row.ActiveMessageId is not { } messageId || approval.MessageId != messageId)
            throw new InvestigationConflictException("This approval belongs to an earlier turn.");
        if (!Options(approval.OptionsJson).Any(x => x.Key == selectedKey)) throw new ArgumentException("Choose an offered approval option.");
        approval.SelectedKey = selectedKey; approval.Actor = actor;
        row.State = await db.Set<AiInvestigationApprovalRecord>().AnyAsync(x => x.InvestigationId == id && x.MessageId == messageId && x.CallId != callId && x.SelectedKey == null, ct)
            ? InvestigationState.AwaitingApproval : InvestigationState.Processing;
        row.LastTransportActivityAtUtc = clock.GetUtcNow();
        Append(db, row, "approval_response", selectedKey, actor, callId: callId);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return messageId;
    }

    public Task<bool> HasAlertAsync(int tenantId, Guid id, CancellationToken ct) => db.Set<AiInvestigationRecord>().AsNoTracking()
        .AnyAsync(x => x.TenantId == tenantId && x.Id == id && x.OccurrenceId != null, ct);

    public async Task ArchiveAsync(int tenantId, Guid id, InvestigationArchiveRequest request, string actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockAsync(tenantId, id, ct);
        if (row.State == InvestigationState.Archived) return;
        if (row.State != InvestigationState.Idle && (!request.AcknowledgePossibleDelivery || request.ExpectedMessageId != row.ActiveMessageId))
            throw new InvestigationConflictException("Acknowledge possible remote activity for the current turn before archiving it.");
        row.State = InvestigationState.Archived;
        Append(db, row, "archived", "Investigation archived. The remote conversation and its previous actions are preserved.", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<InvestigationClientDto>> ClientsAsync(int tenantId, IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        var query = db.Agents.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (ids is not null) query = query.Where(x => ids.Contains(x.Id));
        return await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Take(ids is null ? 500 : 20)
            .Select(x => new InvestigationClientDto(x.Id, x.Name ?? x.Id.ToString(), x.FriendlyName, x.Tags, x.LastSeenUtc)).ToListAsync(ct);
    }

    public async Task ValidateClientsAsync(int tenantId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0 || await db.Agents.CountAsync(x => x.TenantId == tenantId && ids.Contains(x.Id), ct) != ids.Count)
            throw new KeyNotFoundException("One or more clients are unavailable in this tenant.");
    }

    private async Task<AiInvestigationRecord> LockAsync(int tenantId, Guid id, CancellationToken ct) =>
        await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Investigation not found.");
    public static IReadOnlyList<InvestigationOptionDto> Options(string json) => JsonSerializer.Deserialize<InvestigationOptionDto[]>(json) ?? [];
    public static InvestigationSummaryDto Summary(AiInvestigationRecord row) => new(row.Id, row.TenantId, row.Title, row.State,
        row.AgentIds, row.OccurrenceId, row.FlowRunId, row.LastActivityUtc, row.CreatedBy);
    public static void Append(OrchestratorDbContext db, AiInvestigationRecord row, string type, string text,
        string? actor = null, Guid? messageId = null, string? callId = null, string? options = null)
    {
        row.LastActivityUtc = DateTimeOffset.UtcNow;
        db.Set<AiInvestigationEventRecord>().Add(new() { InvestigationId = row.Id, Sequence = ++row.LastSequence,
            Type = type, Text = text.Length <= 128000 ? text : text[..128000], Actor = actor, MessageId = messageId,
            CallId = callId, OptionsJson = options, CreatedAtUtc = row.LastActivityUtc });
    }
}
