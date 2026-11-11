using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Flows;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using System.Text.Json.Nodes;
using System.Text;

namespace NetRatel.API.Services.Orchestration;

public static class OrchestrationCallbackProjection
{
    public static async Task<NetRatelExternalServiceCallbackRequest> BuildAsync(OrchestratorDbContext db,
        IJobRunService runs, RequestInfo request, JobRunDetails details, ManagedOrchestrationRequestBinding binding, CancellationToken ct)
    {
        if (!details.Run.IsAggregate) return Build(request, details, binding);
        var nodes = await JobRunNodeProjection.ReadAsync(db, checked((long)details.Run.Id), ct);
        var callback = Build(request, details, binding, nodes);
        var results = new JsonArray();
        var remainingBytes = 64 * 1024;
        foreach (var node in nodes.Take(25))
        {
            if (node.NativeRunId is not { } id || remainingBytes <= 0) continue;
            var child = await runs.GetDetailsAsync(id, ct);
            if (child is null) continue;
            var activities = new JsonArray();
            foreach (var activity in child.Activities.Where(activity => activity.ResultJson is not null).OrderBy(activity => activity.Id).Take(4))
            {
                var value = activity.ResultJson!;
                var bounded = value[..Math.Min(value.Length, Math.Min(2048, remainingBytes / 4))];
                remainingBytes -= Encoding.UTF8.GetByteCount(bounded);
                activities.Add(new JsonObject { ["requestId"] = activity.RequestId, ["status"] = activity.Status,
                    ["resultJson"] = bounded, ["truncated"] = bounded.Length < value.Length });
                if (remainingBytes < 4) break;
            }
            results.Add(new JsonObject { ["nativeRunId"] = id.ToString(CultureInfo.InvariantCulture), ["activities"] = activities });
        }
        var root = JsonNode.Parse(callback.ResultJson!)!.AsObject();
        root["childResults"] = results;
        root["childResultsTruncated"] = nodes.Count > 25 || remainingBytes < 4;
        callback.ResultJson = root.ToJsonString();
        return callback;
    }

    public static string ResultHash(JobRunDetails details) => JobRunProjectionHash.Compute(details);

    public static async Task<bool> TerminalReadyAsync(OrchestratorDbContext db, JobRunDetails details, CancellationToken ct)
    {
        if (details.Run.Status is JobRunState.Pending or JobRunState.Running) return true;
        var id = checked((long)details.Run.Id);
        if (details.Run.IsAggregate)
        {
            var aggregateHash = JobRunProjectionHash.ComputeAggregate(details, await JobRunNodeProjection.ReadAsync(db, id, ct));
            return await db.Set<JobAggregateControlRecord>().AsNoTracking().AnyAsync(row => row.ParentRunId == id &&
                row.TerminalReadyAtUtc != null && row.TerminalResultHash == aggregateHash, ct);
        }
        var hash = ResultHash(details);
        return await db.Set<JobRunControlRecord>().AsNoTracking().AnyAsync(x => x.RunId == id &&
            x.TerminalReadyAtUtc != null && x.TerminalResultHash == hash, ct);
    }

    public static async Task<bool> ProjectRequestStatusAsync(OrchestratorDbContext db, IRequestService requests, int requestId,
        JobRunState state, string? message, string? resultJson, DateTimeOffset now, CancellationToken ct)
    {
        var status = RequestStatus(state);
        var terminal = state is not (JobRunState.Pending or JobRunState.Running);
        if (db.Database.IsRelational())
        {
            // ExecuteUpdate bypasses SaveChanges interception. Lock and capture the original state,
            // then commit its immutable lifecycle fact with the exact projection write.
            await using var owned = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            var query = db.Database.IsNpgsql()
                ? db.Requests.FromSqlInterpolated($"SELECT * FROM \"Requests\" WHERE \"Id\" = {requestId} FOR UPDATE")
                : db.Requests.Where(x => x.Id == requestId);
            var row = await query.AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is null || !terminal && FlowExecutionOutcomes.FromAuthoritativeStatus(row.Status) is not null) return false;
            var previous = row.Status;
            var changed = await db.Requests.Where(x => x.Id == requestId && x.Status == previous)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, status)
                    .SetProperty(x => x.ResultMessage, message).SetProperty(x => x.ResultData, resultJson)
                    .SetProperty(x => x.UpdatedAtUtc, now), ct);
            if (changed != 1) return false;
            row.Status = status;
            row.ResultMessage = message;
            row.ResultData = resultJson;
            row.UpdatedAtUtc = now;
            await ExecutionLifecycleOutbox.RecordRequestAsync(db, row, previous, DateTimeOffset.UtcNow, ct);
            await db.SaveChangesAsync(ct);
            if (owned is not null) await owned.CommitAsync(ct);
            return true;
        }
        var current = await requests.GetAsync(requestId, ct);
        if (current is null || !terminal && FlowExecutionOutcomes.FromAuthoritativeStatus(current.Status) is not null) return false;
        return await requests.UpdateAsync(new(requestId, null, null, null, null, status, message, resultJson, null, null), ct) is not null;
    }

    public static object ManagedWire(NetRatelExternalServiceCallbackRequest request) => new
    {
        request.RequestTaskId, request.RequestId, request.ExecutionId,
        OrchestrationRequestId = request.NetRatelRequestId, OrchestrationRunId = request.NetRatelRunId,
        request.Status, request.Message, request.ResultJson, request.ErrorJson, request.WorklogSummary,
        request.StartedAtUtc, request.CompletedAtUtc
    };

    public static bool Matches(RequestInfo request, JobRunInfo run, ManagedOrchestrationRequestBinding binding) =>
        request.Id == binding.RequestId && request.SourceSystem == binding.SourceSystem &&
        request.TargetTenantId == binding.TenantId && request.TargetAgentId == binding.AgentId &&
        request.JobDefinitionId == binding.JobDefinitionId && request.ExecutionId == binding.ExecutionId &&
        run.Id.ToString(CultureInfo.InvariantCulture) == binding.ExecutionId &&
        run.JobId.ToString(CultureInfo.InvariantCulture) == binding.JobDefinitionId && run.TenantId == binding.TenantId &&
        run.AgentId == binding.AgentId && run.StartedBy == binding.SourceSystem && run.ParentRunId is null &&
        (binding.TargetPlanDigest is null ? !run.IsAggregate : run.IsAggregate && run.TargetPlanDigest == binding.TargetPlanDigest);

    public static string Status(JobRunState state) => state switch
    {
        JobRunState.Pending => "pending",
        JobRunState.Running => "running",
        JobRunState.Succeeded => "succeeded",
        JobRunState.Cancelled => "failed",
        JobRunState.TimedOut => "failed",
        JobRunState.Unknown => "unknown",
        _ => "failed"
    };

    public static string RequestStatus(JobRunState state) => state switch
    {
        JobRunState.Pending => "Accepted", JobRunState.Running => "Processing", JobRunState.Succeeded => "Completed",
        JobRunState.Cancelled => "Cancelled", JobRunState.TimedOut => "TimedOut", JobRunState.Unknown => "Unknown", _ => "Failed"
    };

    public static NetRatelExternalServiceCallbackRequest Build(RequestInfo request, JobRunDetails details, ManagedOrchestrationRequestBinding binding, IReadOnlyList<JobRunNodeDto>? nodes = null)
    {
        var run = details.Run;
        var results = details.Activities.Where(activity => activity.ResultJson is not null)
            .OrderBy(activity => activity.Id).Select(activity => new { activity.RequestId, activity.Status, activity.ResultJson }).ToArray();
        return new()
        {
            RequestId = request.Id.ToString(CultureInfo.InvariantCulture), RequestTaskId = binding.RequestTaskId,
            NetRatelRequestId = request.Id.ToString(CultureInfo.InvariantCulture), NetRatelRunId = binding.ExecutionId,
            ExecutionId = binding.ExecutionId!, Status = Status(run.Status), Message = run.Error ?? (run.Status is JobRunState.Cancelled or JobRunState.TimedOut ? $"Job execution {run.Status}." : null),
            ResultJson = nodes is not null ? JsonSerializer.Serialize(new { parentRunId = binding.ExecutionId,
                nodeCounts = run.NodeCounts, nodes = nodes.Take(100).Select(node => new { agentId = node.AgentId.ToString("D"),
                    nativeRunId = node.NativeRunId?.ToString(CultureInfo.InvariantCulture), node.State, node.Reason }),
                truncated = nodes.Count > 100 }) : results.Length == 0 ? null : JsonSerializer.Serialize(new { activities = results }),
            ErrorJson = run.Status is JobRunState.Failed or JobRunState.TimedOut or JobRunState.Cancelled or JobRunState.Unknown ? JsonSerializer.Serialize(new { state = run.Status.ToString(), message = run.Error ?? $"Job execution {run.Status}." }) : null,
            StartedAtUtc = run.StartedAtUtc, CompletedAtUtc = run.CompletedAtUtc
        };
    }
}
