using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;
using NetRatel.API.Services.Jobs;

namespace NetRatel.API.Services.Orchestration;

/// <summary>Repairs only an already recorded exact run; recovery never invokes another job.</summary>
public static class ManagedOrchestrationRecovery
{
    public static async Task<RequestInfo?> RecoverAsync(OrchestratorDbContext db, IRequestService requests,
        IJobRunService runs, ManagedOrchestrationRequestBinding binding, CancellationToken ct)
    {
        var request = await requests.GetAsync(binding.RequestId, ct);
        if (request is null || request.SourceSystem != binding.SourceSystem || request.TargetTenantId != binding.TenantId ||
            request.TargetAgentId != binding.AgentId || request.JobDefinitionId != binding.JobDefinitionId) return null;
        JobRunInfo? run;
        if (binding.ExecutionId is null)
        {
            var matches = (await runs.ListAsync(ct)).Where(candidate => SameTarget(candidate, binding) && HasLocalRequest(candidate.InputsJson, binding.RequestId)).ToArray();
            if (matches.Length != 1) return null;
            run = matches[0];
            var current = await db.Set<ManagedOrchestrationRequestBinding>().SingleAsync(x => x.RequestId == binding.RequestId, ct);
            var runId = run.Id.ToString(CultureInfo.InvariantCulture);
            if (current.ExecutionId is not null && current.ExecutionId != runId) return null;
            current.ExecutionId = runId;
            binding.ExecutionId = runId;
            await db.SaveChangesAsync(ct);
        }
        else
        {
            if (!ulong.TryParse(binding.ExecutionId, NumberStyles.None, CultureInfo.InvariantCulture, out var runId)) return null;
            run = await runs.GetAsync(runId, ct);
        }
        if (run is null || !SameTarget(run, binding) || !HasLocalRequest(run.InputsJson, binding.RequestId) ||
            request.ExecutionId is not null && request.ExecutionId != binding.ExecutionId) return null;
        if (request.ExecutionId is null)
            request = await requests.UpdateAsync(new UpdateRequestCommand(request.Id, null, null, null, binding.ExecutionId,
                OrchestrationCallbackProjection.RequestStatus(run.Status), run.Error, null, null, null), ct);
        return request;
    }

    private static bool SameTarget(JobRunInfo run, ManagedOrchestrationRequestBinding binding) =>
        run.StartedBy == binding.SourceSystem && run.TenantId == binding.TenantId && run.AgentId == binding.AgentId &&
        run.JobId.ToString(CultureInfo.InvariantCulture) == binding.JobDefinitionId;

    private static bool HasLocalRequest(string? inputs, int requestId)
    {
        if (inputs is null) return false;
        try
        {
            using var json = JsonDocument.Parse(inputs);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("meta", out var meta) &&
                meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("netratelRequestId", out var value) &&
                value.ValueKind == JsonValueKind.String && value.GetString() == requestId.ToString(CultureInfo.InvariantCulture);
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>
/// Recovers durable run intent without replaying physical start. The original
/// conservative dispatch reconciliation boundary never moves on retry.
/// </summary>
public sealed class ManagedJobRunRecoveryWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<ManagedJobRunRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var afterRunId = 0L;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var authority = scope.ServiceProvider.GetRequiredService<IAkkaJobAuthorityService>();
                var ids = await db.Set<JobRunControlRecord>().AsNoTracking()
                    .Where(x => x.RunId > afterRunId && x.TerminalReadyAtUtc == null)
                    .OrderBy(x => x.RunId).Take(16).Select(x => x.RunId).ToArrayAsync(stoppingToken);
                foreach (var id in ids) await authority.RecoverAsync(checked((ulong)id), stoppingToken);
                afterRunId = ids.Length == 0 ? 0 : ids[^1];
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Run intent recovery failed: {FailureType}.", error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
