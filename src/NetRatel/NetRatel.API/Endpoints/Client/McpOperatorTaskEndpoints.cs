using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Operations;
using NetRatel.Shared.Security;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Production V2 task boundary. It never widens the legacy ExternalService task
/// route: every read is owner-scoped and every remote execution is admitted,
/// previewed, confirmed, idempotent, and delivered through the fenced command
/// gateway with a durable task activity for result aggregation.
/// </summary>
public static class McpOperatorTaskEndpoints
{
    private const string Tool = "netratel_tasks";
    private const int MaximumCommandBytes = 32 * 1024;
    private const int MaximumOutputBytes = 48 * 1024;
    private const int MaximumTimeoutSeconds = 60 * 60;

    public static IEndpointRouteBuilder MapMcpOperatorTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/tasks")
            .WithTags("MCP Operator Tasks")
            .RequireAuthorization("M2MOnly");

        group.MapGet("", ListAsync);
        group.MapGet("/recent", ListAsync);
        group.MapGet("/{taskId:long}", GetAsync);
        group.MapGet("/{taskId:long}/logs", LogsAsync);
        group.MapGet("/logs", LogsByRequestAsync);
        group.MapPost("/preview/{action}", PreviewAsync);
        group.MapPost("/confirm/{action}", ConfirmAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        int tenantId,
        Guid agentId,
        string? state,
        DateTimeOffset? sinceUtc,
        int? limit,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorTaskStore tasks,
        CancellationToken cancellationToken)
    {
        var operation = http.Request.Path.Value?.EndsWith("/recent", StringComparison.Ordinal) is true ? "recent" : "list";
        if (!IsTaskState(state) || limit is < 1 or > 100 || (sinceUtc.HasValue && sinceUtc.Value > DateTimeOffset.UtcNow.AddMinutes(5)))
            return Results.BadRequest(new { code = "task_query_invalid" });
        var admitted = await TryContextAsync(operation, tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var list = await tasks.ListOwnedAsync(tenantId, agentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, state, sinceUtc, limit ?? 25, cancellationToken).ConfigureAwait(false);
            return Results.Ok(list.Select(ToSummary));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> GetAsync(
        int tenantId,
        Guid agentId,
        long taskId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorTaskStore tasks,
        CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("get", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var task = await GetOwnedAsync(taskId, tasks, context, cancellationToken).ConfigureAwait(false);
            if (task is null) return Failure("task_not_found", context);
            http.Response.Headers.ETag = $"\"{task.Version}\"";
            return Results.Ok(ToDetails(task));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> LogsAsync(
        int tenantId,
        Guid agentId,
        long taskId,
        long? sinceId,
        string? stream,
        int? limit,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorTaskStore tasks,
        CancellationToken cancellationToken)
    {
        if (sinceId is < 0 || !IsLogStream(stream) || limit is < 1 or > 100)
            return Results.BadRequest(new { code = "task_logs_invalid" });
        var admitted = await TryContextAsync("logs", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (await GetOwnedAsync(taskId, tasks, context, cancellationToken).ConfigureAwait(false) is null) return Failure("task_not_found", context);
            var logs = await tasks.ListLogsOwnedAsync(taskId, tenantId, agentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, sinceId ?? 0, stream, limit ?? 100, cancellationToken).ConfigureAwait(false);
            return Results.Ok(logs.Select(ToLog));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> LogsByRequestAsync(
        int tenantId,
        Guid agentId,
        string requestId,
        long? sinceId,
        string? stream,
        int? limit,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorTaskStore tasks,
        CancellationToken cancellationToken)
    {
        if (!IsCommandId(requestId) || sinceId is < 0 || !IsLogStream(stream) || limit is < 1 or > 100)
            return Results.BadRequest(new { code = "task_logs_invalid" });
        var admitted = await TryContextAsync("logs_by_request", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var task = await tasks.GetOwnedByRequestIdAsync(requestId, tenantId, agentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken).ConfigureAwait(false);
            if (task is null) return Failure("task_not_found", context);
            var logs = await tasks.ListLogsOwnedAsync(task.TaskId, tenantId, agentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, sinceId ?? 0, stream, limit ?? 100, cancellationToken).ConfigureAwait(false);
            return Results.Ok(logs.Select(ToLog));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> PreviewAsync(
        int tenantId,
        Guid agentId,
        string action,
        McpOperatorTaskMutationRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorTaskStore tasks,
        IMcpOperatorScriptStore scripts,
        CancellationToken cancellationToken)
    {
        if (!IsMutation(action)) return Results.NotFound();
        var admitted = await TryContextAsync(action, tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, false, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        var resolution = await ResolveMutationAsync(action, request, tasks, scripts, admission, context, cancellationToken).ConfigureAwait(false);
        if (resolution.FailureCode is { } code) return Failure(code, context);
        try
        {
            var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(context.Decision, MutationHash(action, request)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorTaskPreview(plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass, action, Decimal(request.TaskId), context.Decision.Request.TargetSetDigest!, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmAsync(
        int tenantId,
        Guid agentId,
        string action,
        McpOperatorTaskMutationRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        [FromServices] IAgentCommandGatewaySessionRegistry commandSessions,
        IMcpOperatorTaskStore tasks,
        IMcpOperatorScriptStore scripts,
        CancellationToken cancellationToken)
    {
        if (!IsMutation(action)) return Results.NotFound();
        var admitted = await TryContextAsync(action, tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, true, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (!HasPlan(request.PlanToken, request.IdempotencyKey)) return Failure("confirmation_plan_invalid", context);
        var resolution = await ResolveMutationAsync(action, request, tasks, scripts, admission, context, cancellationToken).ConfigureAwait(false);
        if (resolution.FailureCode is { } code) return Failure(code, context);
        var confirmation = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, MutationHash(action, request), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationFailure) return Failure(confirmationFailure, context);
        if (confirmation.IsReplay) return await ReplayAsync(confirmation, tasks, context, cancellationToken).ConfigureAwait(false);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);

        McpOperatorAcceptedAudit audit;
        try
        {
            audit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, CancellationToken.None).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }

        if (action == "cancel")
        {
            var existing = resolution.Task!;
            if (IsTerminal(existing.State))
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "task_terminal_conflict", CancellationToken.None).ConfigureAwait(false);
                return Failure("task_terminal_conflict", context);
            }
            try
            {
                var cancelled = await tasks.RequestCancellationAsync(new McpOperatorTaskCancelRequest(existing.TaskId, context.Decision, audit, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                if (cancelled is null) { await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "task_not_found", CancellationToken.None).ConfigureAwait(false); return Failure("task_not_found", context); }
                await commandSessions.CancelAsync(new ClientKey(tenantId, agentId), cancelled.CommandId, "operator_task_cancelled", cancellationToken).ConfigureAwait(false);
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, cancelled.TaskId.ToString(), cancellationToken).ConfigureAwait(false);
                return Results.Accepted(TaskLocation(tenantId, agentId, cancelled.TaskId), new McpOperatorTaskMutationResult(Decimal(cancelled.TaskId), cancelled.State, false, context.CorrelationId));
            }
            catch (AgentCommandGatewaySessionUnavailableException)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "agent_command_session_unavailable", CancellationToken.None).ConfigureAwait(false);
                return Failure("agent_command_session_unavailable", context);
            }
        }

        var normalized = resolution.Normalized!;
        var commandId = Guid.NewGuid().ToString("N");
        McpOperatorTaskLease? createdTask = null;
        try
        {
            createdTask = await tasks.CreateOrGetAsync(new McpOperatorTaskCreateRequest(
                commandId, context.Decision, audit, idempotencyId, context.CorrelationId, normalized.TaskType,
                normalized.ShellType, normalized.CommandHash, normalized.CommandLength, normalized.Script?.Lease.ScriptId,
                normalized.Script?.Lease.Version, normalized.Script?.Lease.ContentHash, normalized.TimeoutSeconds,
                normalized.MaximumOutputBytes, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            await dispatcher.DispatchAsync(new ClientKey(tenantId, agentId), createdTask.CommandId, context.CorrelationId, createdTask.TaskType, normalized.PayloadJson, 0, cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, createdTask.TaskId.ToString(), cancellationToken).ConfigureAwait(false);
            return Results.Accepted(TaskLocation(tenantId, agentId, createdTask.TaskId), new McpOperatorTaskMutationResult(Decimal(createdTask.TaskId), createdTask.State, false, context.CorrelationId));
        }
        catch (McpOperatorTaskLimitException exception)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, exception.Code, CancellationToken.None).ConfigureAwait(false);
            return Failure(exception.Code, context);
        }
        catch (AgentCommandGatewaySessionUnavailableException)
        {
            await RecordDispatchFailureAsync(tasks, createdTask, tenantId, agentId, "agent_command_session_unavailable").ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "agent_command_session_unavailable", CancellationToken.None).ConfigureAwait(false);
            return Failure("agent_command_session_unavailable", context);
        }
        catch (ArgumentException)
        {
            await RecordDispatchFailureAsync(tasks, createdTask, tenantId, agentId, "task_invalid").ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "task_invalid", CancellationToken.None).ConfigureAwait(false);
            return Failure("task_invalid", context);
        }
        catch (Exception)
        {
            await RecordDispatchFailureAsync(tasks, createdTask, tenantId, agentId, "task_dispatch_failed").ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "task_dispatch_failed", CancellationToken.None).ConfigureAwait(false);
            return Failure("task_dispatch_failed", context);
        }
    }

    private static async Task<TaskMutationResolution> ResolveMutationAsync(string action, McpOperatorTaskMutationRequest request, IMcpOperatorTaskStore tasks, IMcpOperatorScriptStore scripts, IMcpOperatorRouteAdmission admission, McpOperatorTaskContext context, CancellationToken cancellationToken)
    {
        if (action == "cancel")
        {
            if (request.TaskId is not > 0 || request.Command is not null || request.Script is not null)
                return TaskMutationResolution.Invalid;
            var task = await GetOwnedAsync(request.TaskId.Value, tasks, context, cancellationToken).ConfigureAwait(false);
            return task is null ? TaskMutationResolution.NotFound : new(task, null, null);
        }
        if (request.TaskId is not null || (action == "create_command" && request.Command is null) || (action == "run_library_script" && request.Script is null) ||
            (action == "create_command" && request.Script is not null) || (action == "run_library_script" && request.Command is not null))
            return TaskMutationResolution.Invalid;
        if (action == "create_command")
            return TryNormalizeCommand(request.Command!, context.Decision, out var command, out var commandFailure)
                ? new(null, command, null)
                : new(null, null, commandFailure);

        var source = await ResolveScriptAsync(request.Script!, scripts, admission, context, cancellationToken).ConfigureAwait(false);
        if (source.FailureCode is not null) return new(null, null, source.FailureCode);
        var script = source.Script!.Value;
        var maximumOutput = Math.Min(context.Decision.EffectiveConstraints!.MaxOutputBytes!.Value, MaximumOutputBytes);
        if (script.Lease.TimeoutSeconds > context.Decision.EffectiveConstraints.MaxCommandDurationSeconds || maximumOutput <= 0)
            return TaskMutationResolution.Invalid;
        var payload = JsonSerializer.Serialize(new ExecLibraryScriptPayload
        {
            ScriptId = checked((int)script.Lease.ScriptId),
            ScriptType = script.Lease.ShellType == "sh" ? ScriptType.Bash : ScriptType.PowerShell,
            ScriptContent = script.Content,
            Parameters = request.Script!.Parameters?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            WorkingDirectory = script.Lease.WorkingDirectory,
            TimeoutSeconds = script.Lease.TimeoutSeconds
        });
        return new(null, new NormalizedTask(TaskKinds.ExecLibraryScript, null, null, 0, script.Lease.TimeoutSeconds, maximumOutput, payload, script), null);
    }

    private static bool TryNormalizeCommand(McpOperatorTaskCommandPayload request, McpOperatorDecision decision, out NormalizedTask? task, out string failureCode)
    {
        task = null;
        failureCode = "task_invalid";
        var constraints = decision.EffectiveConstraints;
        if (constraints is null || constraints.MaxCommandDurationSeconds is not > 0 || constraints.MaxOutputBytes is not > 0 ||
            constraints.AllowedShells is not { Count: > 0 } || constraints.WorkingDirectories is not { Count: > 0 } ||
            string.IsNullOrWhiteSpace(request.Command) || Encoding.UTF8.GetByteCount(request.Command) > MaximumCommandBytes ||
            request.TimeoutSeconds is < 1 or > MaximumTimeoutSeconds || request.TimeoutSeconds > constraints.MaxCommandDurationSeconds ||
            request.MaximumOutputBytes is < 1 or > MaximumOutputBytes || request.MaximumOutputBytes > constraints.MaxOutputBytes ||
            !IsShell(request.Shell) || !constraints.AllowedShells.Contains(request.Shell, StringComparer.OrdinalIgnoreCase) ||
            !IsAllowedDirectory(request.WorkingDirectory, constraints.WorkingDirectories) || !EnvironmentReferencesAreValid(request.EnvironmentReferences))
            return false;

        var shell = request.Shell.Trim().ToLowerInvariant();
        var command = request.Command.Trim();
        var payload = JsonSerializer.Serialize(new ExecShellCommandPayload
        {
            Preferred = ToShellExecutor(shell),
            Command = command,
            WorkingDirectory = request.WorkingDirectory,
            TimeoutSeconds = request.TimeoutSeconds,
            EnvironmentReferences = request.EnvironmentReferences?.ToList()
        });
        task = new NormalizedTask(TaskKinds.ExecShellCommand, shell, Hash(command), Encoding.UTF8.GetByteCount(command), request.TimeoutSeconds, request.MaximumOutputBytes, payload, null);
        return true;
    }

    private static async Task<ScriptResolution> ResolveScriptAsync(McpOperatorTaskScriptPayload request, IMcpOperatorScriptStore scripts, IMcpOperatorRouteAdmission admission, McpOperatorTaskContext context, CancellationToken cancellationToken)
    {
        if (request.ScriptId <= 0 || request.Version <= 0 || !IsSha256(request.ContentHash)) return ScriptResolution.Invalid;
        (McpOperatorScriptLease Lease, string Content, string? ManifestJson)? script;
        try
        {
            script = await scripts.GetOwnedAsync(request.ScriptId, context.TenantId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken).ConfigureAwait(false);
        }
        catch (McpOperatorScriptIntegrityException) { return new(null, "script_integrity_invalid"); }
        if (script is null || script.Value.Lease.Version != request.Version || !string.Equals(script.Value.Lease.ContentHash, request.ContentHash, StringComparison.OrdinalIgnoreCase) || !ParametersAreValid(script.Value.Lease.Parameters, request.Parameters))
            return ScriptResolution.Invalid;
        var access = McpOperationAccessCatalog.Find("netratel_scripts", "get");
        if (access is null) return ScriptResolution.Invalid;
        var scriptRequest = context.Request with
        {
            Tool = "netratel_scripts",
            Operation = "get",
            RequiredScopes = new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal)
        };
        var decision = await admission.EvaluateAsync(scriptRequest, cancellationToken).ConfigureAwait(false);
        return decision.Decision.IsAllowed ? new(script, null) : new(null, "script_access_not_admitted");
    }

    private static async Task<IResult> ReplayAsync(McpOperatorConfirmationAdmission confirmation, IMcpOperatorTaskStore tasks, McpOperatorTaskContext context, CancellationToken cancellationToken)
    {
        if (confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending) return Failure("idempotency_pending", context);
        if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded || !long.TryParse(confirmation.ResultReference, out var taskId)) return Failure("idempotency_replay_unavailable", context);
        var task = await GetOwnedAsync(taskId, tasks, context, cancellationToken).ConfigureAwait(false);
        return task is null
            ? Failure("idempotency_replay_unavailable", context)
            : Results.Ok(new McpOperatorTaskMutationResult(Decimal(task.TaskId), task.State, true, context.CorrelationId));
    }

    private static Task<McpOperatorTaskLease?> GetOwnedAsync(long taskId, IMcpOperatorTaskStore tasks, McpOperatorTaskContext context, CancellationToken cancellationToken) =>
        tasks.GetOwnedAsync(taskId, context.TenantId, context.AgentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken);

    private static Task RecordDispatchFailureAsync(IMcpOperatorTaskStore tasks, McpOperatorTaskLease? task, int tenantId, Guid agentId, string failureCode) =>
        task is null
            ? Task.CompletedTask
            : tasks.RecordLifecycleAsync(task.CommandId, tenantId, agentId, "Failed", failureCode, DateTimeOffset.UtcNow, CancellationToken.None);

    private static async Task<McpOperatorTaskContextResult> TryContextAsync(string operation, int tenantId, Guid agentId, HttpContext http, IHostEnvironment environment, [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options, McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, bool requiresGateway, CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(operation, tenantId, agentId, http.TraceIdentifier);
        var delegated = http.TryGetMcpOperatorDelegation(out var assertion) && assertion is not null;
        if (!delegated && !McpOperatorLocalAgentDelegation.TryCreate(http, environment, localAgents, Tool, operation, tenantId, agentId, out assertion))
            return new(null, Failure(environment.IsProduction() && localAgents.Enabled ? "local_operator_identity_not_allowed" : "delegated_identity_required", fallback));
        var effective = assertion!;
        var access = McpOperationAccessCatalog.Find(Tool, operation);
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var operatorEnvironment, out var expectedInstance) ||
            access is null || !string.Equals(effective.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(effective.Tool, Tool, StringComparison.Ordinal) || !string.Equals(effective.Operation, operation, StringComparison.Ordinal) ||
            effective.TenantId != tenantId || effective.AgentId != agentId || string.IsNullOrWhiteSpace(effective.Resource) || string.IsNullOrWhiteSpace(effective.CorrelationId))
            return new(null, Failure("delegated_identity_invalid", fallback));
        var target = new ClientKey(tenantId, agentId);
        var online = !requiresGateway || (await presence.GetSnapshotAsync(target, cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var available = !requiresGateway || (options.IsCommandAuthorityActive && dispatcher.IsAvailable(target));
        var route = new McpOperatorRouteAccessRequest(operatorEnvironment,
            new McpOperatorPrincipal(effective.Identity.Subject, effective.Identity.ClientId, effective.Identity.AuthorizedParty,
                effective.Identity.Groups.ToHashSet(StringComparer.Ordinal), effective.Identity.Roles.ToHashSet(StringComparer.Ordinal), effective.Identity.Scopes.ToHashSet(StringComparer.Ordinal)),
            effective.ServicePrincipal, effective.Resource!, effective.Instance!, Tool, operation, tenantId, agentId,
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal), effective.CorrelationId!, effective.RequestId, online, available);
        var evaluated = await admission.EvaluateAsync(route, cancellationToken).ConfigureAwait(false);
        var context = new McpOperatorTaskContext(route, evaluated.Decision, McpOperationAccessScopeNames.Canonical(access.RequiredScope), operation, tenantId, agentId, effective.CorrelationId!);
        return evaluated.Decision.IsAllowed ? new(context, null) : new(context, Failure(evaluated.Decision.FailureCode ?? "target_policy_missing", context));
    }

    private static bool ParametersAreValid(IReadOnlyList<McpOperatorScriptParameter> schema, IReadOnlyDictionary<string, string>? values)
    {
        values ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (values.Count > schema.Count || values.Any(pair => !IsIdentifier(pair.Key, 128) || pair.Value is null || pair.Value.Length > 1024 || pair.Value.Any(char.IsControl))) return false;
        foreach (var parameter in schema)
        {
            if (parameter.Required && !values.ContainsKey(parameter.Name) && parameter.DefaultValue is null) return false;
            if (!values.TryGetValue(parameter.Name, out var value)) continue;
            if (parameter.Type == "integer" && !long.TryParse(value, out _)) return false;
            if (parameter.Type == "boolean" && !bool.TryParse(value, out _)) return false;
            if (parameter.Type == "choice" && (parameter.Options is null || !parameter.Options.Contains(value, StringComparer.Ordinal))) return false;
            if (parameter.Type == "secret_reference" && !IsIdentifier(value, 128)) return false;
        }
        return values.Keys.All(key => schema.Any(parameter => parameter.Name == key));
    }

    private static string MutationHash(string action, McpOperatorTaskMutationRequest request) => Hash(JsonSerializer.Serialize(new { action, request.TaskId, request.Command, request.Script }));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool HasPlan(string? plan, string? key) => IsOpaque(plan) && IsOpaque(key);
    private static bool IsOpaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool IsMutation(string action) => action is "create_command" or "run_library_script" or "cancel";
    private static bool IsTerminal(string state) => state is "Completed" or "Failed" or "Cancelled";
    private static bool IsTaskState(string? state) => state is null or "Pending" or "Processing" or "CancelRequested" or "Completed" or "Failed" or "Cancelled";
    private static bool IsLogStream(string? value) => value is null or "all" or "stdout" or "stderr";
    private static bool IsCommandId(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool IsShell(string? value) => value is { Length: > 0 and <= 32 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool IsAllowedDirectory(string? value, IReadOnlyList<string> allowed) => value is { Length: > 0 and <= 4096 } && !value.Any(char.IsControl) && allowed.AllowsWorkingDirectory(value);
    private static bool EnvironmentReferencesAreValid(IReadOnlyList<string>? values) => values is not { Count: > 32 } && (values is null || values.All(value => IsIdentifier(value, 128)));
    private static bool IsIdentifier(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static ShellExecutor ToShellExecutor(string shell) => shell.ToLowerInvariant() switch { "powershell" => ShellExecutor.WindowsPowerShell, "pwsh" => ShellExecutor.Pwsh, "bash" or "sh" => ShellExecutor.Bash, "cmd" => ShellExecutor.Cmd, _ => ShellExecutor.Auto };
    private static string Decimal(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Decimal(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string TaskLocation(int tenantId, Guid agentId, long taskId) => $"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/tasks/{taskId}";
    private static McpOperatorTaskContext MinimalContext(string operation, int tenantId, Guid agentId, string correlationId) => new(null!, null!, McpOperationAccessScopeNames.Canonical(McpOperationAccessCatalog.Find(Tool, operation)!.RequiredScope), operation, tenantId, agentId, correlationId);
    private static McpOperatorTaskSummary ToSummary(McpOperatorTaskLease task) => new(Decimal(task.TaskId), task.CommandId, task.TenantId, task.AgentId, task.TaskType, task.State, task.IsCancellationRequested, task.CreatedAtUtc, task.UpdatedAtUtc, task.CompletedAtUtc, task.CorrelationId);
    private static McpOperatorTaskDetails ToDetails(McpOperatorTaskLease task) => new(ToSummary(task), task.ShellType, Decimal(task.ScriptId), Decimal(task.ScriptVersion), task.ScriptContentHash, task.TimeoutSeconds, task.MaximumOutputBytes, task.ResultSummary is null ? null : OperatorOutputRedactor.Redact(task.ResultSummary), task.PolicyId, Decimal(task.PolicyVersion), task.TargetSetDigest, Decimal(task.Version));
    private static McpOperatorTaskLogLine ToLog(McpOperatorTaskLogLease log) => new(Decimal(log.LogId), Decimal(log.TaskId), log.RequestId, log.Stream, OperatorOutputRedactor.Redact(log.Message.Length <= 4096 ? log.Message : log.Message[..4096]), log.Sequence, log.TimestampUtc);

    private static IResult Failure(string code, McpOperatorTaskContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "task_not_found" => StatusCodes.Status404NotFound,
            "task_invalid" or "task_query_invalid" or "task_logs_invalid" => StatusCodes.Status400BadRequest,
            "task_terminal_conflict" or "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => StatusCodes.Status409Conflict,
            "target_offline" or "capability_unavailable" or "agent_command_session_unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "oauth_scope_missing" => "oauth_scope",
            "tenant_not_authorized" => "tenant",
            "target_not_found" or "target_disabled" or "target_offline" => "target",
            "capability_unavailable" or "agent_command_session_unavailable" => "capability",
            "task_invalid" or "task_query_invalid" or "task_logs_invalid" or "task_terminal_conflict" => "constraint",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            _ => "policy"
        };
        return Results.Problem(statusCode: status, title: "MCP operator task access was not admitted.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false,
            ["summary"] = "MCP operator task access was not admitted.",
            ["failure"] = new { code, layer, retryable = code is "target_offline" or "capability_unavailable" or "agent_command_session_unavailable", requiredScopes = new[] { context.RequiredScope }, requiredOperation = $"{Tool}/{context.Operation}", target = code == "tenant_not_authorized" ? null : new { context.TenantId, context.AgentId }, remediation = "Review exact task ownership, typed input bounds, script revision access, active target policy, confirmation credentials, and the command gateway." },
            ["correlationId"] = context.CorrelationId
        });
    }

    private sealed record McpOperatorTaskContext(McpOperatorRouteAccessRequest Request, McpOperatorDecision Decision, string RequiredScope, string Operation, int TenantId, Guid AgentId, string CorrelationId);
    private sealed record McpOperatorTaskContextResult(McpOperatorTaskContext? Context, IResult? Failure);
    private sealed record NormalizedTask(string TaskType, string? ShellType, string? CommandHash, int CommandLength, int TimeoutSeconds, int MaximumOutputBytes, string PayloadJson, (McpOperatorScriptLease Lease, string Content, string? ManifestJson)? Script);
    private sealed record ScriptResolution((McpOperatorScriptLease Lease, string Content, string? ManifestJson)? Script, string? FailureCode)
    {
        public static ScriptResolution Invalid { get; } = new(null, "task_invalid");
    }
    private sealed record TaskMutationResolution(McpOperatorTaskLease? Task, NormalizedTask? Normalized, string? FailureCode)
    {
        public static TaskMutationResolution Invalid { get; } = new(null, null, "task_invalid");
        public static TaskMutationResolution NotFound { get; } = new(null, null, "task_not_found");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed record McpOperatorTaskMutationRequest(long? TaskId = null, McpOperatorTaskCommandPayload? Command = null, McpOperatorTaskScriptPayload? Script = null, string? PlanToken = null, string? IdempotencyKey = null);
public sealed record McpOperatorTaskPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, string Action, string TaskId, string TargetSetDigest, string CorrelationId);
public sealed record McpOperatorTaskMutationResult(string TaskId, string State, bool Replayed, string CorrelationId);
public sealed record McpOperatorTaskSummary(string TaskId, string RequestId, int TenantId, Guid AgentId, string TaskType, string State, bool CancellationRequested, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? CompletedAtUtc, string CorrelationId);
public sealed record McpOperatorTaskDetails(McpOperatorTaskSummary Task, string? ShellType, string ScriptId, string ScriptVersion, string? ScriptContentHash, int TimeoutSeconds, int MaximumOutputBytes, string? ResultSummary, Guid PolicyId, string PolicyVersion, string TargetSetDigest, string Version);
public sealed record McpOperatorTaskLogLine(string LogId, string TaskId, string RequestId, string Stream, string Message, long Sequence, DateTimeOffset TimestampUtc);
