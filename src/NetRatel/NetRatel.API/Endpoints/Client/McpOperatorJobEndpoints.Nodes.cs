using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Gateway;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

public static partial class McpOperatorJobEndpoints
{
    private static async Task<string?> LegacyGatewayFailureAsync(McpOperatorJobContext context, HttpContext http, CancellationToken ct)
    {
        var client = new ClientKey(context.TenantId, context.AgentId);
        var presence = await http.RequestServices.GetRequiredService<IClientPresenceRouter>().GetSnapshotAsync(client, ct).ConfigureAwait(false);
        if (presence.Status != ClientPresenceStatus.Online) return "target_offline";
        if (!http.RequestServices.GetRequiredService<IAgentCommandAuthorityDispatcher>().IsAvailable(client)) return "capability_unavailable";
        return null;
    }

    private static async Task<IResult> RunNodesAsync(int tenantId, Guid agentId, ulong runId, int? page, int? pageSize,
        HttpContext http, IHostEnvironment environment, [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission, McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, [FromServices] OrchestratorDbContext db, CancellationToken ct)
    {
        var result = await TryContextAsync(RunsTool, "nodes", tenantId, agentId, http, environment, presence, admission,
            localAgents, dispatcher, false, ct).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        var index = page ?? 1; var size = pageSize ?? 50;
        if (index < 1 || index > 100000 || size is < 1 or > 100) return Failure("job_node_page_invalid", context);
        try
        {
            var run = await GetOwnedRunAsync(runId, jobs, context, ct).ConfigureAwait(false);
            if (run is null) return Failure("job_run_not_found", context);
            if (!run.IsAggregate) return Results.Ok(new { items = Array.Empty<object>(), totalCount = 0,
                legacyRunId = runId.ToString(CultureInfo.InvariantCulture) });
            await RequireParentTargetsAsync(runId, context, admission, db, ct).ConfigureAwait(false);
            var query = db.JobRunNodes.AsNoTracking().Where(node => node.ParentRunId == checked((long)runId) && node.TenantId == tenantId);
            var total = await query.CountAsync(ct).ConfigureAwait(false);
            var nodes = await query.OrderBy(node => node.Ordinal).Skip((index - 1) * size).Take(size).ToArrayAsync(ct).ConfigureAwait(false);
            await admission.RecordAcceptedAsync(context.Request, ct).ConfigureAwait(false);
            return Results.Ok(new { items = nodes.Select(NodeView), totalCount = total, page = index, pageSize = size,
                nextPage = (long)index * size < total ? index + 1 : (int?)null });
        }
        catch (McpOperatorAdmissionRejectedException error) { return Failure(error.FailureCode, context); }
    }

    private static Task<IResult> NodeStepsAsync(int tenantId, Guid agentId, ulong runId, Guid nodeAgentId,
        HttpContext http, IHostEnvironment environment, [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission, McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, IJobRunService runs,
        [FromServices] OrchestratorDbContext db, CancellationToken ct) =>
        NodeOutputAsync("node_steps", tenantId, agentId, runId, nodeAgentId, null, http, environment, presence,
            admission, localAgents, dispatcher, jobs, runs, db, ct);

    private static Task<IResult> NodeLogsAsync(int tenantId, Guid agentId, ulong runId, Guid nodeAgentId, ulong stepRunId, string? cursor, int? limit, int? maximumBytes,
        HttpContext http, IHostEnvironment environment, [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission, McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, IJobRunService runs,
        [FromServices] OrchestratorDbContext db, CancellationToken ct) =>
        NodeOutputAsync("node_logs", tenantId, agentId, runId, nodeAgentId, stepRunId, http, environment, presence,
            admission, localAgents, dispatcher, jobs, runs, db, ct, cursor, limit, maximumBytes);

    private static async Task<IResult> NodeOutputAsync(string operation, int tenantId, Guid agentId, ulong runId,
        Guid nodeAgentId, ulong? stepRunId, HttpContext http, IHostEnvironment environment, IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission, McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, IJobRunService runs,
        OrchestratorDbContext db, CancellationToken ct, string? cursor = null, int? limit = null, int? maximumBytes = null)
    {
        var result = await TryContextAsync(RunsTool, operation, tenantId, agentId, http, environment, presence,
            admission, localAgents, dispatcher, false, ct).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        try
        {
            if (await GetOwnedRunAsync(runId, jobs, context, ct).ConfigureAwait(false) is not { IsAggregate: true })
                return Failure("job_run_not_found", context);
            var node = await db.JobRunNodes.AsNoTracking().SingleOrDefaultAsync(row => row.ParentRunId == checked((long)runId) &&
                row.TenantId == tenantId && row.AgentId == nodeAgentId, ct).ConfigureAwait(false);
            if (node is null) return Failure("job_node_not_found", context);
            var decision = (await admission.EvaluateAsync(context.Request with { AgentId = nodeAgentId }, ct).ConfigureAwait(false)).Decision;
            if (!decision.IsAllowed) return Failure(decision.FailureCode ?? "target_policy_denied", context);
            if (node.NativeRunId is not { } native)
                return Results.Ok(new { node = NodeView(node), steps = Array.Empty<object>() });
            var details = await runs.GetDetailsAsync(checked((ulong)native), ct).ConfigureAwait(false);
            if (details is null || details.Run.ParentRunId != runId || details.Run.AgentId != nodeAgentId || details.Run.TenantId != tenantId)
                return Failure("job_node_not_found", context);
            await admission.RecordAcceptedAsync(context.Request with { AgentId = nodeAgentId }, ct).ConfigureAwait(false);
            if (stepRunId is null)
                return Results.Ok(new { node = NodeView(node), steps = details.Steps.OrderBy(step => step.Ordinal)
                    .Select(step => new { stepRunId = step.Id.ToString(CultureInfo.InvariantCulture), nativeRunId = native.ToString(CultureInfo.InvariantCulture),
                        nodeAgentId, parentRunId = runId.ToString(CultureInfo.InvariantCulture), step.Ordinal,
                        state = step.Status.ToString(), step.Error, step.TaskRequestId, step.StartedAtUtc, step.CompletedAtUtc }) });
            var selected = details.Steps.SingleOrDefault(step => step.Id == stepRunId);
            if (selected is null) return Failure("job_step_run_not_found", context);
            var activity = details.Activities.SingleOrDefault(item => item.RequestId == selected.TaskRequestId && item.TenantId == tenantId && item.AgentId == nodeAgentId);
            var requestedLimit = limit ?? 500;
            var requestedBytes = maximumBytes ?? 65536;
            JobLogPageBuilder.ValidateBounds(requestedLimit, requestedBytes);
            _ = JobLogPageBuilder.ParseCursor(cursor);
            var budget = Math.Min(requestedBytes, decision.EffectiveConstraints?.MaxOutputBytes ?? 65536);
            if (budget < 1) return Failure("job_node_output_unavailable", context);
            var output = activity is null ? null : await runs.GetForwardLogsPageAsync(activity.RequestId, cursor,
                requestedLimit, budget, ct).ConfigureAwait(false);
            return Results.Ok(new { parentRunId = runId.ToString(CultureInfo.InvariantCulture), nodeAgentId,
                nativeRunId = native.ToString(CultureInfo.InvariantCulture), stepRunId = stepRunId.Value.ToString(CultureInfo.InvariantCulture),
                output = output is null ? null : new { output.RequestId, output.Plain, output.Html, output.ExitCode,
                    logs = output.Logs.Select(log => new { id = log.Id.ToString(CultureInfo.InvariantCulture), log.RequestId, log.ClientIdentity,
                        log.Ts, log.Stream, log.Message, seq = log.Seq.ToString(CultureInfo.InvariantCulture) }), output.Truncated, output.NextCursor, output.HasMore,
                    truncatedLogIds = output.TruncatedLogIds?.Select(id => id.ToString(CultureInfo.InvariantCulture)), output.FullOutputUrl } });
        }
        catch (McpOperatorAdmissionRejectedException error) { return Failure(error.FailureCode, context); }
        catch (JobTargetValidationException error) { return Failure(error.Code, context); }
    }

    private static object NodeView(JobRunNodeRecord node)
    {
        var target = System.Text.Json.JsonSerializer.Deserialize<JobTargetNode>(node.TargetJson);
        return new { parentRunId = node.ParentRunId.ToString(CultureInfo.InvariantCulture), node.AgentId, node.Ordinal,
            nativeRunId = node.NativeRunId?.ToString(CultureInfo.InvariantCulture), state = node.State.ToString(),
            target?.Hostname, target?.DisplayName, node.Reason, node.StartedAtUtc, node.CompletedAtUtc };
    }

    private static async Task RequireParentTargetsAsync(ulong runId, McpOperatorJobContext context,
        IMcpOperatorRouteAdmission admission, OrchestratorDbContext db, CancellationToken ct)
    {
        var ids = await db.JobRunNodes.AsNoTracking().Where(row => row.ParentRunId == checked((long)runId) && row.TenantId == context.TenantId)
            .Select(row => row.AgentId).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var target in ids)
        {
            var decision = (await admission.EvaluateAsync(context.Request with { AgentId = target }, ct).ConfigureAwait(false)).Decision;
            if (!decision.IsAllowed) throw new McpOperatorAdmissionRejectedException(decision.FailureCode ?? "target_policy_denied");
        }
    }

    private static async Task<IResult?> ReplayAggregateStartAsync(long jobId, McpOperatorConfirmationAdmission receipt,
        IMcpOperatorJobStore jobs, McpOperatorJobContext context, HttpContext http,
        IMcpOperatorConfirmationService confirmations, IMcpOperatorRouteAdmission admission, CancellationToken ct)
    {
        if (receipt.Outcome != McpOperatorIdempotencyOutcome.Pending)
        {
            if (receipt.Outcome == McpOperatorIdempotencyOutcome.Succeeded &&
                ulong.TryParse(receipt.ResultReference, NumberStyles.None, CultureInfo.InvariantCulture, out var originalRunId))
            {
                var originalRun = await GetOwnedRunAsync(originalRunId, jobs, context, ct).ConfigureAwait(false);
                if (originalRun is { IsAggregate: false })
                {
                    var gatewayFailure = await LegacyGatewayFailureAsync(context, http, ct).ConfigureAwait(false);
                    if (gatewayFailure is not null) return Failure(gatewayFailure, context);
                }
                if (originalRun is { IsAggregate: true })
                {
                    try { await RequireParentTargetsAsync(originalRunId, context, admission,
                        http.RequestServices.GetRequiredService<OrchestratorDbContext>(), ct).ConfigureAwait(false); }
                    catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
                }
            }
            return await ReplayRunStartAsync(receipt, jobs, context, ct).ConfigureAwait(false);
        }
        if (receipt.IdempotencyId is not { } id) return Failure("idempotency_replay_unavailable", context);
        var db = http.RequestServices.GetRequiredService<OrchestratorDbContext>();
        var key = McpAggregateAdmissionKey.For(context.TenantId, context.Request.Principal.Subject, id);
        var parent = await db.JobAggregateControls.AsNoTracking().Where(control => control.AdmissionKey == key)
            .Select(control => (long?)control.ParentRunId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        // Confirmation, target reservations and protected original material are
        // one transaction. A crash before parent creation resumes that material;
        // group/definition changes never re-resolve an already admitted request.
        if (parent is null)
        {
            var original = await confirmations.GetReviewedJobSubmissionAsync(id, context.Decision, ct).ConfigureAwait(false);
            if (original is null) return null;
            if (original.Snapshot.Definition.Job.Id != (ulong)jobId) return Failure("idempotency_conflict", context);
            try
            {
                var verifier = http.RequestServices.GetRequiredService<IMcpAggregateExecutionAuthority>();
                foreach (var target in original.Snapshot.TargetPlan!.Nodes)
                    await verifier.RequireAllowedAsync(original.Snapshot, target.AgentId, ct).ConfigureAwait(false);
                var accepted = await admission.RecordAcceptedAsync(context.Request, ct).ConfigureAwait(false);
                var started = await http.RequestServices.GetRequiredService<IAkkaJobAuthorityService>()
                    .StartReviewedMcpAsync(original.Snapshot, original.Request, ct).ConfigureAwait(false);
                var recorded = await jobs.RecordAdmittedRunAsync(id, started.Id, ct).ConfigureAwait(false);
                await confirmations.CompleteAsync(id, McpOperatorIdempotencyOutcome.Succeeded,
                    started.Id.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                return Results.Ok(new McpOperatorJobRunResult(started.Id.ToString(CultureInfo.InvariantCulture), recorded.State, true, context.CorrelationId));
            }
            catch (Exception denial) when (denial is ExecutionAdmissionDeniedException or McpOperatorAdmissionRejectedException)
            {
                await confirmations.FailReviewedJobBeforeParentAsync(id, "job_current_authority_denied", ct).ConfigureAwait(false);
                return Failure(denial is McpOperatorAdmissionRejectedException rejection ? rejection.FailureCode : "job_start_rejected", context);
            }
            catch (InvalidOperationException) { return Failure("job_start_rejected", context); }
        }
        var runs = http.RequestServices.GetRequiredService<IJobRunService>();
        var run = await runs.GetAsync(checked((ulong)parent.Value), ct).ConfigureAwait(false);
        var snapshot = await runs.GetMcpExecutionSnapshotAsync(checked((ulong)parent.Value), ct).ConfigureAwait(false);
        var actual = await runs.GetResolvedTargetPlanAsync(checked((ulong)parent.Value), ct).ConfigureAwait(false);
        if (run is null || run.JobId != (ulong)jobId || run.TenantId != context.TenantId || snapshot?.ReviewIdempotencyId != id ||
            snapshot.Subject != context.Request.Principal.Subject || snapshot.TargetPlan is not { } approved ||
            actual is null || actual.ComputeDigest() != run.TargetPlanDigest || !actual.MatchesAdmissionPlan(approved))
            return Failure("idempotency_replay_unavailable", context);
        try { await RequireParentTargetsAsync(run.Id, context, admission, db, ct).ConfigureAwait(false); }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
        var audit = await admission.RecordAcceptedAsync(context.Request, ct).ConfigureAwait(false);
        var owned = await jobs.RecordAdmittedRunAsync(id, run.Id, ct).ConfigureAwait(false);
        await confirmations.CompleteAsync(id, McpOperatorIdempotencyOutcome.Succeeded, run.Id.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        return Results.Ok(new McpOperatorJobRunResult(run.Id.ToString(CultureInfo.InvariantCulture), owned.State, true, context.CorrelationId));
    }
}
