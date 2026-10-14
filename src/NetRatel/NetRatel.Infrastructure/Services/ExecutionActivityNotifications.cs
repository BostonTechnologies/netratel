using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>Disposable invalidations. PostgreSQL delivers notifications only after commit;
/// authorized database snapshots remain the execution and output authority.</summary>
public static class ExecutionActivityNotifications
{
    public const string ChannelName = "netratel_execution_activity";

    public static Task NotifyAsync(OrchestratorDbContext db, int? tenantId,
        ulong? runId, string? requestId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql()) return Task.CompletedTask;
        var payload = JsonSerializer.Serialize(new { Id = Guid.NewGuid(), TenantId = tenantId,
            RunId = runId, RequestId = requestId });
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_notify('netratel_execution_activity', {payload})", cancellationToken);
    }
}
