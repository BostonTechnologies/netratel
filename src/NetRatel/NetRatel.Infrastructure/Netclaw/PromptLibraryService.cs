using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Netclaw;

public sealed class PromptLibraryService(OrchestratorDbContext db, TimeProvider clock) : IPromptLibraryService
{
    public async Task<AiPromptListDto> ListAsync(int tenantId, string? search = null, bool includeArchived = false, CancellationToken ct = default)
    {
        if (tenantId <= 0) throw new ArgumentException("Select a tenant.");
        search = search?.Trim(); if (search?.Length > 128) throw new ArgumentException("Prompt search is limited to 128 characters.");
        var defaultState = await ResolveDefaultStateAsync(tenantId, ct);
        var defaultId = defaultState.Prompt?.PromptId;
        var query = db.AiPrompts.AsNoTracking().Where(x => x.TenantId == tenantId && (includeArchived || !x.Archived));
        if (!string.IsNullOrEmpty(search))
        {
            var term = search.ToLowerInvariant();
            query = query.Where(x => x.Name.ToLower().Contains(term) || x.Description != null && x.Description.ToLower().Contains(term));
        }
        // Lists deliberately omit bodies; only guidance actually selected enters a chat request.
        var rows = await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Take(AiPromptLimits.MaxListItems + 1)
            .Select(x => new AiPromptSummaryDto(x.Id, x.Name, x.Description, x.Revision, x.Archived,
                x.Id == defaultId, x.CreatedAtUtc, x.UpdatedAtUtc, x.CreatedBy, x.UpdatedBy)).ToListAsync(ct);
        return new(rows.Take(AiPromptLimits.MaxListItems).ToArray(), defaultState, rows.Count > AiPromptLimits.MaxListItems);
    }

    public async Task<AiPromptDetailDto> GetAsync(int tenantId, Guid id, long? revision = null, CancellationToken ct = default)
    {
        var row = await db.AiPrompts.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct)
            ?? throw new KeyNotFoundException();
        var currentDefault = await DefaultAsync(tenantId, ct);
        var summary = Summary(row, currentDefault.PromptId == id);
        if (revision is null || revision == row.Revision) return new(summary, row.Body);
        var history = await RevisionAsync(tenantId, new(id, revision.Value), ct);
        return new(summary with { Revision = history.Revision, Name = history.Name, Description = history.Description,
            UpdatedAtUtc = history.CreatedAtUtc, UpdatedBy = history.CreatedBy }, history.Body);
    }

    public async Task<AiPromptDetailDto> CreateAsync(int tenantId, AiPromptSaveRequest request, string actor, CancellationToken ct = default)
    {
        Validate(request); if (request.ExpectedRevision != 0) throw new AiPromptConflictException("A new prompt must have revision zero.");
        await using var transaction = await LockTenantAsync(tenantId, ct);
        if (!await db.Tenants.AnyAsync(x => x.Id == tenantId, ct)) throw new KeyNotFoundException();
        var now = clock.GetUtcNow();
        var row = new AiPromptRecord { TenantId = tenantId, Name = request.Name.Trim(), Description = NormalizeDescription(request.Description),
            Body = request.Body, CreatedAtUtc = now, UpdatedAtUtc = now, CreatedBy = actor, UpdatedBy = actor };
        db.AiPrompts.Add(row); db.AiPromptRevisions.Add(Revision(row)); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(Summary(row, false), row.Body);
    }

    public async Task<AiPromptDetailDto> UpdateAsync(int tenantId, Guid id, AiPromptSaveRequest request, string actor, CancellationToken ct = default)
    {
        Validate(request); await using var transaction = await LockTenantAsync(tenantId, ct);
        var row = await ActiveAsync(tenantId, id, ct); RequireRevision(row.Revision, request.ExpectedRevision);
        row.Name = request.Name.Trim(); row.Description = NormalizeDescription(request.Description); row.Body = request.Body;
        row.Revision = checked(row.Revision + 1); row.UpdatedAtUtc = clock.GetUtcNow(); row.UpdatedBy = actor;
        db.AiPromptRevisions.Add(Revision(row)); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(Summary(row, (await DefaultAsync(tenantId, ct)).PromptId == id), row.Body);
    }

    public async Task<AiPromptDefaultDto> SetDefaultAsync(int tenantId, AiPromptSetDefaultRequest request, CancellationToken ct = default)
    {
        await using var transaction = await LockTenantAsync(tenantId, ct);
        if (!await db.Tenants.AnyAsync(x => x.Id == tenantId, ct)) throw new KeyNotFoundException();
        if (request.PromptId is { } id)
        {
            var prompt = await ActiveAsync(tenantId, id, ct);
            if (request.ExpectedPromptRevision is not { } expected) throw new ArgumentException("Select the prompt revision before setting the default.");
            RequireRevision(prompt.Revision, expected);
        }
        var current = await db.AiPromptDefaults.SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        RequireRevision(current?.Revision ?? 0, request.ExpectedRevision);
        if (current is null) { current = new() { TenantId = tenantId }; db.AiPromptDefaults.Add(current); }
        current.PromptId = request.PromptId; current.Revision = checked(current.Revision + 1);
        await db.SaveChangesAsync(ct);
        var result = await ResolveDefaultStateAsync(tenantId, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<AiPromptDetailDto> ArchiveAsync(int tenantId, Guid id, AiPromptArchiveRequest request, string actor, CancellationToken ct = default)
    {
        await using var transaction = await LockTenantAsync(tenantId, ct);
        var row = await ActiveAsync(tenantId, id, ct); RequireRevision(row.Revision, request.ExpectedRevision);
        var current = await db.AiPromptDefaults.SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        RequireRevision(current?.Revision ?? 0, request.ExpectedDefaultRevision);
        if (current?.PromptId == id)
        {
            if (!request.RemoveDefault) throw new AiPromptConflictException("Choose a replacement default or explicitly remove the default before archiving this prompt.");
            current.PromptId = null; current.Revision = checked(current.Revision + 1);
        }
        row.Archived = true; row.Revision = checked(row.Revision + 1); row.UpdatedAtUtc = clock.GetUtcNow(); row.UpdatedBy = actor;
        // Archive itself is a revision: all previously selected/published revisions remain immutable.
        db.AiPromptRevisions.Add(Revision(row)); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return new(Summary(row, false), row.Body);
    }

    public async Task<AiPromptSnapshot?> ResolveDefaultAsync(int tenantId, CancellationToken ct = default) =>
        (await ResolveDefaultStateAsync(tenantId, ct)).Prompt;

    private async Task<AiPromptDefaultDto> ResolveDefaultStateAsync(int tenantId, CancellationToken ct)
    {
        // Join reads the current default and current revision together in one statement.
        // Editing affects the next session; stored investigation snapshots are never refreshed here.
        return await (from state in db.AiPromptDefaults.AsNoTracking()
                      join prompt in db.AiPrompts.AsNoTracking() on new { state.TenantId, Id = state.PromptId } equals new { prompt.TenantId, Id = (Guid?)prompt.Id } into prompts
                      from prompt in prompts.DefaultIfEmpty()
                      where state.TenantId == tenantId
                      select new AiPromptDefaultDto(state.Revision, prompt == null || prompt.Archived ? null :
                          new AiPromptSnapshot(prompt.Id, prompt.Revision, prompt.Name, prompt.Description, prompt.Body, true))).SingleOrDefaultAsync(ct)
            ?? new(0, null);
    }

    public async Task<AiPromptSnapshot> ResolveAsync(int tenantId, AiPromptReference reference, bool allowArchived = false, CancellationToken ct = default)
    {
        if (reference.PromptId == Guid.Empty || reference.Revision <= 0) throw new ArgumentException("Select an existing prompt revision.");
        return await (from prompt in db.AiPrompts.AsNoTracking()
                      join history in db.AiPromptRevisions.AsNoTracking() on new { prompt.TenantId, PromptId = prompt.Id } equals new { history.TenantId, history.PromptId }
                      where prompt.TenantId == tenantId && prompt.Id == reference.PromptId && history.Revision == reference.Revision && (allowArchived || !prompt.Archived)
                      select new AiPromptSnapshot(history.PromptId, history.Revision, history.Name, history.Description, history.Body, false)).SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException();
    }

    private async Task<AiPromptRevisionRecord> RevisionAsync(int tenantId, AiPromptReference reference, CancellationToken ct) =>
        await db.AiPromptRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.PromptId == reference.PromptId && x.Revision == reference.Revision, ct)
            ?? throw new KeyNotFoundException();
    private async Task<AiPromptRecord> ActiveAsync(int tenantId, Guid id, CancellationToken ct) =>
        await db.AiPrompts.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id && !x.Archived, ct) ?? throw new KeyNotFoundException();
    private async Task<AiPromptDefaultRecord> DefaultAsync(int tenantId, CancellationToken ct) =>
        await db.AiPromptDefaults.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct) ?? new() { TenantId = tenantId };
    private async Task<IDbContextTransaction> LockTenantAsync(int tenantId, CancellationToken ct)
    {
        if (tenantId <= 0) throw new ArgumentException("Select a tenant.");
        var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize prompt/default mutations together; a default cannot race an archive.
        // Supplied revision tokens still reject stale operator edits after acquiring the lock.
        try { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({tenantId}, {223})", ct); return transaction; }
        catch { await transaction.DisposeAsync(); throw; }
    }
    private static AiPromptRevisionRecord Revision(AiPromptRecord row) => new() { TenantId = row.TenantId, PromptId = row.Id, Revision = row.Revision,
        Name = row.Name, Description = row.Description, Body = row.Body, CreatedAtUtc = row.UpdatedAtUtc, CreatedBy = row.UpdatedBy };
    private static AiPromptSummaryDto Summary(AiPromptRecord row, bool isDefault) => new(row.Id, row.Name, row.Description, row.Revision, row.Archived, isDefault,
        row.CreatedAtUtc, row.UpdatedAtUtc, row.CreatedBy, row.UpdatedBy);
    private static string? NormalizeDescription(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void RequireRevision(long current, long expected)
    { if (current != expected) throw new AiPromptConflictException("The prompt or default changed. Refresh before saving your changes."); }
    private static void Validate(AiPromptSaveRequest request)
    {
        if (!FlowGraphValidator.IsBoundedText(request.Name, AiPromptLimits.MaxNameLength))
            throw new ArgumentException($"Enter a prompt name of at most {AiPromptLimits.MaxNameLength} characters.");
        if (request.Description is not null && !FlowGraphValidator.IsBoundedText(request.Description, AiPromptLimits.MaxDescriptionLength, allowEmpty: true, multiline: true)) throw new ArgumentException($"Description is limited to {AiPromptLimits.MaxDescriptionLength} characters.");
        if (!FlowGraphValidator.IsBoundedText(request.Body, AiPromptLimits.MaxBodyUtf8Bytes, multiline: true))
            throw new ArgumentException("Enter bounded prompt guidance containing valid text, tabs and line breaks.");
        if (Encoding.UTF8.GetByteCount(request.Body) > AiPromptLimits.MaxBodyUtf8Bytes)
            throw new ArgumentException($"Prompt guidance is limited to {AiPromptLimits.MaxBodyUtf8Bytes} UTF-8 bytes. Shorten it before saving.");
    }
}
