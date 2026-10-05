using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services.Orchestration;

public static class OrchestrationCallbackProjection
{
    public static string ResultHash(JobRunDetails details) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            details.Run,
            Steps = details.Steps.OrderBy(x => x.Id),
            Activities = details.Activities.OrderBy(x => x.Id)
        })));

    public static async Task<bool> TerminalReadyAsync(OrchestratorDbContext db, JobRunDetails details, CancellationToken ct)
    {
        if (details.Run.Status is JobRunState.Pending or JobRunState.Running) return true;
        var id = checked((long)details.Run.Id);
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
            // The accepted terminal run body is immutable. This one-statement
            // guard prevents a stale Running reader from regressing its request.
            return await db.Requests.Where(x => x.Id == requestId && (terminal ||
                    x.Status != "Completed" && x.Status != "Failed" && x.Status != "Cancelled" && x.Status != "TimedOut"))
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, status)
                    .SetProperty(x => x.ResultMessage, message).SetProperty(x => x.ResultData, resultJson)
                    .SetProperty(x => x.UpdatedAtUtc, now), ct) == 1;
        }
        var current = await requests.GetAsync(requestId, ct);
        if (current is null || !terminal && current.Status is "Completed" or "Failed" or "Cancelled" or "TimedOut") return false;
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
        run.AgentId == binding.AgentId && run.StartedBy == binding.SourceSystem;

    public static string Status(JobRunState state) => state switch
    {
        JobRunState.Pending => "pending",
        JobRunState.Running => "running",
        JobRunState.Succeeded => "succeeded",
        JobRunState.Cancelled => "failed",
        JobRunState.TimedOut => "failed",
        _ => "failed"
    };

    public static string RequestStatus(JobRunState state) => state switch
    {
        JobRunState.Pending => "Accepted", JobRunState.Running => "Processing", JobRunState.Succeeded => "Completed",
        JobRunState.Cancelled => "Cancelled", JobRunState.TimedOut => "TimedOut", _ => "Failed"
    };

    public static NetRatelExternalServiceCallbackRequest Build(RequestInfo request, JobRunDetails details, ManagedOrchestrationRequestBinding binding)
    {
        var run = details.Run;
        var results = details.Activities.Where(activity => activity.ResultJson is not null)
            .OrderBy(activity => activity.Id).Select(activity => new { activity.RequestId, activity.Status, activity.ResultJson }).ToArray();
        return new()
        {
            RequestId = request.Id.ToString(CultureInfo.InvariantCulture), RequestTaskId = binding.RequestTaskId,
            NetRatelRequestId = request.Id.ToString(CultureInfo.InvariantCulture), NetRatelRunId = binding.ExecutionId,
            ExecutionId = binding.ExecutionId!, Status = Status(run.Status), Message = run.Error ?? (run.Status is JobRunState.Cancelled or JobRunState.TimedOut ? $"Job execution {run.Status}." : null),
            ResultJson = results.Length == 0 ? null : JsonSerializer.Serialize(new { activities = results }),
            ErrorJson = run.Status is JobRunState.Failed or JobRunState.TimedOut or JobRunState.Cancelled ? JsonSerializer.Serialize(new { state = run.Status.ToString(), message = run.Error ?? $"Job execution {run.Status}." }) : null,
            StartedAtUtc = run.StartedAtUtc, CompletedAtUtc = run.CompletedAtUtc
        };
    }
}
