using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class AgentNonceReplayService
{
    private readonly OrchestratorDbContext _db;
    private readonly TimeProvider _clock;

    public AgentNonceReplayService(OrchestratorDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> TryRegisterAsync(Guid agentId, string nonce, TimeSpan retention, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var cutoff = now.Subtract(retention);

        var expired = await _db.AgentNonceLogs
            .Where(x => x.CreatedAtUtc < cutoff)
            .ToListAsync(ct);
        if (expired.Count > 0)
        {
            _db.AgentNonceLogs.RemoveRange(expired);
        }

        var exists = await _db.AgentNonceLogs.AnyAsync(x => x.AgentId == agentId && x.Nonce == nonce, ct);
        if (exists)
        {
            return false;
        }

        _db.AgentNonceLogs.Add(new AgentNonceLog
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            Nonce = nonce,
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);
        return true;
    }
}
