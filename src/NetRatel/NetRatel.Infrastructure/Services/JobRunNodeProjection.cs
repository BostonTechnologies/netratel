using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Infrastructure.Services;

/// <summary>One committed node projection shared by aggregate receipts and authorized run views.</summary>
public static class JobRunNodeProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static async Task<IReadOnlyList<JobRunNodeDto>> ReadAsync(OrchestratorDbContext db, long parentRunId,
        CancellationToken ct, int? limit = null)
    {
        var query = db.Set<JobRunNodeRecord>().AsNoTracking().Where(row => row.ParentRunId == parentRunId)
            .OrderBy(row => row.Ordinal).ThenBy(row => row.AgentId);
        var rows = await (limit is { } maximum ? query.Take(maximum) : query).Select(row => new
        {
            Node = row,
            CurrentStep = db.JobRuns.Where(run => run.Id == row.NativeRunId)
                .Select(run => (int?)run.CurrentStepOrdinal).FirstOrDefault() ?? 0,
            NativeHash = db.Set<JobRunControlRecord>().Where(control => control.RunId == row.NativeRunId &&
                control.TerminalReadyAtUtc != null).Select(control => control.TerminalResultHash).FirstOrDefault()
        }).ToArrayAsync(ct);
        return rows.Select(row => ToDto(row.Node, row.CurrentStep) with { NativeTerminalResultHash = row.NativeHash }).ToArray();
    }

    public static JobRunNodeDto ToDto(JobRunNodeRecord row, int currentStepOrdinal = 0)
    {
        var target = JsonSerializer.Deserialize<JobTargetNode>(row.TargetJson, Json)
            ?? throw new InvalidOperationException("The frozen node identity is unavailable.");
        if (target.AgentId != row.AgentId)
            throw new InvalidOperationException("The frozen node identity differs from its durable binding.");
        return new(checked((ulong)row.ParentRunId), row.AgentId, row.Ordinal,
            row.NativeRunId is { } native ? checked((ulong)native) : null, row.State,
            target.Hostname, target.DisplayName, row.Reason, Micros(row.StartedAtUtc), Micros(row.CompletedAtUtc))
        { Tags = target.Tags, Environment = target.Environment, CurrentStepOrdinal = currentStepOrdinal };
    }

    private static long? Micros(DateTimeOffset? value) => value is { } timestamp
        ? (timestamp.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10 : null;
}
