using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Production V2 operator adapter for explicitly-owned job definitions and
/// runs. It does not invoke the legacy ExternalService job routes: library-script
/// steps, revision checks, policy re-evaluation, preview/confirm, and run
/// ownership are all enforced here before the established job authority runs.
/// </summary>
public static class McpOperatorJobEndpoints
{
    private const string JobsTool = "netratel_jobs";
    private const string RunsTool = "netratel_job_runs";
    private static readonly HashSet<string> JobMutations = new(StringComparer.Ordinal)
    {
        "create", "update", "delete", "param_add", "param_update", "param_delete", "step_add", "step_update", "step_reorder", "step_delete"
    };

    public static IEndpointRouteBuilder MapMcpOperatorJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs")
            .WithTags("MCP Operator Jobs")
            .RequireAuthorization("M2MOnly");

        group.MapGet("", ListAsync);
        group.MapGet("/{jobId:long}", GetAsync);
        group.MapGet("/{jobId:long}/details", DetailsAsync);
        group.MapGet("/{jobId:long}/params", ParamsAsync);
        group.MapGet("/{jobId:long}/steps", StepsAsync);
        group.MapPost("/validate", ValidateAsync);
        group.MapPost("/preview/{action}", PreviewMutationAsync);
        group.MapPost("/confirm/{action}", ConfirmMutationAsync);

        group.MapGet("/runs", ListRunsAsync);
        group.MapGet("/runs/query", ListRunsAsync);
        group.MapGet("/runs/{runId:long}", GetRunAsync);
        group.MapGet("/runs/{runId:long}/steps", RunStepsAsync);
        group.MapGet("/runs/{runId:long}/logs", RunLogsAsync);
        group.MapPost("/{jobId:long}/runs/preview", PreviewStartAsync);
        group.MapPost("/{jobId:long}/runs/confirm", ConfirmStartAsync);
        group.MapPost("/runs/{runId:long}/cancel/preview", PreviewCancelAsync);
        group.MapPost("/runs/{runId:long}/cancel/confirm", ConfirmCancelAsync);
        group.MapPost("/runs/{runId:long}/delete/preview", PreviewDeleteRunAsync);
        group.MapPost("/runs/{runId:long}/delete/confirm", ConfirmDeleteRunAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(int tenantId, Guid agentId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(JobsTool, "list", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            return Results.Ok((await jobs.ListOwnedAsync(tenantId, agentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken).ConfigureAwait(false)).Select(ToSummary));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> GetAsync(int tenantId, Guid agentId, long jobId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(JobsTool, "get", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var job = await GetOwnedJobAsync(jobId, jobs, context, cancellationToken).ConfigureAwait(false);
            if (job is null) return Failure("job_not_found", context);
            SetEtag(http, job.Version);
            return Results.Ok(ToSummary(job));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> DetailsAsync(int tenantId, Guid agentId, long jobId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(JobsTool, "details", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var job = await GetOwnedJobAsync(jobId, jobs, context, cancellationToken).ConfigureAwait(false);
            if (job is null) return Failure("job_not_found", context);
            SetEtag(http, job.Version);
            var parameters = await jobs.ListParametersAsync(jobId, tenantId, agentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken).ConfigureAwait(false);
            var steps = await jobs.ListStepsAsync(jobId, tenantId, agentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorJobDetails(ToSummary(job), parameters.Select(ToParameterView).ToArray(), steps.Select(ToStepView).ToArray()));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> ParamsAsync(int tenantId, Guid agentId, long jobId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(JobsTool, "params", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (await GetOwnedJobAsync(jobId, jobs, context, cancellationToken).ConfigureAwait(false) is not { } job) return Failure("job_not_found", context);
            SetEtag(http, job.Version);
            return Results.Ok((await jobs.ListParametersAsync(jobId, tenantId, agentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken).ConfigureAwait(false)).Select(ToParameterView));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> StepsAsync(int tenantId, Guid agentId, long jobId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(JobsTool, "steps", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (await GetOwnedJobAsync(jobId, jobs, context, cancellationToken).ConfigureAwait(false) is not { } job) return Failure("job_not_found", context);
            SetEtag(http, job.Version);
            return Results.Ok((await jobs.ListStepsAsync(jobId, tenantId, agentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken).ConfigureAwait(false)).Select(ToStepView));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> ValidateAsync(int tenantId, Guid agentId, McpOperatorJobDraft draft, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(JobsTool, "create", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken, delegatedOperation: "create").ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        try
        {
            await jobs.ValidateDraftAsync(context.Decision, draft, cancellationToken).ConfigureAwait(false);
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorJobValidation(draft.Name.Trim(), draft.FolderPath.Trim(), context.Decision.MatchingPolicyIds.Single(), Decimal(context.Decision.SelectedPolicyVersion!.Value), context.Decision.Request.TargetSetDigest!, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("job_invalid", context); }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> PreviewMutationAsync(int tenantId, Guid agentId, string action, McpOperatorJobMutationRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorConfirmationService confirmations, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
    {
        if (!JobMutations.Contains(action)) return Results.NotFound();
        var result = await TryContextAsync(JobsTool, action, tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        var validation = await ValidateMutationAsync(action, request, jobs, context, cancellationToken).ConfigureAwait(false);
        if (validation.FailureCode is { } code) return Failure(code, context);
        try
        {
            var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(context.Decision, MutationHash(action, request)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorJobPreview(plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass, action, Decimal(request.JobId), Decimal(request.ExpectedVersion), context.Decision.Request.TargetSetDigest!, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmMutationAsync(int tenantId, Guid agentId, string action, McpOperatorJobMutationRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorConfirmationService confirmations, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
    {
        if (!JobMutations.Contains(action)) return Results.NotFound();
        var result = await TryContextAsync(JobsTool, action, tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        if (!HasPlan(request.PlanToken, request.IdempotencyKey)) return Failure("confirmation_plan_invalid", context);
        var validation = await ValidateMutationAsync(action, request, jobs, context, cancellationToken).ConfigureAwait(false);
        if (validation.FailureCode is { } code) return Failure(code, context);
        var confirmation = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, MutationHash(action, request), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationCode) return Failure(confirmationCode, context);
        if (confirmation.IsReplay) return await ReplayMutationAsync(action, confirmation, jobs, context, cancellationToken).ConfigureAwait(false);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);

        try
        {
            var audit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var mutation = await ApplyMutationAsync(action, request, jobs, context.Decision, audit, cancellationToken).ConfigureAwait(false);
            if (mutation is null) { await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "job_not_found", CancellationToken.None).ConfigureAwait(false); return Failure("job_not_found", context); }
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, mutation.Value.Job.JobId.ToString(), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorJobMutationResult(Decimal(mutation.Value.Job.JobId), Decimal(mutation.Value.Job.Version), Decimal(mutation.Value.ItemId), action == "delete", false, context.CorrelationId));
        }
        catch (McpOperatorJobConcurrencyException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "job_etag_mismatch", CancellationToken.None).ConfigureAwait(false);
            return Failure("job_etag_mismatch", context);
        }
        catch (ArgumentException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "job_invalid", CancellationToken.None).ConfigureAwait(false);
            return Failure("job_invalid", context);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, CancellationToken.None).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }
        catch (InvalidOperationException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "job_active_run_conflict", CancellationToken.None).ConfigureAwait(false);
            return Failure("job_active_run_conflict", context);
        }
    }

    private static async Task<IResult> ListRunsAsync(int tenantId, Guid agentId, long? jobId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
    {
        var operation = http.Request.Path.Value?.EndsWith("/query", StringComparison.Ordinal) == true ? "query" : "list";
        var result = await TryContextAsync(RunsTool, operation, tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            return Results.Ok((await jobs.ListRunsOwnedAsync(jobId, tenantId, agentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken).ConfigureAwait(false)).Select(ToRunSummary));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> GetRunAsync(int tenantId, Guid agentId, ulong runId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(RunsTool, "get", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var run = await GetOwnedRunAsync(runId, jobs, context, cancellationToken).ConfigureAwait(false);
            return run is null ? Failure("job_run_not_found", context) : Results.Ok(ToRunSummary(run));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> RunStepsAsync(int tenantId, Guid agentId, ulong runId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, IJobRunService runs, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(RunsTool, "steps", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (await GetOwnedRunAsync(runId, jobs, context, cancellationToken).ConfigureAwait(false) is null) return Failure("job_run_not_found", context);
            var details = await runs.GetDetailsAsync(runId, cancellationToken).ConfigureAwait(false);
            return details is null ? Failure("job_run_not_found", context) : Results.Ok(details.Steps);
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> RunLogsAsync(int tenantId, Guid agentId, ulong runId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, IJobRunService runs, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(RunsTool, "logs", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (await GetOwnedRunAsync(runId, jobs, context, cancellationToken).ConfigureAwait(false) is null) return Failure("job_run_not_found", context);
            var details = await runs.GetDetailsAsync(runId, cancellationToken).ConfigureAwait(false);
            if (details is null) return Failure("job_run_not_found", context);
            var logs = new List<McpOperatorJobLogLine>();
            foreach (var activity in details.Activities.Take(32))
            {
                var persisted = await runs.GetLogsByRequestIdAsync(activity.RequestId, cancellationToken).ConfigureAwait(false);
                logs.AddRange(ProjectActivityLogs(activity, persisted, 100 - logs.Count));
                if (logs.Count == 100) break;
            }
            return Results.Ok(logs);
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> PreviewStartAsync(int tenantId, Guid agentId, long jobId, McpOperatorJobRunRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorConfirmationService confirmations, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(RunsTool, "start", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, true, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        var startValidation = await CanStartAsync(jobId, request, jobs, admission, context, cancellationToken).ConfigureAwait(false);
        if (startValidation.FailureCode is { } startFailure) return Failure(startFailure, context);
        try
        {
            var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(context.Decision, RunHash(jobId, request)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorJobRunPreview(plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass, Decimal(jobId), context.Decision.Request.TargetSetDigest!, 1, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmStartAsync(int tenantId, Guid agentId, long jobId, McpOperatorJobRunRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorConfirmationService confirmations, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, IAkkaJobAuthorityService authority, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(RunsTool, "start", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, true, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        if (!HasPlan(request.PlanToken, request.IdempotencyKey)) return Failure("confirmation_plan_invalid", context);
        var startValidation = await CanStartAsync(jobId, request, jobs, admission, context, cancellationToken).ConfigureAwait(false);
        if (startValidation.FailureCode is { } startFailure) return Failure(startFailure, context);
        var confirmation = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, RunHash(jobId, request), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } code) return Failure(code, context);
        if (confirmation.IsReplay) return await ReplayRunStartAsync(confirmation, jobs, context, cancellationToken).ConfigureAwait(false);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);
        try
        {
            var audit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var inputsJson = JsonSerializer.Serialize(request.Inputs ?? new Dictionary<string, string>());
            var run = await authority.StartAsync(checked((ulong)jobId), new RunJobRequest($"mcp:{context.Request.Principal.Subject}", inputsJson, null), cancellationToken).ConfigureAwait(false);
            var owned = await jobs.RecordStartedRunAsync(jobId, run.Id, idempotencyId, context.Decision, audit, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, run.Id.ToString(), cancellationToken).ConfigureAwait(false);
            return Results.Accepted($"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/jobs/runs/{run.Id}", new McpOperatorJobRunResult(run.Id.ToString(), owned.State, false, context.CorrelationId));
        }
        catch (AgentJobGatewaySessionUnavailableException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "agent_job_session_unavailable", CancellationToken.None).ConfigureAwait(false);
            return Failure("agent_job_session_unavailable", context);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, CancellationToken.None).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }
        catch (InvalidOperationException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "job_start_rejected", CancellationToken.None).ConfigureAwait(false);
            return Failure("job_start_rejected", context);
        }
    }

    private static Task<IResult> PreviewCancelAsync(int tenantId, Guid agentId, ulong runId, McpOperatorJobRunActionRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorConfirmationService confirmations, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
        => PreviewRunActionAsync("cancel", tenantId, agentId, runId, request, http, environment, presence, admission, confirmations, options, localAgents, dispatcher, jobs, true, cancellationToken);

    private static Task<IResult> ConfirmCancelAsync(int tenantId, Guid agentId, ulong runId, McpOperatorJobRunActionRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorConfirmationService confirmations, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, IAkkaJobAuthorityService authority, CancellationToken cancellationToken)
        => ConfirmRunActionAsync("cancel", tenantId, agentId, runId, request, http, environment, presence, admission, confirmations, options, localAgents, dispatcher, jobs, authority, cancellationToken);

    private static Task<IResult> PreviewDeleteRunAsync(int tenantId, Guid agentId, ulong runId, McpOperatorJobRunActionRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorConfirmationService confirmations, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, CancellationToken cancellationToken)
        => PreviewRunActionAsync("delete", tenantId, agentId, runId, request, http, environment, presence, admission, confirmations, options, localAgents, dispatcher, jobs, false, cancellationToken);

    private static Task<IResult> ConfirmDeleteRunAsync(int tenantId, Guid agentId, ulong runId, McpOperatorJobRunActionRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorConfirmationService confirmations, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, IAkkaJobAuthorityService authority, CancellationToken cancellationToken)
        => ConfirmRunActionAsync("delete", tenantId, agentId, runId, request, http, environment, presence, admission, confirmations, options, localAgents, dispatcher, jobs, authority, cancellationToken);

    private static async Task<IResult> PreviewRunActionAsync(string action, int tenantId, Guid agentId, ulong runId, McpOperatorJobRunActionRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorConfirmationService confirmations, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, bool requiresGateway, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(RunsTool, action, tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, requiresGateway, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        var run = await GetOwnedRunAsync(runId, jobs, context, cancellationToken).ConfigureAwait(false);
        if (run is null || run.TargetSetDigest != context.Decision.Request.TargetSetDigest || (action == "cancel" && IsTerminal(run.State)) || (action == "delete" && !IsTerminal(run.State))) return Failure("job_run_action_invalid", context);
        try
        {
            var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(context.Decision, RunActionHash(action, runId, request.Reason)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorJobRunPreview(plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass, Decimal(run.JobId), context.Decision.Request.TargetSetDigest!, 1, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmRunActionAsync(string action, int tenantId, Guid agentId, ulong runId, McpOperatorJobRunActionRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorConfirmationService confirmations, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorJobStore jobs, IAkkaJobAuthorityService authority, CancellationToken cancellationToken)
    {
        var result = await TryContextAsync(RunsTool, action, tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, action == "cancel", cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) return failure;
        var context = result.Context!;
        if (!HasPlan(request.PlanToken, request.IdempotencyKey)) return Failure("confirmation_plan_invalid", context);
        var run = await GetOwnedRunAsync(runId, jobs, context, cancellationToken).ConfigureAwait(false);
        if (run is null || run.TargetSetDigest != context.Decision.Request.TargetSetDigest || (action == "cancel" && IsTerminal(run.State)) || (action == "delete" && !IsTerminal(run.State))) return Failure("job_run_action_invalid", context);
        var confirmation = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, RunActionHash(action, runId, request.Reason), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } code) return Failure(code, context);
        if (confirmation.IsReplay) return ReplayRunAction(confirmation, run, context);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);
        try
        {
            var audit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            McpOperatorJobRunLease? updated;
            if (action == "cancel")
            {
                if (!await authority.CancelAsync(runId, BoundedReason(request.Reason), cancellationToken).ConfigureAwait(false))
                {
                    await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "job_cancel_rejected", CancellationToken.None).ConfigureAwait(false);
                    return Failure("job_cancel_rejected", context);
                }
                updated = await jobs.RecordCancellationRequestedAsync(runId, context.Decision, audit, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                updated = await jobs.DeleteRunAsync(runId, context.Decision, audit, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            }
            if (updated is null)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "job_run_action_invalid", CancellationToken.None).ConfigureAwait(false);
                return Failure("job_run_action_invalid", context);
            }
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, runId.ToString(), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorJobRunResult(runId.ToString(), updated.State, false, context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, CancellationToken.None).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }
        catch (InvalidOperationException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "job_run_action_invalid", CancellationToken.None).ConfigureAwait(false);
            return Failure("job_run_action_invalid", context);
        }
    }

    private static async Task<MutationValidation> ValidateMutationAsync(string action, McpOperatorJobMutationRequest request, IMcpOperatorJobStore jobs, McpOperatorJobContext context, CancellationToken cancellationToken)
    {
        if (action == "create")
        {
            if (request.Job is null || request.JobId is not null || request.ExpectedVersion is not null) return MutationValidation.Invalid;
            try { await jobs.ValidateDraftAsync(context.Decision, request.Job, cancellationToken).ConfigureAwait(false); return MutationValidation.Valid; }
            catch (ArgumentException) { return MutationValidation.Invalid; }
        }
        if (request.JobId is not > 0 || request.ExpectedVersion is not > 0) return MutationValidation.Invalid;
        var existing = await GetOwnedJobAsync(request.JobId.Value, jobs, context, cancellationToken).ConfigureAwait(false);
        if (existing is null) return MutationValidation.NotFound;
        if (existing.Version != request.ExpectedVersion.Value) return new("job_etag_mismatch");
        if (action == "update")
        {
            if (request.Job is null) return MutationValidation.Invalid;
            try { await jobs.ValidateDraftAsync(context.Decision, request.Job, cancellationToken).ConfigureAwait(false); return MutationValidation.Valid; }
            catch (ArgumentException) { return MutationValidation.Invalid; }
        }
        if (action == "delete") return request.Parameter is null && request.Step is null ? MutationValidation.Valid : MutationValidation.Invalid;
        if (action.StartsWith("param_", StringComparison.Ordinal))
        {
            if (action is "param_add" or "param_update")
            {
                if (request.Parameter is null || (action == "param_add" && request.ParameterId is not null) || (action == "param_update" && request.ParameterId is not > 0)) return MutationValidation.Invalid;
                try { await jobs.ValidateParameterAsync(request.Parameter, cancellationToken).ConfigureAwait(false); return MutationValidation.Valid; }
                catch (ArgumentException) { return MutationValidation.Invalid; }
            }
            return action switch
            {
                "param_delete" when request.ParameterId is > 0 => MutationValidation.Valid,
                _ => MutationValidation.Invalid
            };
        }
        if (action.StartsWith("step_", StringComparison.Ordinal))
        {
            if (action is "step_add" or "step_update")
            {
                if (request.Step is null || (action == "step_add" && request.StepId is not null) || (action == "step_update" && request.StepId is not > 0)) return MutationValidation.Invalid;
                try { await jobs.ValidateStepAsync(request.Step, context.Decision, cancellationToken).ConfigureAwait(false); return MutationValidation.Valid; }
                catch (ArgumentException) { return MutationValidation.Invalid; }
            }
            return action switch
            {
                "step_reorder" when request.StepId is > 0 && request.Ordinal is > 0 => MutationValidation.Valid,
                "step_delete" when request.StepId is > 0 => MutationValidation.Valid,
                _ => MutationValidation.Invalid
            };
        }
        return MutationValidation.Invalid;
    }

    private static async Task<(McpOperatorJobLease Job, long? ItemId)?> ApplyMutationAsync(string action, McpOperatorJobMutationRequest request, IMcpOperatorJobStore jobs, McpOperatorDecision decision, McpOperatorAcceptedAudit audit, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return action switch
        {
            "create" => (await jobs.CreateAsync(new McpOperatorJobCreateRequest(decision, audit, request.Job!, now), cancellationToken).ConfigureAwait(false), null),
            "update" => ToMutation(await jobs.ReplaceAsync(new McpOperatorJobReplaceRequest(request.JobId!.Value, request.ExpectedVersion!.Value, decision, audit, request.Job!, now), cancellationToken).ConfigureAwait(false)),
            "delete" => ToMutation(await jobs.DeleteAsync(request.JobId!.Value, request.ExpectedVersion!.Value, decision, audit, now, cancellationToken).ConfigureAwait(false)),
            "param_add" => await jobs.AddParameterAsync(new McpOperatorJobParameterMutationRequest(request.JobId!.Value, null, request.ExpectedVersion!.Value, decision, audit, request.Parameter!, now), cancellationToken).ConfigureAwait(false),
            "param_update" => ToMutation(await jobs.ReplaceParameterAsync(new McpOperatorJobParameterMutationRequest(request.JobId!.Value, request.ParameterId, request.ExpectedVersion!.Value, decision, audit, request.Parameter!, now), cancellationToken).ConfigureAwait(false)),
            "param_delete" => ToMutation(await jobs.DeleteParameterAsync(request.JobId!.Value, request.ParameterId!.Value, request.ExpectedVersion!.Value, decision, audit, now, cancellationToken).ConfigureAwait(false)),
            "step_add" => await jobs.AddStepAsync(new McpOperatorJobStepMutationRequest(request.JobId!.Value, null, request.ExpectedVersion!.Value, decision, audit, request.Step!, now), cancellationToken).ConfigureAwait(false),
            "step_update" => ToMutation(await jobs.ReplaceStepAsync(new McpOperatorJobStepMutationRequest(request.JobId!.Value, request.StepId, request.ExpectedVersion!.Value, decision, audit, request.Step!, now), cancellationToken).ConfigureAwait(false)),
            "step_reorder" => ToMutation(await jobs.ReorderStepAsync(request.JobId!.Value, request.StepId!.Value, request.Ordinal!.Value, request.ExpectedVersion!.Value, decision, audit, now, cancellationToken).ConfigureAwait(false)),
            "step_delete" => ToMutation(await jobs.DeleteStepAsync(request.JobId!.Value, request.StepId!.Value, request.ExpectedVersion!.Value, decision, audit, now, cancellationToken).ConfigureAwait(false)),
            _ => null
        };
    }

    private static (McpOperatorJobLease Job, long? ItemId)? ToMutation(McpOperatorJobLease? job) => job is null ? null : (job, null);

    private static async Task<IResult> ReplayMutationAsync(string action, McpOperatorConfirmationAdmission confirmation, IMcpOperatorJobStore jobs, McpOperatorJobContext context, CancellationToken cancellationToken)
    {
        if (confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending) return Failure("idempotency_pending", context);
        if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded || !long.TryParse(confirmation.ResultReference, out var jobId)) return Failure("idempotency_replay_unavailable", context);
        if (action == "delete") return Results.Ok(new McpOperatorJobMutationResult(Decimal(jobId), "0", null, true, true, context.CorrelationId));
        var job = await GetOwnedJobAsync(jobId, jobs, context, cancellationToken).ConfigureAwait(false);
        return job is null ? Failure("idempotency_replay_unavailable", context) : Results.Ok(new McpOperatorJobMutationResult(Decimal(job.JobId), Decimal(job.Version), null, false, true, context.CorrelationId));
    }

    private static async Task<JobRunStartValidation> CanStartAsync(long jobId, McpOperatorJobRunRequest request, IMcpOperatorJobStore jobs, IMcpOperatorRouteAdmission admission, McpOperatorJobContext context, CancellationToken cancellationToken)
    {
        var job = await GetOwnedJobAsync(jobId, jobs, context, cancellationToken).ConfigureAwait(false);
        if (job is null || !await jobs.IsExecutableAsync(jobId, context.Decision, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken).ConfigureAwait(false)) return JobRunStartValidation.Invalid;
        var scriptAccess = McpOperationAccessCatalog.Find("netratel_scripts", "get");
        if (scriptAccess is null) return JobRunStartValidation.Invalid;
        var scriptRoute = context.Request with
        {
            Tool = "netratel_scripts",
            Operation = "get",
            RequiredScopes = new HashSet<string>([McpOperationAccessScopeNames.Canonical(scriptAccess.RequiredScope)], StringComparer.Ordinal)
        };
        var scriptAdmission = await admission.EvaluateAsync(scriptRoute, cancellationToken).ConfigureAwait(false);
        if (!scriptAdmission.Decision.IsAllowed) return new(scriptAdmission.Decision.FailureCode ?? "script_read_not_authorized");
        var parameters = await jobs.ListParametersAsync(jobId, context.TenantId, context.AgentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken).ConfigureAwait(false);
        return InputsAreValid(parameters, request.Inputs) ? JobRunStartValidation.Valid : JobRunStartValidation.Invalid;
    }

    private static async Task<IResult> ReplayRunStartAsync(McpOperatorConfirmationAdmission confirmation, IMcpOperatorJobStore jobs, McpOperatorJobContext context, CancellationToken cancellationToken)
    {
        if (confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending) return Failure("idempotency_pending", context);
        if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded || !ulong.TryParse(confirmation.ResultReference, out var runId)) return Failure("idempotency_replay_unavailable", context);
        var run = await GetOwnedRunAsync(runId, jobs, context, cancellationToken).ConfigureAwait(false);
        return run is null ? Failure("idempotency_replay_unavailable", context) : Results.Ok(new McpOperatorJobRunResult(runId.ToString(), run.State, true, context.CorrelationId));
    }

    private static IResult ReplayRunAction(McpOperatorConfirmationAdmission confirmation, McpOperatorJobRunLease run, McpOperatorJobContext context) =>
        confirmation.Outcome == McpOperatorIdempotencyOutcome.Succeeded && confirmation.ResultReference == run.RunId.ToString()
            ? Results.Ok(new McpOperatorJobRunResult(run.RunId.ToString(), run.State, true, context.CorrelationId))
            : Failure(confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending ? "idempotency_pending" : "idempotency_replay_unavailable", context);

    private static async Task<McpOperatorJobLease?> GetOwnedJobAsync(long jobId, IMcpOperatorJobStore jobs, McpOperatorJobContext context, CancellationToken cancellationToken) =>
        await jobs.GetOwnedAsync(jobId, context.TenantId, context.AgentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken).ConfigureAwait(false);

    private static async Task<McpOperatorJobRunLease?> GetOwnedRunAsync(ulong runId, IMcpOperatorJobStore jobs, McpOperatorJobContext context, CancellationToken cancellationToken) =>
        await jobs.GetRunOwnedAsync(runId, context.TenantId, context.AgentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken).ConfigureAwait(false);

    private static async Task<McpOperatorJobContextResult> TryContextAsync(string tool, string operation, int tenantId, Guid agentId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options, McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher, bool requiresGateway, CancellationToken cancellationToken, string? delegatedOperation = null)
    {
        var fallback = MinimalContext(tool, operation, tenantId, agentId, http.TraceIdentifier);
        var delegated = http.TryGetMcpOperatorDelegation(out var assertion) && assertion is not null;
        if (!delegated && !McpOperatorLocalAgentDelegation.TryCreate(http, environment, localAgents, tool, delegatedOperation ?? operation, tenantId, agentId, out assertion))
            return new(null, Failure(environment.IsProduction() && localAgents.Enabled ? "local_operator_identity_not_allowed" : "delegated_identity_required", fallback));
        var effective = assertion!;
        var access = McpOperationAccessCatalog.Find(tool, operation);
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var operatorEnvironment, out var expectedInstance) ||
            access is null || !string.Equals(effective.Instance, expectedInstance, StringComparison.Ordinal) || !string.Equals(effective.Tool, tool, StringComparison.Ordinal) ||
            !string.Equals(effective.Operation, delegatedOperation ?? operation, StringComparison.Ordinal) || effective.TenantId != tenantId || effective.AgentId != agentId ||
            string.IsNullOrWhiteSpace(effective.Resource) || string.IsNullOrWhiteSpace(effective.CorrelationId))
        {
            return new(null, Failure("delegated_identity_invalid", fallback));
        }
        var target = new ClientKey(tenantId, agentId);
        var online = !requiresGateway || (await presence.GetSnapshotAsync(target, cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var available = !requiresGateway || (options.IsJobAuthorityActive && dispatcher.IsAvailable(target));
        var route = new McpOperatorRouteAccessRequest(operatorEnvironment,
            new McpOperatorPrincipal(effective.Identity.Subject, effective.Identity.ClientId, effective.Identity.AuthorizedParty,
                effective.Identity.Groups.ToHashSet(StringComparer.Ordinal), effective.Identity.Roles.ToHashSet(StringComparer.Ordinal), effective.Identity.Scopes.ToHashSet(StringComparer.Ordinal)),
            effective.ServicePrincipal, effective.Resource!, effective.Instance!, tool, operation, tenantId, agentId,
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal), effective.CorrelationId!, effective.RequestId, online, available);
        var evaluated = await admission.EvaluateAsync(route, cancellationToken).ConfigureAwait(false);
        var context = new McpOperatorJobContext(route, evaluated.Decision, McpOperationAccessScopeNames.Canonical(access.RequiredScope), tool, operation, tenantId, agentId, effective.CorrelationId!);
        return evaluated.Decision.IsAllowed ? new(context, null) : new(context, Failure(evaluated.Decision.FailureCode ?? "target_policy_missing", context));
    }

    private static bool InputsAreValid(IReadOnlyList<McpOperatorJobParameter> schema, IReadOnlyDictionary<string, string>? inputs)
    {
        inputs ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (inputs.Count > schema.Count || inputs.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || pair.Value.Length > 1024 || pair.Value.Any(char.IsControl))) return false;
        foreach (var parameter in schema)
        {
            if (parameter.Required && !inputs.ContainsKey(parameter.Name) && parameter.DefaultValue is null) return false;
            if (!inputs.TryGetValue(parameter.Name, out var value)) continue;
            if (parameter.Type == "integer" && !long.TryParse(value, out _)) return false;
            if (parameter.Type == "boolean" && !bool.TryParse(value, out _)) return false;
            if (parameter.Type == "choice" && (parameter.Options is null || !parameter.Options.Contains(value, StringComparer.Ordinal))) return false;
            if (parameter.Type == "secret_reference" && !IsIdentifier(value, 128)) return false;
        }
        return inputs.Keys.All(key => schema.Any(parameter => parameter.Name == key));
    }

    private static string MutationHash(string action, McpOperatorJobMutationRequest request) => Hash(JsonSerializer.Serialize(new { action, request.JobId, request.ExpectedVersion, request.Job, request.ParameterId, request.Parameter, request.StepId, request.Step, request.Ordinal }));
    private static string RunHash(long jobId, McpOperatorJobRunRequest request) => Hash(JsonSerializer.Serialize(new { jobId, request.Inputs }));
    private static string RunActionHash(string action, ulong runId, string? reason) => Hash(JsonSerializer.Serialize(new { action, runId, reason = BoundedReason(reason) }));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool HasPlan(string? plan, string? key) => IsOpaque(plan) && IsOpaque(key);
    private static bool IsOpaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool IsIdentifier(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static bool IsTerminal(string state) => state is nameof(JobRunState.Succeeded) or nameof(JobRunState.Failed) or nameof(JobRunState.Cancelled) or nameof(JobRunState.TimedOut);

    internal static IReadOnlyList<McpOperatorJobLogLine> ProjectActivityLogs(
        JobTaskActivityInfo activity, IReadOnlyList<JobTaskLogInfo> persisted, int limit)
    {
        limit = Math.Clamp(limit, 0, 100);
        if (persisted.Count > 0)
            return persisted.Take(limit).Select(log => new McpOperatorJobLogLine(
                log.RequestId, log.Stream, TruncateAndRedact(log.Message), log.Sequence, log.TimestampUtc)).ToArray();
        if (limit == 0 || string.IsNullOrWhiteSpace(activity.ResultJson)) return [];

        // The job gateway persists terminal output with the activity; it does not emit log rows.
        // Prefer streamed rows when present so a terminal snapshot never duplicates streamed output.
        var logs = new List<McpOperatorJobLogLine>();
        try
        {
            using var document = JsonDocument.Parse(activity.ResultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            foreach (var stream in new[] { "stdout", "stderr" })
            {
                if (!document.RootElement.TryGetProperty(stream, out var output)) continue;
                if (output.ValueKind == JsonValueKind.String) Add(output, stream);
                else if (output.ValueKind == JsonValueKind.Array)
                    foreach (var line in output.EnumerateArray())
                    {
                        Add(line, stream);
                        if (logs.Count == limit) break;
                    }
                if (logs.Count == limit) break;
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return logs;

        void Add(JsonElement value, string stream)
        {
            if (value.ValueKind != JsonValueKind.String || logs.Count == limit) return;
            logs.Add(new McpOperatorJobLogLine(activity.RequestId, stream,
                TruncateAndRedact(value.GetString()!), logs.Count + 1,
                activity.CompletedAtUtc ?? activity.CreatedAtUtc));
        }
    }
    private static string BoundedReason(string? value) => string.IsNullOrWhiteSpace(value) ? "operator_requested" : value.Trim().Length > 128 || value.Any(char.IsControl) ? "operator_requested" : value.Trim();
    private static string TruncateAndRedact(string value)
    {
        var bounded = value.Length <= 4096 ? value : value[..4096];
        return System.Text.RegularExpressions.Regex.Replace(bounded, @"(?i)(bearer\s+|password\s*[=:]\s*|secret\s*[=:]\s*|token\s*[=:]\s*)[^\s;]+", "$1[REDACTED]");
    }
    private static void SetEtag(HttpContext http, long version) => http.Response.Headers.ETag = $"\"{version}\"";
    private static McpOperatorJobSummary ToSummary(McpOperatorJobLease job) => new(Decimal(job.JobId), job.TenantId, job.AgentId, job.Name, job.FolderPath, job.Description, job.PolicyId, Decimal(job.PolicyVersion), job.TargetSetDigest, job.CreatedAtUtc, job.UpdatedAtUtc, Decimal(job.Version));
    private static McpOperatorJobParameterView ToParameterView(McpOperatorJobParameter parameter) => new(Decimal(parameter.Id), parameter.Name, parameter.Type, parameter.Required, parameter.Description, parameter.DefaultValue, parameter.Options, parameter.SecretReference);
    private static McpOperatorJobStepView ToStepView(McpOperatorJobStep step) => new(Decimal(step.Id), step.Ordinal, Decimal(step.ScriptId), Decimal(step.ScriptVersion), step.ScriptContentHash, step.Enabled);
    private static McpOperatorJobRunSummary ToRunSummary(McpOperatorJobRunLease run) => new(Decimal(run.RunId), Decimal(run.JobId), run.State, run.CurrentStepOrdinal, run.CreatedAtUtc, run.StartedAtUtc, run.CompletedAtUtc, run.FailureCode, run.IsCancellationRequested, run.CorrelationId);
    private static string Decimal(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Decimal(ulong value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string? Decimal(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static McpOperatorJobContext MinimalContext(string tool, string operation, int tenantId, Guid agentId, string correlationId) => new(null!, null!, McpOperationAccessScopeNames.Canonical(McpOperationAccessCatalog.Find(tool, operation)!.RequiredScope), tool, operation, tenantId, agentId, correlationId);

    private static IResult Failure(string code, McpOperatorJobContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "job_not_found" or "job_run_not_found" => StatusCodes.Status404NotFound,
            "job_invalid" or "job_run_invalid" or "job_run_action_invalid" => StatusCodes.Status400BadRequest,
            "job_etag_mismatch" or "job_active_run_conflict" or "job_start_rejected" or "job_cancel_rejected" or "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => StatusCodes.Status409Conflict,
            "target_offline" or "capability_unavailable" or "agent_job_session_unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "oauth_scope_missing" => "oauth_scope",
            "tenant_not_authorized" => "tenant",
            "target_not_found" or "target_disabled" or "target_offline" => "target",
            "capability_unavailable" or "agent_job_session_unavailable" => "capability",
            "job_invalid" or "job_run_invalid" or "job_run_action_invalid" or "job_etag_mismatch" or "job_active_run_conflict" or "job_start_rejected" or "job_cancel_rejected" => "constraint",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            _ => "policy"
        };
        return Results.Problem(statusCode: status, title: "MCP operator job access was not admitted.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false,
            ["summary"] = "MCP operator job access was not admitted.",
            ["failure"] = new { code, layer, retryable = code is "target_offline" or "capability_unavailable" or "agent_job_session_unavailable", requiredScopes = new[] { context.RequiredScope }, requiredOperation = $"{context.Tool}/{context.Operation}", target = code == "tenant_not_authorized" ? null : new { context.TenantId, context.AgentId }, remediation = "Review explicit job ownership, the current ETag, exact library-script revision, script-read access, active target policy, and the job gateway for execution." },
            ["correlationId"] = context.CorrelationId
        });
    }

    private sealed record McpOperatorJobContext(McpOperatorRouteAccessRequest Request, McpOperatorDecision Decision, string RequiredScope, string Tool, string Operation, int TenantId, Guid AgentId, string CorrelationId);
    private sealed record McpOperatorJobContextResult(McpOperatorJobContext? Context, IResult? Failure);
    private readonly record struct MutationValidation(string? FailureCode)
    {
        public static MutationValidation Valid { get; } = new(null);
        public static MutationValidation Invalid { get; } = new("job_invalid");
        public static MutationValidation NotFound { get; } = new("job_not_found");
    }
    private readonly record struct JobRunStartValidation(string? FailureCode)
    {
        public static JobRunStartValidation Valid { get; } = new(null);
        public static JobRunStartValidation Invalid { get; } = new("job_run_invalid");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed record McpOperatorJobMutationRequest(McpOperatorJobDraft? Job = null, long? JobId = null, long? ExpectedVersion = null, long? ParameterId = null, McpOperatorJobParameter? Parameter = null, long? StepId = null, McpOperatorJobStep? Step = null, int? Ordinal = null, string? PlanToken = null, string? IdempotencyKey = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorJobRunRequest(IReadOnlyDictionary<string, string>? Inputs = null, string? PlanToken = null, string? IdempotencyKey = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorJobRunActionRequest(string? Reason = null, string? PlanToken = null, string? IdempotencyKey = null);
public sealed record McpOperatorJobSummary(string JobId, int TenantId, Guid AgentId, string Name, string FolderPath, string? Description, Guid PolicyId, string PolicyVersion, string TargetSetDigest, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string Version);
public sealed record McpOperatorJobParameterView(string? ParameterId, string Name, string Type, bool Required, string? Description, string? DefaultValue, IReadOnlyList<string>? Options, string? SecretReference);
public sealed record McpOperatorJobStepView(string? StepId, int? Ordinal, string ScriptId, string ScriptVersion, string ScriptContentHash, bool Enabled);
public sealed record McpOperatorJobDetails(McpOperatorJobSummary Job, IReadOnlyList<McpOperatorJobParameterView> Parameters, IReadOnlyList<McpOperatorJobStepView> Steps);
public sealed record McpOperatorJobValidation(string Name, string FolderPath, Guid PolicyId, string PolicyVersion, string TargetSetDigest, string CorrelationId);
public sealed record McpOperatorJobPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, string Action, string? JobId, string? ExpectedVersion, string TargetSetDigest, string CorrelationId);
public sealed record McpOperatorJobMutationResult(string JobId, string Version, string? ItemId, bool Deleted, bool Replayed, string CorrelationId);
public sealed record McpOperatorJobRunPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, string JobId, string TargetSetDigest, int TargetCount, string CorrelationId);
public sealed record McpOperatorJobRunResult(string JobRunId, string State, bool Replayed, string CorrelationId);
public sealed record McpOperatorJobRunSummary(string JobRunId, string JobId, string State, int CurrentStepOrdinal, DateTimeOffset CreatedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc, string? FailureCode, bool CancellationRequested, string CorrelationId);
public sealed record McpOperatorJobLogLine(string RequestId, string Stream, string Message, long Sequence, DateTimeOffset TimestampUtc);
