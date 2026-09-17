using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Services.Operations;
using NetRatel.Application.Scripts;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Delegated script library adapter. Narrow grants retain caller-owned reviewed
/// definitions; full Development admission uses the canonical shared library
/// with source revision fencing. Execution still requires a reviewed definition.
/// </summary>
public static class McpOperatorScriptEndpoints
{
    private const string Tool = "netratel_scripts";
    private static readonly HashSet<string> MutationOperations = new(StringComparer.Ordinal)
    {
        "create", "update", "parse_manifest", "delete"
    };

    public static IEndpointRouteBuilder MapMcpOperatorScriptEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts")
            .WithTags("MCP Operator Scripts")
            .RequireAuthorization("M2MOnly");

        group.MapGet("", ListAsync);
        group.MapGet("/{scriptId:long}", GetAsync);
        group.MapGet("/{scriptId:long}/params", ParamsAsync);
        group.MapPost("/validate", ValidateAsync);
        group.MapPost("/preview/{action}", PreviewMutationAsync);
        group.MapPost("/confirm/{action}", ConfirmMutationAsync);
        group.MapPost("/{scriptId:long}/runs/preview", PreviewRunAsync);
        group.MapPost("/{scriptId:long}/runs/confirm", ConfirmRunAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        int tenantId, Guid agentId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorScriptStore scripts, CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync("list", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (McpDevelopmentScriptAdapter.IsAllowed(context.Decision))
            {
                if (!long.TryParse(http.Request.Query["afterId"].FirstOrDefault() ?? "0", out var afterId) || afterId < 0 ||
                    !int.TryParse(http.Request.Query["limit"].FirstOrDefault() ?? "100", out var limit) || limit is < 1 or > 100)
                    return Failure("script_invalid", context);
                var current = (await admission.EvaluateAsync(context.Request, cancellationToken)).Decision;
                return Results.Ok(await http.RequestServices.GetRequiredService<McpDevelopmentScriptAdapter>()
                    .ListAsync(current, afterId, limit, cancellationToken));
            }
            return Results.Ok((await scripts.ListOwnedAsync(tenantId, context.Request.Principal, context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false))
                .Select(ToSummary));
        }
        catch (McpDevelopmentScriptException denied) { return Failure(denied.Code, context); }
        catch (McpOperatorScriptIntegrityException) { return Failure("script_integrity_invalid", context); }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> GetAsync(
        int tenantId, Guid agentId, long scriptId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorScriptStore scripts, CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync("get", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (McpDevelopmentScriptAdapter.IsAllowed(context.Decision))
            {
                var current = (await admission.EvaluateAsync(context.Request, cancellationToken)).Decision;
                var source = await http.RequestServices.GetRequiredService<McpDevelopmentScriptAdapter>().GetAsync(scriptId, current, cancellationToken);
                if (source is null) return Failure("script_not_found", context);
                SetEtag(http, source.Script.SourceRevision);
                return Results.Ok(source);
            }
            var script = await scripts.GetOwnedAsync(scriptId, tenantId, context.Request.Principal, context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
            if (script is null) return Failure("script_not_found", context);
            SetEtag(http, script.Value.Lease.Version);
            return Results.Ok(new McpOperatorScriptDetail(ToSummary(script.Value.Lease), script.Value.Content, script.Value.ManifestJson));
        }
        catch (McpDevelopmentScriptException denied) { return Failure(denied.Code, context); }
        catch (McpOperatorScriptIntegrityException) { return Failure("script_integrity_invalid", context); }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> ParamsAsync(
        int tenantId, Guid agentId, long scriptId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorScriptStore scripts, CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync("params", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (McpDevelopmentScriptAdapter.IsAllowed(context.Decision))
            {
                var current = (await admission.EvaluateAsync(context.Request, cancellationToken)).Decision;
                var parameters = await http.RequestServices.GetRequiredService<McpDevelopmentScriptAdapter>().ParamsAsync(scriptId, current, cancellationToken);
                return parameters is null ? Failure("script_not_found", context) : Results.Ok(parameters);
            }
            var script = await scripts.GetOwnedAsync(scriptId, tenantId, context.Request.Principal, context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
            return script is null ? Failure("script_not_found", context) : Results.Ok(script.Value.Lease.Parameters);
        }
        catch (McpDevelopmentScriptException denied) { return Failure(denied.Code, context); }
        catch (McpOperatorScriptIntegrityException) { return Failure("script_integrity_invalid", context); }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> ValidateAsync(
        int tenantId, Guid agentId, McpOperatorScriptDraft draft, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorScriptStore scripts, CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync("validate", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await scripts.ValidateAsync(context.Decision, draft, cancellationToken).ConfigureAwait(false);
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorScriptValidation(
                draft.ContentHash.ToUpperInvariant(), Encoding.UTF8.GetByteCount(draft.Content), draft.ShellType,
                draft.TimeoutSeconds, draft.WorkingDirectory, draft.DeclaredSideEffects, draft.Parameters.Count,
                context.Decision.MatchingPolicyIds.Single(), context.Decision.SelectedPolicyVersion!.Value, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("script_invalid", context); }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> PreviewMutationAsync(
        int tenantId, Guid agentId, string action, McpOperatorScriptMutationRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorScriptStore scripts, IMcpOperatorConfirmationService confirmations, CancellationToken cancellationToken)
    {
        if (!MutationOperations.Contains(action)) return Results.NotFound();
        var admitted = await TryCreateContextAsync(action, tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (McpDevelopmentScriptAdapter.IsSourceMutation(request))
        {
            if (!McpDevelopmentScriptAdapter.IsAllowed(context.Decision)) return Failure("development_environment_access_required", context);
            try
            {
                var adapter = http.RequestServices.GetRequiredService<McpDevelopmentScriptAdapter>();
                return Results.Ok(await adapter.PreviewAsync(action, request, context.Decision, cancellationToken));
            }
            catch (McpDevelopmentScriptException denied) { return Failure(denied.Code, context); }
            catch (ScriptSourceConcurrencyException) { return Failure("script_source_revision_conflict", context); }
            catch (ArgumentException) { return Failure("script_invalid", context); }
            catch (JsonException) { return Failure("script_invalid", context); }
        }

        if (!TryBuildMutationDraft(action, request, tenantId, context, scripts, cancellationToken, out var draftTask))
            return Failure("script_invalid", context);
        McpOperatorScriptDraft? draft;
        try { draft = await draftTask.ConfigureAwait(false); }
        catch (McpOperatorScriptIntegrityException) { return Failure("script_integrity_invalid", context); }
        if (draft is null) return Failure("script_not_found", context);
        try
        {
            await scripts.ValidateAsync(context.Decision, draft, cancellationToken).ConfigureAwait(false);
            var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(context.Decision, MutationPayloadHash(action, request.ScriptId, request.ExpectedVersion, draft)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorScriptPreview(plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass,
                action, request.ScriptId, request.ExpectedVersion, draft.ContentHash.ToUpperInvariant(), RiskSummary(draft),
                context.Decision.MatchingPolicyIds.Single(), context.Decision.SelectedPolicyVersion!.Value, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("script_invalid", context); }
    }

    private static async Task<IResult> ConfirmMutationAsync(
        int tenantId, Guid agentId, string action, McpOperatorScriptMutationRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorScriptStore scripts, IMcpOperatorConfirmationService confirmations, CancellationToken cancellationToken)
    {
        if (!MutationOperations.Contains(action)) return Results.NotFound();
        var admitted = await TryCreateContextAsync(action, tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (McpDevelopmentScriptAdapter.IsSourceMutation(request))
        {
            if (!McpDevelopmentScriptAdapter.IsAllowed(context.Decision)) return Failure("development_environment_access_required", context);
            try
            {
                var adapter = http.RequestServices.GetRequiredService<McpDevelopmentScriptAdapter>();
                return Results.Ok(await adapter.ConfirmAsync(action, request, context.Decision, context.Request, cancellationToken));
            }
            catch (McpDevelopmentScriptException denied) { return Failure(denied.Code, context); }
            catch (ScriptSourceConcurrencyException) { return Failure("script_source_revision_conflict", context); }
            catch (ArgumentException) { return Failure("script_invalid", context); }
            catch (JsonException) { return Failure("script_invalid", context); }
        }

        if (!HasPlanCredentials(request.PlanToken, request.IdempotencyKey) ||
            !TryBuildMutationDraft(action, request, tenantId, context, scripts, cancellationToken, out var draftTask))
            return Failure("confirmation_plan_invalid", context);
        McpOperatorScriptDraft? draft;
        try { draft = await draftTask.ConfigureAwait(false); }
        catch (McpOperatorScriptIntegrityException) { return Failure("script_integrity_invalid", context); }
        if (draft is null) return Failure("script_not_found", context);

        var confirmation = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(
            request.PlanToken!, request.IdempotencyKey!, MutationPayloadHash(action, request.ScriptId, request.ExpectedVersion, draft), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } failureCode) return Failure(failureCode, context);
        if (confirmation.IsReplay)
            return await ReplayMutationAsync(action, confirmation, request.ScriptId, tenantId, context, scripts, cancellationToken).ConfigureAwait(false);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", context);

        try
        {
            var audit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            McpOperatorScriptLease? result = action switch
            {
                "create" => await scripts.CreateAsync(new McpOperatorScriptCreateRequest(context.Decision, audit, draft, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false),
                "update" or "parse_manifest" when request.ScriptId is > 0 && request.ExpectedVersion is > 0 => await scripts.ReplaceAsync(new McpOperatorScriptReplaceRequest(request.ScriptId.Value, request.ExpectedVersion.Value, context.Decision, audit, draft, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false),
                "delete" when request.ScriptId is > 0 && request.ExpectedVersion is > 0 => await scripts.DeleteAsync(request.ScriptId.Value, request.ExpectedVersion.Value, context.Decision, audit, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentException("The confirmed script mutation is invalid.")
            };
            if (result is null)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "script_not_found", cancellationToken).ConfigureAwait(false);
                return Failure("script_not_found", context);
            }
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, result.ScriptId.ToString(), cancellationToken).ConfigureAwait(false);
            SetEtag(http, result.Version);
            return action == "delete"
                ? Results.Ok(new McpOperatorScriptMutationResult(result.ScriptId, result.Version, true, false, context.CorrelationId))
                : Results.Created($"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/scripts/{result.ScriptId}", new McpOperatorScriptMutationResult(result.ScriptId, result.Version, false, false, context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, CancellationToken.None).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }
        catch (McpOperatorScriptConcurrencyException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "script_etag_mismatch", CancellationToken.None).ConfigureAwait(false);
            return Failure("script_etag_mismatch", context);
        }
        catch (ArgumentException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "script_invalid", CancellationToken.None).ConfigureAwait(false);
            return Failure("script_invalid", context);
        }
        catch (Exception)
        {
            // Mutation input can contain raw script content. Preserve only a
            // deterministic content-free outcome if a downstream persistence
            // boundary fails after confirmation admission.
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "script_mutation_failed", CancellationToken.None).ConfigureAwait(false);
            return Failure("script_mutation_failed", context);
        }
    }

    private static async Task<IResult> PreviewRunAsync(
        int tenantId, Guid agentId, long scriptId, McpOperatorScriptRunRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorScriptStore scripts, IMcpOperatorConfirmationService confirmations, CancellationToken cancellationToken)
    {
        var resolved = await ResolveRunAsync(tenantId, agentId, scriptId, request, http, environment, presence, admission, options, localAgents, dispatcher, scripts, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is { } failure) return failure;
        try
        {
            var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(resolved.Context!.Decision, RunPayloadHash(scriptId, request)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorScriptRunPreview(plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass,
                scriptId, request.Version, request.ContentHash.ToUpperInvariant(), 1, resolved.Context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", resolved.Context!); }
    }

    private static async Task<IResult> ConfirmRunAsync(
        int tenantId, Guid agentId, long scriptId, McpOperatorScriptRunRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher,
        IMcpOperatorScriptStore scripts, IMcpOperatorConfirmationService confirmations, IMcpOperatorTaskStore tasks, IEventRecorder events,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveRunAsync(tenantId, agentId, scriptId, request, http, environment, presence, admission, options, localAgents, dispatcher, scripts, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is { } failure) return failure;
        var context = resolved.Context!;
        if (!HasPlanCredentials(request.PlanToken, request.IdempotencyKey)) return Failure("confirmation_plan_invalid", context);
        var confirmation = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, RunPayloadHash(scriptId, request), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } failureCode) return Failure(failureCode, context);
        if (confirmation.IsReplay)
        {
            if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded)
                return Failure(confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending ? "idempotency_pending" : "idempotency_replay_unavailable", context);
            var existing = await tasks.GetOwnedByRequestIdAsync(confirmation.ResultReference!, tenantId, agentId,
                context.Request.Principal, context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorScriptRunResult(scriptId, confirmation.ResultReference!, true, context.CorrelationId)
            {
                TaskId = existing?.TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);

        var script = resolved.Script!.Value;
        var requestId = Guid.NewGuid().ToString("N");
        McpOperatorTaskLease? activity = null;
        try
        {
            var audit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            activity = await tasks.CreateOrGetAsync(new McpOperatorTaskCreateRequest(requestId, context.Decision, audit,
                idempotencyId, context.CorrelationId, TaskKinds.ExecLibraryScript, null, null, 0,
                script.Lease.ScriptId, script.Lease.Version, script.Lease.ContentHash, script.Lease.TimeoutSeconds,
                (int)Math.Min(48 * 1024, context.Decision.EffectiveConstraints!.MaxOutputBytes!.Value),
                DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            var payload = JsonSerializer.Serialize(new ExecLibraryScriptPayload
            {
                ScriptId = checked((int)script.Lease.ScriptId),
                ScriptType = script.Lease.ShellType == "sh" ? ScriptType.Bash : ScriptType.PowerShell,
                ScriptContent = script.Content,
                Parameters = request.Parameters?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                WorkingDirectory = script.Lease.WorkingDirectory,
                TimeoutSeconds = script.Lease.TimeoutSeconds
            });
            await dispatcher.DispatchAsync(new ClientKey(tenantId, agentId), requestId, context.CorrelationId, TaskKinds.ExecLibraryScript, payload, environment: 0, cancellationToken).ConfigureAwait(false);
            await events.RecordAsync(new DomainEvent
            {
                EventType = NetRatelEventTypes.Task.Submitted,
                Source = "McpOperatorScripts",
                CorrelationId = context.CorrelationId,
                TenantId = tenantId.ToString(),
                EntityId = activity.TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Severity = "Info",
                Message = $"Production operator script {script.Lease.ScriptId} submitted.",
                Payload = new { scriptId, scriptVersion = script.Lease.Version, scriptHash = script.Lease.ContentHash, requestId, agentId, acceptedAuditId = audit.AuditId }
            }, cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, requestId, cancellationToken).ConfigureAwait(false);
            return Results.Accepted($"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/tasks/{activity.TaskId}",
                new McpOperatorScriptRunResult(scriptId, requestId, false, context.CorrelationId)
                {
                    TaskId = activity.TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, CancellationToken.None).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }
        catch (McpOperatorTaskLimitException exception)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, exception.Code, CancellationToken.None).ConfigureAwait(false);
            return Failure(exception.Code, context);
        }
        catch (AgentCommandGatewaySessionUnavailableException)
        {
            if (activity is not null)
                await tasks.RecordLifecycleAsync(requestId, tenantId, agentId, "Failed", "agent_command_session_unavailable", DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "agent_command_session_unavailable", CancellationToken.None).ConfigureAwait(false);
            return Failure("agent_command_session_unavailable", context);
        }
        catch (Exception)
        {
            if (activity is not null)
                await tasks.RecordLifecycleAsync(requestId, tenantId, agentId, "Failed", "script_dispatch_failed", DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            // Do not expose dispatcher, task, or script-content diagnostics
            // through the operator result. The idempotency outcome remains
            // deterministic and safely replayable.
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "script_dispatch_failed", CancellationToken.None).ConfigureAwait(false);
            return Failure("script_dispatch_failed", context);
        }
    }

    private static async Task<McpOperatorScriptRunResolution> ResolveRunAsync(
        int tenantId, Guid agentId, long scriptId, McpOperatorScriptRunRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, IMcpOperatorScriptStore scripts, CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync("run", tenantId, agentId, http, environment, presence, admission, options, localAgents, dispatcher, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return new(null, null, failure);
        var context = admitted.Context!;
        (McpOperatorScriptLease Lease, string Content, string? ManifestJson)? script;
        try
        {
            script = await scripts.GetOwnedAsync(scriptId, tenantId, context.Request.Principal, context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
        }
        catch (McpOperatorScriptIntegrityException)
        {
            return new(context, null, Failure("script_integrity_invalid", context));
        }
        if (script is null) return new(context, null, Failure("script_not_found", context));
        if (request.Version != script.Value.Lease.Version || !string.Equals(request.ContentHash, script.Value.Lease.ContentHash, StringComparison.OrdinalIgnoreCase) ||
            !ParametersAreValid(script.Value.Lease.Parameters, request.Parameters))
        {
            return new(context, null, Failure("script_version_or_parameters_invalid", context));
        }
        var constraints = context.Decision.EffectiveConstraints;
        if (constraints is null || constraints.MaxConcurrentCommands is not > 0 || constraints.MaxTaskTargetCount is not > 0 ||
            constraints.MaxFanOut is not > 0 || constraints.MaxOutputBytes is not > 0 ||
            constraints.MaxCommandDurationSeconds is null || script.Value.Lease.TimeoutSeconds > constraints.MaxCommandDurationSeconds)
            return new(context, null, Failure("script_execution_limits_invalid", context));
        return new(context, script, null);
    }

    private static bool TryBuildMutationDraft(
        string action, McpOperatorScriptMutationRequest request, int tenantId, McpOperatorScriptContext context,
        IMcpOperatorScriptStore scripts, CancellationToken cancellationToken, out Task<McpOperatorScriptDraft?> draftTask)
    {
        if (action is "create" or "update")
        {
            draftTask = Task.FromResult(request.Script);
            return request.Script is not null && (action == "create" || request.ScriptId is > 0 && request.ExpectedVersion is > 0);
        }
        if (action == "parse_manifest")
        {
            if (request.ScriptId is not > 0 || request.ExpectedVersion is not > 0 || string.IsNullOrWhiteSpace(request.ManifestJson))
            {
                draftTask = Task.FromResult<McpOperatorScriptDraft?>(null);
                return false;
            }
            draftTask = BuildManifestDraftAsync(request, tenantId, context, scripts, cancellationToken);
            return true;
        }
        if (action == "delete")
        {
            if (request.ScriptId is not > 0 || request.ExpectedVersion is not > 0)
            {
                draftTask = Task.FromResult<McpOperatorScriptDraft?>(null);
                return false;
            }
            draftTask = BuildExistingDraftAsync(request.ScriptId.Value, tenantId, context, scripts, cancellationToken);
            return true;
        }
        draftTask = Task.FromResult<McpOperatorScriptDraft?>(null);
        return false;
    }

    private static async Task<McpOperatorScriptDraft?> BuildManifestDraftAsync(McpOperatorScriptMutationRequest request, int tenantId, McpOperatorScriptContext context, IMcpOperatorScriptStore scripts, CancellationToken cancellationToken)
    {
        var existing = await scripts.GetOwnedAsync(request.ScriptId!.Value, tenantId, context.Request.Principal, context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
        return existing is null ? null : ToDraft(existing.Value, request.ManifestJson);
    }

    private static async Task<McpOperatorScriptDraft?> BuildExistingDraftAsync(long scriptId, int tenantId, McpOperatorScriptContext context, IMcpOperatorScriptStore scripts, CancellationToken cancellationToken)
    {
        var existing = await scripts.GetOwnedAsync(scriptId, tenantId, context.Request.Principal, context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
        return existing is null ? null : ToDraft(existing.Value, existing.Value.ManifestJson);
    }

    private static McpOperatorScriptDraft ToDraft((McpOperatorScriptLease Lease, string Content, string? ManifestJson) existing, string? manifestJson) => new(
        existing.Lease.Name, existing.Lease.Description, existing.Lease.ShellType, existing.Content, existing.Lease.ContentHash,
        existing.Lease.Parameters, existing.Lease.TimeoutSeconds, existing.Lease.WorkingDirectory, existing.Lease.DeclaredSideEffects, manifestJson);

    private static async Task<IResult> ReplayMutationAsync(string action, McpOperatorConfirmationAdmission confirmation, long? scriptId, int tenantId, McpOperatorScriptContext context, IMcpOperatorScriptStore scripts, CancellationToken cancellationToken)
    {
        if (confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending) return Failure("idempotency_pending", context);
        if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded || !long.TryParse(confirmation.ResultReference, out var resultId))
            return Failure("idempotency_replay_unavailable", context);
        if (action == "delete") return Results.Ok(new McpOperatorScriptMutationResult(resultId, 0, true, true, context.CorrelationId));
        (McpOperatorScriptLease Lease, string Content, string? ManifestJson)? existing;
        try
        {
            existing = await scripts.GetOwnedAsync(resultId, tenantId, context.Request.Principal, context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
        }
        catch (McpOperatorScriptIntegrityException)
        {
            return Failure("script_integrity_invalid", context);
        }
        return existing is null
            ? Failure("idempotency_replay_unavailable", context)
            : Results.Ok(new McpOperatorScriptMutationResult(resultId, existing.Value.Lease.Version, false, true, context.CorrelationId));
    }

    private static async Task<McpOperatorScriptContextResult> TryCreateContextAsync(
        string operation, int tenantId, Guid agentId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents, IAgentCommandAuthorityDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, operation, http.TraceIdentifier);
        var hasSignedDelegation = http.TryGetMcpOperatorDelegation(out var delegation) && delegation is not null;
        if (!hasSignedDelegation && !McpOperatorLocalAgentDelegation.TryCreate(http, environment, localAgents, Tool, operation, tenantId, agentId, out delegation))
            return new(null, Failure(environment.IsProduction() && localAgents.Enabled ? "local_operator_identity_not_allowed" : "delegated_identity_required", fallback));
        var effective = delegation!;
        var access = McpOperationAccessCatalog.Find(Tool, operation);
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var operatorEnvironment, out var expectedInstance) ||
            access is null || !string.Equals(effective.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(effective.Tool, Tool, StringComparison.Ordinal) || !string.Equals(effective.Operation, operation, StringComparison.Ordinal) ||
            effective.TenantId != tenantId || effective.AgentId != agentId || string.IsNullOrWhiteSpace(effective.Resource) || string.IsNullOrWhiteSpace(effective.CorrelationId))
        {
            return new(null, Failure("delegated_identity_invalid", fallback));
        }
        var requiresExecutionGateway = operation == "run";
        var target = new ClientKey(tenantId, agentId);
        var online = !requiresExecutionGateway || (await presence.GetSnapshotAsync(target, cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var available = !requiresExecutionGateway || (options.IsCommandAuthorityActive && dispatcher.IsAvailable(target));
        var route = new McpOperatorRouteAccessRequest(operatorEnvironment,
            new McpOperatorPrincipal(effective.Identity.Subject, effective.Identity.ClientId, effective.Identity.AuthorizedParty,
                effective.Identity.Groups.ToHashSet(StringComparer.Ordinal), effective.Identity.Roles.ToHashSet(StringComparer.Ordinal), effective.Identity.Scopes.ToHashSet(StringComparer.Ordinal)),
            effective.ServicePrincipal, effective.Resource!, effective.Instance!, Tool, operation, tenantId, agentId,
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal),
            effective.CorrelationId!, effective.RequestId, online, available);
        var evaluated = await admission.EvaluateAsync(route, cancellationToken).ConfigureAwait(false);
        var context = new McpOperatorScriptContext(route, evaluated.Decision, McpOperationAccessScopeNames.Canonical(access.RequiredScope), tenantId, agentId, operation, effective.CorrelationId!);
        return evaluated.Decision.IsAllowed ? new(context, null) : new(context, Failure(evaluated.Decision.FailureCode ?? "target_policy_missing", context));
    }

    private static bool ParametersAreValid(IReadOnlyList<McpOperatorScriptParameter> schema, IReadOnlyDictionary<string, string>? parameters)
    {
        parameters ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (parameters.Count > schema.Count || parameters.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || pair.Value.Length > 1024 || pair.Value.Any(char.IsControl))) return false;
        foreach (var definition in schema)
        {
            if (definition.Required && !parameters.ContainsKey(definition.Name)) return false;
            if (!parameters.TryGetValue(definition.Name, out var value)) continue;
            if (definition.Type == "secret_reference" && !IsIdentifier(value, 128)) return false;
            if (definition.Type == "integer" && !long.TryParse(value, out _)) return false;
            if (definition.Type == "boolean" && !bool.TryParse(value, out _)) return false;
            if (definition.Type == "choice" && (definition.Options is null || !definition.Options.Contains(value, StringComparer.Ordinal))) return false;
        }
        return parameters.Keys.All(key => schema.Any(definition => definition.Name == key));
    }

    private static string MutationPayloadHash(string action, long? scriptId, long? expectedVersion, McpOperatorScriptDraft draft) => Hash(JsonSerializer.Serialize(new
    {
        action,
        scriptId,
        expectedVersion,
        draft.Name,
        draft.Description,
        draft.ShellType,
        draft.ContentHash,
        draft.Parameters,
        draft.TimeoutSeconds,
        draft.WorkingDirectory,
        draft.DeclaredSideEffects,
        draft.ManifestJson
    }));

    private static string RunPayloadHash(long scriptId, McpOperatorScriptRunRequest request) => Hash(JsonSerializer.Serialize(new
    {
        scriptId,
        request.Version,
        contentHash = request.ContentHash?.ToUpperInvariant(),
        request.Parameters
    }));

    private static string RiskSummary(McpOperatorScriptDraft draft) => draft.DeclaredSideEffects.Contains(McpOperatorScriptSideEffect.ReadOnly)
        ? "read_only"
        : string.Join(',', draft.DeclaredSideEffects.Order());

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool HasPlanCredentials(string? token, string? key) => IsOpaque(token) && IsOpaque(key);
    private static bool IsOpaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool IsIdentifier(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static void SetEtag(HttpContext http, long version) => http.Response.Headers.ETag = $"\"{version}\"";

    private static McpOperatorScriptSummary ToSummary(McpOperatorScriptLease lease) => new(
        lease.ScriptId, lease.TenantId, lease.Name, lease.Description, lease.ShellType, lease.ContentHash, lease.ManifestHash,
        lease.TimeoutSeconds, lease.WorkingDirectory, lease.DeclaredSideEffects, lease.CreatedAtUtc, lease.UpdatedAtUtc, lease.Version);

    private static McpOperatorScriptContext MinimalContext(int tenantId, Guid agentId, string operation, string correlationId)
    {
        var access = McpOperationAccessCatalog.Find(Tool, operation)!;
        return new(null!, null!, McpOperationAccessScopeNames.Canonical(access.RequiredScope), tenantId, agentId, operation, correlationId);
    }

    private static IResult Failure(string code, McpOperatorScriptContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "script_not_found" => StatusCodes.Status404NotFound,
            "script_invalid" or "script_version_or_parameters_invalid" => StatusCodes.Status400BadRequest,
            "script_integrity_invalid" or "script_source_revision_conflict" or "script_source_in_use" => StatusCodes.Status409Conflict,
            "script_dispatch_failed" => StatusCodes.Status500InternalServerError,
            "script_etag_mismatch" or "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => StatusCodes.Status409Conflict,
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
            "script_invalid" or "script_version_or_parameters_invalid" or "script_etag_mismatch" or "script_integrity_invalid" or "script_source_revision_conflict" or "script_source_in_use" => "constraint",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            _ => "policy"
        };
        return Results.Problem(statusCode: status, title: "MCP operator script access was not admitted.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false,
            ["summary"] = "MCP operator script access was not admitted.",
            ["failure"] = new
            {
                code,
                layer,
                retryable = code is "target_offline" or "capability_unavailable" or "agent_command_session_unavailable",
                requiredScopes = new[] { context.RequiredScope },
                requiredOperation = $"{Tool}/{context.Operation}",
                target = code == "tenant_not_authorized" ? null : new { context.TenantId, context.AgentId },
                remediation = code switch
                {
                    "script_source_revision_conflict" => "Reload the canonical source, review its revision and content hash, and create a new preview before confirming.",
                    "script_source_in_use" => "Remove the script's job definition references before deleting it; existing history is retained.",
                    "development_environment_access_required" => "Canonical library access requires the server-admitted full Development environment grant.",
                    "local_operator_identity_not_allowed" => "Ask an API owner to allowlist this exact local OAuth client under both M2M and NetRatel:Mcp:LocalAgent, then review its scope and target policy.",
                    _ => "Review the script ownership, ETag, target policy, and active execution gateway when applicable."
                }
            },
            ["correlationId"] = context.CorrelationId
        });
    }

    private sealed record McpOperatorScriptContext(McpOperatorRouteAccessRequest Request, McpOperatorDecision Decision, string RequiredScope, int TenantId, Guid AgentId, string Operation, string CorrelationId);
    private sealed record McpOperatorScriptContextResult(McpOperatorScriptContext? Context, IResult? Failure);
    private sealed record McpOperatorScriptRunResolution(McpOperatorScriptContext? Context, (McpOperatorScriptLease Lease, string Content, string? ManifestJson)? Script, IResult? Failure);
}

public sealed record McpOperatorScriptMutationRequest(
    McpOperatorScriptDraft? Script = null,
    long? ScriptId = null,
    long? ExpectedVersion = null,
    string? ManifestJson = null,
    string? PlanToken = null,
    string? IdempotencyKey = null,
    McpDevelopmentScriptSource? Source = null,
    long? ExpectedSourceRevision = null,
    string? ExpectedContentHash = null);

public sealed record McpOperatorScriptRunRequest(
    long Version,
    string ContentHash,
    IReadOnlyDictionary<string, string>? Parameters = null,
    string? PlanToken = null,
    string? IdempotencyKey = null);

public sealed record McpOperatorScriptSummary(long ScriptId, int TenantId, string Name, string Description, string ShellType, string ContentHash, string ManifestHash, int TimeoutSeconds, string WorkingDirectory, IReadOnlyList<McpOperatorScriptSideEffect> DeclaredSideEffects, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, long Version);
public sealed record McpOperatorScriptDetail(McpOperatorScriptSummary Script, string Content, string? ManifestJson);
public sealed record McpOperatorScriptValidation(string ContentHash, int ContentBytes, string ShellType, int TimeoutSeconds, string WorkingDirectory, IReadOnlyList<McpOperatorScriptSideEffect> DeclaredSideEffects, int ParameterCount, Guid PolicyId, long PolicyVersion, string CorrelationId);
public sealed record McpOperatorScriptPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, string Action, long? ScriptId, long? ExpectedVersion, string ContentHash, string RiskSummary, Guid PolicyId, long PolicyVersion, string CorrelationId);
public sealed record McpOperatorScriptMutationResult(long ScriptId, long Version, bool Deleted, bool Replayed, string CorrelationId);
public sealed record McpOperatorScriptRunPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, long ScriptId, long Version, string ContentHash, int TargetCount, string CorrelationId);
public sealed record McpOperatorScriptRunResult(long ScriptId, string RequestId, bool Replayed, string CorrelationId)
{
    public string? TaskId { get; init; }
}
