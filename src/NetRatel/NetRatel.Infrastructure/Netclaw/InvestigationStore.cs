using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

/// <summary>PostgreSQL owns message admission and transcript ordering. Transport work never starts inside a read.</summary>
public sealed partial class InvestigationStore(OrchestratorDbContext db, TimeProvider clock, IPromptLibraryService? promptLibrary = null)
{
    public async Task<IReadOnlyList<InvestigationSummaryDto>> ListAsync(int tenantId, int skip, CancellationToken ct, bool includeAlerts = true)
    {
        if (skip < 0 || skip > 100000) throw new ArgumentException("Invalid history offset.");
        var rows = await db.Set<AiInvestigationRecord>().AsNoTracking().Where(x => x.TenantId == tenantId && (includeAlerts || x.OccurrenceId == null))
            .OrderByDescending(x => x.LastActivityUtc).ThenBy(x => x.Id).Skip(skip).Take(100).ToListAsync(ct);
        return rows.Select(Summary).ToArray();
    }

    public async Task<AiInvestigationRecord> CreateAsync(int tenantId, InvestigationCreateRequest request, string actor,
        CancellationToken ct, string? sourceActionKey = null, Guid? flowRunId = null, IReadOnlyList<AiPromptSnapshot>? pinnedPrompts = null)
    {
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200 ||
            request.Title.Any(char.IsControl) || request.AgentIds is not { Count: > 0 and <= 20 } ||
            request.AgentIds.Any(x => x == Guid.Empty) || request.AgentIds.Distinct().Count() != request.AgentIds.Count)
            throw new ArgumentException("Provide a request ID, a title of up to 200 characters and 1–20 unique client IDs.");
        var references = NormalizePrompts(request.SelectedPrompts);
        var referencesJson = JsonSerializer.Serialize(references);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(822, {tenantId})", ct);
        var existing = await db.Set<AiInvestigationRecord>().SingleOrDefaultAsync(x => x.TenantId == tenantId &&
            (x.RequestId == request.RequestId || sourceActionKey != null && x.SourceActionKey == sourceActionKey), ct);
        if (existing is not null)
        {
            if (existing.Title != request.Title.Trim() || existing.CreatedBy != actor || existing.OccurrenceId != request.OccurrenceId ||
                existing.FlowRunId != flowRunId || existing.SourceActionKey != sourceActionKey ||
                !existing.AgentIds.Order().SequenceEqual(request.AgentIds.Order()) || !NormalizePrompts(JsonSerializer.Deserialize<AiPromptReference[]>(existing.SelectedPromptReferencesJson)).SequenceEqual(references))
                throw new InvestigationConflictException("This request ID already belongs to another investigation request.");
            return existing;
        }
        if (await db.Set<AiInvestigationRecord>().CountAsync(x => x.TenantId == tenantId && x.State != InvestigationState.Archived, ct) >= 256)
            throw new InvestigationConflictException("Archive completed investigations before opening more.");
        await ValidateClientsAsync(tenantId, request.AgentIds, ct);
        if (request.OccurrenceId is { } occurrence &&
            !await db.MonitoringOccurrences.AnyAsync(x => x.TenantId == tenantId && x.OccurrenceId == occurrence && (request.AgentIds.Contains(x.AgentId) || sourceActionKey != null && x.AgentId == Guid.Empty), ct))
            throw new KeyNotFoundException("The alert does not belong to the selected tenant and clients.");
        var library = promptLibrary ?? new PromptLibraryService(db, clock);
        var snapshots = new List<AiPromptSnapshot>();
        var tenantDefault = await library.ResolveDefaultAsync(tenantId, ct);
        if (tenantDefault is not null) snapshots.Add(tenantDefault);
        foreach (var reference in references)
        {
            var snapshot = await library.ResolveAsync(tenantId, reference, allowArchived: pinnedPrompts is not null && sourceActionKey is not null, ct: ct);
            if (pinnedPrompts is not null && (sourceActionKey is null || !pinnedPrompts.Any(x => x == snapshot)))
                throw new KeyNotFoundException("The published prompt snapshot does not match its authorized revision.");
            if (snapshots.All(x => x.PromptId != snapshot.PromptId || x.Revision != snapshot.Revision)) snapshots.Add(snapshot);
        }
        var row = new AiInvestigationRecord { TenantId = tenantId, RequestId = request.RequestId, Title = request.Title.Trim(),
            AgentIds = request.AgentIds.ToArray(), OccurrenceId = request.OccurrenceId, CreatedBy = actor,
            SourceActionKey = sourceActionKey, FlowRunId = flowRunId, LastActivityUtc = clock.GetUtcNow(),
            PromptSnapshotsJson = JsonSerializer.Serialize(snapshots), SelectedPromptReferencesJson = referencesJson };
        await ComposeAsync(row, Guid.NewGuid(), "", new string('s', 256), ct);
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
            approvals.Select(x => new InvestigationApprovalDto(x.CallId, Options(x.OptionsJson))).ToArray(),
            Prompts: InvestigationContextComposer.Prompts(row), SentContext: await LastContextAsync(id, row.ActiveMessageId, ct));
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
        await ComposeAsync(row, request.MessageId, request.Text, row.RemoteSessionId ?? new string('s', 256), ct);
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
        await StageTurnResultAsync(db, row, Shared.Contracts.Flows.FlowAiResultStatus.Cancelled, clock.GetUtcNow(), ct);
        row.State = InvestigationState.Archived;
        Append(db, row, "archived", "Investigation archived. The remote conversation and its previous actions are preserved.", actor);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<InvestigationClientDto>> ClientsAsync(int tenantId, IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        if (ids is null) return (await SearchClientsAsync(tenantId, null, 0, ct)).Items;
        var tenantName = await TenantNameAsync(tenantId, ct);
        var agents = await db.Agents.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.Id))
            .OrderBy(x => x.Id).Take(20).ToListAsync(ct);
        return await ProjectClientsAsync(agents, tenantName, ct);
    }

    public async Task<InvestigationClientPageDto> SearchClientsAsync(int tenantId, string? search, int offset, CancellationToken ct)
    {
        if (offset < 0 || offset > 1000000 || search?.Length > 200) throw new ArgumentException("Invalid system search or offset.");
        var tenantName = await TenantNameAsync(tenantId, ct);
        IQueryable<Agent> query = db.Agents.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = db.Agents.FromSqlInterpolated($"""
                SELECT * FROM "Agents" WHERE "TenantId" = {tenantId} AND (
                    "FriendlyName" ILIKE {pattern} ESCAPE '\' OR "Name" ILIKE {pattern} ESCAPE '\' OR "Id"::text ILIKE {pattern} ESCAPE '\'
                    OR EXISTS (SELECT 1 FROM unnest("Tags") tag WHERE tag ILIKE {pattern} ESCAPE '\')
                    OR EXISTS (SELECT 1 FROM (
                        SELECT CASE WHEN "DeviceInfoJson" IS JSON OBJECT THEN "DeviceInfoJson"::jsonb ELSE jsonb_build_object() END AS info
                    ) inventory WHERE info->>'hostName' ILIKE {pattern} ESCAPE '\'
                        OR info->>'reportedAddress' ILIKE {pattern} ESCAPE '\'
                        OR info->>'ipAddress' ILIKE {pattern} ESCAPE '\'
                        OR (info->'ipAddresses')::text ILIKE {pattern} ESCAPE '\'
                        OR (info->'addresses')::text ILIKE {pattern} ESCAPE '\'))
                """).AsNoTracking();
        }
        var matches = new List<InvestigationClientDto>();
        for (var scannedPages = 0; scannedPages < 4 && matches.Count < 100; ++scannedPages)
        {
            var page = await query.OrderBy(x => x.Id).Skip(offset).Take(100).ToListAsync(ct);
            if (page.Count == 0) return new(matches, null);
            var projected = await ProjectClientsAsync(page, tenantName, ct);
            foreach (var client in projected)
            {
                ++offset;
                if (InvestigationClientProjection.Matches(client, search)) matches.Add(client);
                if (matches.Count == 100) return new(matches, offset);
            }
            if (page.Count < 100) return new(matches, null);
        }
        return new(matches, offset);
    }

    private async Task<IReadOnlyList<InvestigationClientDto>> ProjectClientsAsync(IReadOnlyList<Agent> agents, string tenantName, CancellationToken ct)
    {
        var ids = agents.Select(x => x.Id).ToArray();
        var tenantId = agents.FirstOrDefault()?.TenantId;
        var owners = await db.Set<ClientConnectionOwnerRecord>().AsNoTracking()
            .Where(x => x.TenantId == tenantId && ids.Contains(x.AgentId)).ToDictionaryAsync(x => x.AgentId, ct);
        var now = clock.GetUtcNow();
        return agents.Select(x => InvestigationClientProjection.Create(x, tenantName, owners.GetValueOrDefault(x.Id), now)).ToArray();
    }

    private async Task<string> TenantNameAsync(int tenantId, CancellationToken ct) =>
        await db.Tenants.AsNoTracking().Where(x => x.Id == tenantId).Select(x => x.Name).SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Tenant not found.");

    public async Task<InvestigationSentContextDto> ComposeAsync(AiInvestigationRecord row, Guid messageId, string question, string sessionId, CancellationToken ct)
    {
        await ValidateClientsAsync(row.TenantId, row.AgentIds, ct);
        var tenantName = await TenantNameAsync(row.TenantId, ct);
        var clients = await ClientsAsync(row.TenantId, row.AgentIds, ct);
        var alertJson = row.OccurrenceId is { } occurrence
            ? await db.MonitoringOccurrences.AsNoTracking().Where(x => x.TenantId == row.TenantId && x.OccurrenceId == occurrence)
                .Select(x => x.OccurrenceJson).SingleOrDefaultAsync(ct) : null;
        if (row.OccurrenceId is not null && alertJson is null) throw new KeyNotFoundException("The alert is unavailable in this tenant.");
        var alert = alertJson is null ? null : JsonSerializer.Deserialize<MonitoringOccurrenceDto>(alertJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return InvestigationContextComposer.Compose(row, tenantName, clients, alert, messageId, question, sessionId, clock.GetUtcNow());
    }

    public async Task RecordContextAsync(Guid id, InvestigationSentContextDto context, CancellationToken ct)
    {
        var record = new AiInvestigationContextRecord { InvestigationId = id, MessageId = context.MessageId,
            Request = context.Request, MetadataJson = context.MetadataJson, OmissionNotesJson = JsonSerializer.Serialize(context.OmissionNotes),
            PayloadBytes = context.PayloadBytes, PreparedAtUtc = context.PreparedAtUtc, SendAttemptedAtUtc = context.SendAttemptedAtUtc };
        db.Add(record); await db.SaveChangesAsync(ct);
    }

    private async Task<InvestigationSentContextDto?> LastContextAsync(Guid id, Guid? activeMessageId, CancellationToken ct)
    {
        var record = await db.Set<AiInvestigationContextRecord>().AsNoTracking().Where(x => x.InvestigationId == id)
            .OrderByDescending(x => x.MessageId == activeMessageId).ThenByDescending(x => x.PreparedAtUtc).FirstOrDefaultAsync(ct);
        return record is null ? null : new(record.MessageId, record.Request, record.MetadataJson, record.PayloadBytes,
            record.PreparedAtUtc, record.SendAttemptedAtUtc, JsonSerializer.Deserialize<string[]>(record.OmissionNotesJson) ?? []);
    }

    public static IReadOnlyList<AiPromptReference> NormalizePrompts(IReadOnlyList<AiPromptReference>? prompts)
    {
        if (prompts?.Count > AiPromptLimits.MaxSelected || prompts?.Any(x => x is null || x.PromptId == Guid.Empty || x.Revision < 1) == true ||
            prompts?.GroupBy(x => x.PromptId).Any(x => x.Select(y => y.Revision).Distinct().Count() != 1) == true)
            throw new ArgumentException("Select up to four optional prompts with valid, unambiguous revisions.");
        return (prompts ?? []).Distinct().ToArray();
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
