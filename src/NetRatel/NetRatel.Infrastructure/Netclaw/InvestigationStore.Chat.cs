using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Netclaw;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed partial class InvestigationStore
{
    // Investigations always have selected clients. An empty persisted selection identifies a general chat.
    public async Task<IReadOnlyList<InvestigationSummaryDto>> ListChatsAsync(int tenantId, int skip, CancellationToken ct)
    {
        if (skip < 0 || skip > 100000) throw new ArgumentException("Invalid history offset.");
        var rows = await db.Set<AiInvestigationRecord>().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.AgentIds.Length == 0)
            .OrderByDescending(x => x.LastActivityUtc).ThenBy(x => x.Id).Skip(skip).Take(100).ToListAsync(ct);
        return rows.Select(Summary).ToArray();
    }

    public async Task<AiInvestigationRecord> CreateChatAsync(int tenantId, GeneralChatCreateRequest request,
        string actor, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200 || request.Title.Any(char.IsControl))
            throw new ArgumentException("Provide a request ID and a title of up to 200 characters.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(822, {tenantId})", ct);
        var existing = await db.Set<AiInvestigationRecord>().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.RequestId == request.RequestId, ct);
        if (existing is not null)
        {
            if (existing.AgentIds.Length != 0 || existing.Title != request.Title.Trim() || existing.CreatedBy != actor)
                throw new InvestigationConflictException("This request ID already belongs to another conversation request.");
            GeneralChatContextComposer.Validate(existing);
            return existing;
        }
        if (await db.Set<AiInvestigationRecord>().CountAsync(x => x.TenantId == tenantId && x.State != InvestigationState.Archived, ct) >= 256)
            throw new InvestigationConflictException("Archive completed conversations before opening more.");
        var row = new AiInvestigationRecord
        {
            TenantId = tenantId, RequestId = request.RequestId, Title = request.Title.Trim(),
            CreatedBy = actor, LastActivityUtc = clock.GetUtcNow()
        };
        await ComposeAsync(row, Guid.NewGuid(), "", new string('s', 256), ct);
        db.Add(row);
        Append(db, row, "created", "Chat opened.", actor);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row;
    }

    public Task ValidateContextAsync(AiInvestigationRecord row, CancellationToken ct)
    {
        if (row.AgentIds.Length != 0) return ValidateClientsAsync(row.TenantId, row.AgentIds, ct);
        GeneralChatContextComposer.Validate(row);
        return Task.CompletedTask;
    }
}
