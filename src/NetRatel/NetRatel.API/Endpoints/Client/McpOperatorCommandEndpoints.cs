using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Production one-shot command adapter. An exact signed delegation or an
/// explicitly allowlisted local agent identity, exact target, current policy,
/// confirmation, idempotency record, and durable ownership lease are required
/// before the raw command is handed to the fenced gateway. Command text and
/// result bytes are never persisted by this route.
/// </summary>
public static class McpOperatorCommandEndpoints
{
    private const string Tool = "netratel_commands";
    private const int MaximumCommandBytes = 32 * 1024;
    private const int MaximumTimeoutSeconds = 60 * 60;
    private const int MaximumOutputBytes = 48 * 1024;

    public static IEndpointRouteBuilder MapMcpOperatorCommandEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/commands")
            .WithTags("MCP Operator Commands")
            .RequireAuthorization("M2MOnly");

        group.MapGet("/availability", AvailabilityAsync);
        group.MapPost("/preview", PreviewAsync);
        group.MapPost("/confirm", ConfirmAsync);
        group.MapGet("/{commandId}", GetAsync);
        group.MapPost("/{commandId}/cancel", CancelAsync);
        return app;
    }

    private static async Task<IResult> AvailabilityAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync(http, environment, presence, admission, options, localAgents, dispatcher, tenantId, agentId,
            "availability", cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;
        var context = admitted.Context!;
        try
        {
            var audit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorCommandAvailability(tenantId, agentId, dispatcher.IsAvailable(new ClientKey(tenantId, agentId)), audit.AuditId, context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            return Failure(rejection.FailureCode, context);
        }
    }

    private static async Task<IResult> PreviewAsync(
        int tenantId,
        Guid agentId,
        McpOperatorCommandExecuteRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        // Preview evaluates the same destructive execution operation that the
        // confirmation will dispatch, while the delegation carries the
        // distinct preview verb bound by the MCP adapter.
        var admitted = await TryCreateContextAsync(http, environment, presence, admission, options, localAgents, dispatcher, tenantId, agentId,
            "execute", cancellationToken, delegatedOperation: "preview_execute").ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;
        var context = admitted.Context!;
        if (!TryNormalize(request, context.Decision, out var command, out var failureCode))
            return Failure(failureCode, context);

        try
        {
            var plan = await confirmations.CreatePlanAsync(
                new McpOperatorConfirmationPlanRequest(context.Decision, PayloadHash(command)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorCommandPreview(
                plan.PlanToken,
                plan.IdempotencyKey,
                plan.ExpiresAtUtc,
                plan.ConfirmationClass,
                tenantId,
                agentId,
                command.Shell,
                $"sha256:{command.CommandHash.ToLowerInvariant()}; utf8_bytes:{command.CommandBytes}",
                command.WorkingDirectory,
                command.TimeoutSeconds,
                command.MaximumOutputBytes,
                context.Decision.MatchingPolicyIds.Single(),
                context.Decision.SelectedPolicyVersion!.Value,
                McpOperatorConfirmationClass.Destructive,
                1,
                context.CorrelationId));
        }
        catch (ArgumentException)
        {
            return Failure("confirmation_plan_invalid", context);
        }
    }

    private static async Task<IResult> ConfirmAsync(
        int tenantId,
        Guid agentId,
        McpOperatorCommandConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        IMcpOperatorCommandStore commands,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync(http, environment, presence, admission, options, localAgents, dispatcher, tenantId, agentId,
            "execute", cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;
        var context = admitted.Context!;
        if (!HasPlanCredentials(request.PlanToken, request.IdempotencyKey))
            return Failure("confirmation_plan_invalid", context);
        if (!TryNormalize(request, context.Decision, out var command, out var failureCode))
            return Failure(failureCode, context);

        var confirmation = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken, request.IdempotencyKey, PayloadHash(command), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, context);
        if (confirmation.IsReplay)
            return await ReplayAsync(confirmation, commands, context, cancellationToken).ConfigureAwait(false);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", context);

        McpOperatorAcceptedAudit audit;
        try
        {
            audit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }

        var commandId = Guid.NewGuid().ToString("N");
        McpOperatorCommandLease lease;
        try
        {
            lease = await commands.CreateOrGetAsync(new McpOperatorCommandCreateRequest(
                commandId,
                context.Decision,
                audit,
                idempotencyId,
                context.CorrelationId,
                command.Shell,
                command.WorkingDirectory,
                command.CommandHash,
                command.CommandBytes,
                command.EnvironmentReferences,
                command.TimeoutSeconds,
                command.MaximumOutputBytes,
                DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            await commands.RecordLifecycleAsync(lease.CommandId, tenantId, agentId, McpOperatorCommandState.Dispatched,
                DateTimeOffset.UtcNow, null, cancellationToken).ConfigureAwait(false);

            var payload = JsonSerializer.Serialize(new ExecShellCommandPayload
            {
                Preferred = ToShellExecutor(command.Shell),
                Command = command.Command,
                WorkingDirectory = command.WorkingDirectory,
                TimeoutSeconds = command.TimeoutSeconds,
                EnvironmentReferences = command.EnvironmentReferences.ToList()
            });
            await dispatcher.DispatchAsync(new ClientKey(tenantId, agentId), lease.CommandId, context.CorrelationId,
                TaskKinds.ExecShellCommand, payload, environment: 0, cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, lease.CommandId, cancellationToken).ConfigureAwait(false);
            var dispatched = await commands.GetAsync(lease.CommandId, cancellationToken).ConfigureAwait(false) ?? lease;
            return Results.Accepted(CommandLocation(tenantId, agentId, lease.CommandId), ToResult(dispatched, context.CorrelationId, replayed: false));
        }
        catch (McpOperatorCommandLimitException exception)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, exception.Code, cancellationToken).ConfigureAwait(false);
            return Failure(exception.Code, context);
        }
        catch (AgentCommandGatewaySessionUnavailableException)
        {
            await commands.RecordLifecycleAsync(commandId, tenantId, agentId, McpOperatorCommandState.Failed,
                DateTimeOffset.UtcNow, "agent_command_session_unavailable", CancellationToken.None).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "agent_command_session_unavailable", CancellationToken.None).ConfigureAwait(false);
            return Failure("agent_command_session_unavailable", context);
        }
        catch (ArgumentException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "command_policy_constraints_missing", cancellationToken).ConfigureAwait(false);
            return Failure("command_policy_constraints_missing", context);
        }
        catch (Exception)
        {
            // The dispatcher boundary deliberately does not return exception
            // detail here: it can include endpoint, task, or command content.
            // Leave a deterministic, content-free terminal lifecycle instead
            // of stranding a confirmed idempotency record in Pending.
            await commands.RecordLifecycleAsync(commandId, tenantId, agentId, McpOperatorCommandState.Failed,
                DateTimeOffset.UtcNow, "command_dispatch_failed", CancellationToken.None).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "command_dispatch_failed", CancellationToken.None).ConfigureAwait(false);
            return Failure("command_dispatch_failed", context);
        }
    }

    private static async Task<IResult> GetAsync(
        int tenantId,
        Guid agentId,
        string commandId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorCommandStore commands,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        [FromServices] IAgentCommandGatewaySessionRegistry commandSessions,
        CancellationToken cancellationToken)
    {
        var resolved = await RequireOwnedAsync("get", commandId, tenantId, agentId, http, environment, presence, admission,
            commands, options, localAgents, dispatcher, commandSessions, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is { } failure)
            return failure;
        try
        {
            await admission.RecordAcceptedAsync(resolved.Context!.Request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(ToResult(resolved.Lease!, resolved.Context.CorrelationId, replayed: false));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            return Failure(rejection.FailureCode, resolved.Context!);
        }
    }

    private static async Task<IResult> CancelAsync(
        int tenantId,
        Guid agentId,
        string commandId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorCommandStore commands,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        [FromServices] IAgentCommandGatewaySessionRegistry commandSessions,
        CancellationToken cancellationToken)
    {
        var resolved = await RequireOwnedAsync("cancel", commandId, tenantId, agentId, http, environment, presence, admission,
            commands, options, localAgents, dispatcher, commandSessions, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is { } failure)
            return failure;
        if (!commandSessions.IsAvailable(new ClientKey(tenantId, agentId)))
            return Failure("agent_command_session_unavailable", resolved.Context!);

        var lease = await commands.RequestCancelAsync(commandId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (lease is null)
            return Failure("command_not_found", resolved.Context!);
        try
        {
            await admission.RecordAcceptedAsync(resolved.Context!.Request, cancellationToken).ConfigureAwait(false);
            await commandSessions.CancelAsync(new ClientKey(tenantId, agentId), commandId, "operator_cancelled", cancellationToken).ConfigureAwait(false);
            return Results.Accepted(CommandLocation(tenantId, agentId, commandId), ToResult(lease, resolved.Context.CorrelationId, replayed: false));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            return Failure(rejection.FailureCode, resolved.Context!);
        }
        catch (AgentCommandGatewaySessionUnavailableException)
        {
            return Failure("agent_command_session_unavailable", resolved.Context!);
        }
    }

    private static async Task<McpOperatorCommandRouteResult> RequireOwnedAsync(
        string operation,
        string commandId,
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorCommandStore commands,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        [FromServices] IAgentCommandGatewaySessionRegistry commandSessions,
        CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync(http, environment, presence, admission, options, localAgents, dispatcher, tenantId, agentId,
            operation, cancellationToken).ConfigureAwait(false);
        if (admitted.Context is not { } context)
            return new(null, null, admitted.Failure!);
        var lease = await commands.GetOwnedAsync(commandId, tenantId, agentId, context.Request.Principal, context.Request.McpResource,
            context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
        {
            if (lease is not null && !IsTerminal(lease.State))
            {
                // A policy expiry or revocation must not leave a one-shot
                // command executing just because its owner next attempted a
                // status read. Persist the cancellation first, then make a
                // best-effort fenced delivery when the gateway is connected.
                await commands.RequestCancelAsync(commandId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (commandSessions.IsAvailable(new ClientKey(tenantId, agentId)))
                        await commandSessions.CancelAsync(new ClientKey(tenantId, agentId), commandId, "command_policy_revoked", CancellationToken.None).ConfigureAwait(false);
                }
                catch (AgentCommandGatewaySessionUnavailableException)
                {
                    // Repeat the durable cancellation write after the failed
                    // delivery attempt. It is idempotent and preserves the
                    // CancelRequested fence for a reconnecting gateway.
                    await commands.RequestCancelAsync(commandId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                }
            }
            return new(null, context, failure);
        }
        return lease is null
            ? new(null, context, Failure("command_not_found", context))
            : new(lease, context, null);
    }

    private static async Task<McpOperatorCommandContextResult> TryCreateContextAsync(
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        NetRatelAkkaMigrationOptions options,
        McpOperatorLocalAgentOptions localAgents,
        IAgentCommandAuthorityDispatcher dispatcher,
        int tenantId,
        Guid agentId,
        string operation,
        CancellationToken cancellationToken,
        string? delegatedOperation = null)
    {
        var fallback = MinimalContext(tenantId, agentId, operation, http.TraceIdentifier);
        var hasSignedDelegation = http.TryGetMcpOperatorDelegation(out var delegation) && delegation is not null;
        if (!hasSignedDelegation && !McpOperatorLocalAgentDelegation.TryCreate(http, environment, localAgents, Tool,
                delegatedOperation ?? operation, tenantId, agentId, out delegation))
            return new(null, Failure(environment.IsProduction() && localAgents.Enabled ? "local_operator_identity_not_allowed" : "delegated_identity_required", fallback));
        var effectiveDelegation = delegation!;
        var access = McpOperationAccessCatalog.Find(Tool, operation);
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var operatorEnvironment, out var expectedInstance) ||
            access is null ||
            !string.Equals(effectiveDelegation.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(effectiveDelegation.Tool, Tool, StringComparison.Ordinal) ||
            !string.Equals(effectiveDelegation.Operation, delegatedOperation ?? operation, StringComparison.Ordinal) ||
            effectiveDelegation.TenantId != tenantId || effectiveDelegation.AgentId != agentId ||
            string.IsNullOrWhiteSpace(effectiveDelegation.Resource) || string.IsNullOrWhiteSpace(effectiveDelegation.CorrelationId))
        {
            return new(null, Failure("delegated_identity_invalid", fallback));
        }

        var client = new ClientKey(tenantId, agentId);
        var requiresLiveGateway = operation != "get";
        var online = !requiresLiveGateway ||
            (await presence.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var available = !requiresLiveGateway || (options.IsCommandAuthorityActive && dispatcher.IsAvailable(client));
        var routeRequest = new McpOperatorRouteAccessRequest(
            operatorEnvironment,
            new McpOperatorPrincipal(effectiveDelegation.Identity.Subject, effectiveDelegation.Identity.ClientId, effectiveDelegation.Identity.AuthorizedParty,
                effectiveDelegation.Identity.Groups.ToHashSet(StringComparer.Ordinal), effectiveDelegation.Identity.Roles.ToHashSet(StringComparer.Ordinal),
                effectiveDelegation.Identity.Scopes.ToHashSet(StringComparer.Ordinal)),
            effectiveDelegation.ServicePrincipal,
            effectiveDelegation.Resource!,
            effectiveDelegation.Instance!,
            Tool,
            operation,
            tenantId,
            agentId,
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal),
            effectiveDelegation.CorrelationId!,
            effectiveDelegation.RequestId,
            online,
            available);
        var evaluated = await admission.EvaluateAsync(routeRequest, cancellationToken).ConfigureAwait(false);
        var context = new McpOperatorCommandContext(routeRequest, evaluated.Decision, McpOperationAccessScopeNames.Canonical(access.RequiredScope));
        return evaluated.Decision.IsAllowed
            ? new(context, null)
            : new(context, Failure(evaluated.Decision.FailureCode ?? "target_policy_missing", context));
    }

    private static bool TryNormalize(
        McpOperatorCommandExecuteRequest request,
        McpOperatorDecision decision,
        out McpOperatorCommand command,
        out string failure)
    {
        command = default!;
        failure = "command_invalid";
        var shell = request.Shell?.Trim().ToLowerInvariant();
        var directory = request.WorkingDirectory?.Trim();
        var constraints = decision.EffectiveConstraints;
        if (string.IsNullOrWhiteSpace(request.Command) || shell is null || directory is null || constraints is null ||
            constraints.AllowedShells is not { Count: > 0 } shells || !shells.Contains(shell, StringComparer.OrdinalIgnoreCase) ||
            constraints.WorkingDirectories is not { Count: > 0 } directories || !directories.AllowsWorkingDirectory(directory) ||
            constraints.MaxCommandDurationSeconds is not > 0 || constraints.MaxConcurrentCommands is not > 0 ||
            constraints.MaxOutputBytes is not > 0 || constraints.MaxTaskTargetCount is not > 0 || constraints.MaxFanOut is not > 0)
        {
            failure = "command_policy_constraints_missing";
            return false;
        }

        int commandBytes;
        try { commandBytes = new UTF8Encoding(false, true).GetByteCount(request.Command); }
        catch (EncoderFallbackException) { return false; }
        if (commandBytes is < 1 or > MaximumCommandBytes || request.Command.IndexOf('\0') >= 0)
            return false;

        var timeout = request.TimeoutSeconds ?? Math.Min(constraints.MaxCommandDurationSeconds.Value, MaximumTimeoutSeconds);
        var output = request.MaximumOutputBytes ?? Math.Min(constraints.MaxOutputBytes.Value, MaximumOutputBytes);
        if (timeout is < 1 or > MaximumTimeoutSeconds || timeout > constraints.MaxCommandDurationSeconds ||
            output is < 1 or > MaximumOutputBytes || output > constraints.MaxOutputBytes || !TryEnvironmentReferences(request.EnvironmentReferences, out var environmentReferences) ||
            !TryToShellExecutor(shell, out _))
        {
            return false;
        }

        command = new McpOperatorCommand(
            request.Command,
            shell,
            directory,
            environmentReferences,
            timeout,
            output,
            commandBytes,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Command))));
        return true;
    }

    private static bool TryEnvironmentReferences(IReadOnlyList<string>? values, out IReadOnlyList<string> normalized)
    {
        normalized = [];
        if (values is null)
            return true;
        if (values.Count > 32 || values.Any(value => value is not { Length: > 0 and <= 128 } ||
            (!char.IsAsciiLetter(value[0]) && value[0] != '_') ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')))
        {
            return false;
        }
        normalized = values.Select(value => value.Trim()).Order(StringComparer.Ordinal).ToArray();
        return normalized.Distinct(StringComparer.Ordinal).Count() == normalized.Count;
    }

    private static bool TryToShellExecutor(string shell, out ShellExecutor executor)
    {
        executor = shell switch
        {
            "pwsh" => ShellExecutor.Pwsh,
            "powershell" or "windows-powershell" or "windows_powershell" => ShellExecutor.WindowsPowerShell,
            "bash" or "sh" => ShellExecutor.Bash,
            "cmd" => ShellExecutor.Cmd,
            _ => ShellExecutor.Auto
        };
        return executor != ShellExecutor.Auto;
    }

    private static ShellExecutor ToShellExecutor(string shell) =>
        TryToShellExecutor(shell, out var executor) ? executor : throw new ArgumentOutOfRangeException(nameof(shell));

    private static string PayloadHash(McpOperatorCommand command) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"execute:{command.CommandHash}:{command.Shell}:{Hash(command.WorkingDirectory)}:{string.Join(',', command.EnvironmentReferences)}:{command.TimeoutSeconds}:{command.MaximumOutputBytes}")));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool HasPlanCredentials(string? token, string? key) => IsOpaqueCredential(token) && IsOpaqueCredential(key);
    private static bool IsOpaqueCredential(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool IsCommandId(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);
    private static bool IsTerminal(McpOperatorCommandState state) => state is McpOperatorCommandState.Completed or McpOperatorCommandState.Failed or McpOperatorCommandState.Cancelled;
    private static string CommandLocation(int tenantId, Guid agentId, string commandId) => $"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/commands/{commandId}";

    private static async Task<IResult> ReplayAsync(McpOperatorConfirmationAdmission confirmation, IMcpOperatorCommandStore commands, McpOperatorCommandContext context, CancellationToken cancellationToken)
    {
        if (confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending)
            return Failure("idempotency_pending", context);
        if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded || !IsCommandId(confirmation.ResultReference))
            return Failure("idempotency_replay_unavailable", context);
        var lease = await commands.GetOwnedAsync(confirmation.ResultReference!, context.TenantId, context.AgentId, context.Request.Principal,
            context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
        return lease is null
            ? Failure("idempotency_replay_unavailable", context)
            : Results.Ok(ToResult(lease, context.CorrelationId, replayed: true));
    }

    private static McpOperatorCommandResult ToResult(McpOperatorCommandLease lease, string correlationId, bool replayed) => new(
        lease.CommandId,
        lease.CommandId,
        lease.CommandId,
        lease.TenantId,
        lease.AgentId,
        lease.ShellType,
        lease.WorkingDirectory,
        lease.TimeoutSeconds,
        lease.MaximumOutputBytes,
        lease.State.ToString().ToLowerInvariant(),
        lease.FailureCode,
        lease.CreatedAtUtc,
        lease.LastUpdatedAtUtc,
        replayed,
        correlationId) { Output = lease.Output };

    private static McpOperatorCommandContext MinimalContext(int tenantId, Guid agentId, string operation, string correlationId)
    {
        var access = McpOperationAccessCatalog.Find(Tool, operation)!;
        return new(null!, null!, McpOperationAccessScopeNames.Canonical(access.RequiredScope), tenantId, agentId, operation, correlationId);
    }

    private static IResult Failure(string code, McpOperatorCommandContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "command_not_found" => StatusCodes.Status404NotFound,
            "target_offline" or "capability_unavailable" or "agent_command_session_unavailable" => StatusCodes.Status503ServiceUnavailable,
            "command_invalid" => StatusCodes.Status400BadRequest,
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "oauth_scope_missing" => "oauth_scope",
            "tenant_not_authorized" => "tenant",
            "target_not_found" or "target_disabled" or "target_offline" => "target",
            "capability_unavailable" or "agent_command_session_unavailable" => "capability",
            "command_invalid" or "command_policy_constraints_missing" or "command_concurrency_limit_reached" => "constraint",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            _ => "policy"
        };
        return Results.Problem(statusCode: status, title: "MCP operator command access was not admitted.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false,
            ["summary"] = "MCP operator command access was not admitted.",
            ["failure"] = new
            {
                code,
                layer,
                retryable = code is "target_offline" or "capability_unavailable" or "agent_command_session_unavailable",
                requiredScopes = new[] { context.RequiredScope },
                requiredOperation = $"{Tool}/{context.Operation}",
                target = code == "tenant_not_authorized" ? null : new { context.TenantId, context.AgentId },
                remediation = code == "local_operator_identity_not_allowed"
                    ? "Ask an API owner to allowlist this exact local OAuth client under both M2M and NetRatel:Mcp:LocalAgent, then review its scope and target policy."
                    : "Review the operator policy, target profile, and active command gateway session."
            },
            ["correlationId"] = context.CorrelationId
        });
    }

    private sealed record McpOperatorCommandContext(McpOperatorRouteAccessRequest Request, McpOperatorDecision Decision, string RequiredScope, int TenantId, Guid AgentId, string Operation, string CorrelationId)
    {
        public McpOperatorCommandContext(McpOperatorRouteAccessRequest request, McpOperatorDecision decision, string requiredScope)
            : this(request, decision, requiredScope, request.TenantId, request.AgentId, request.Operation, request.CorrelationId) { }
    }

    private sealed record McpOperatorCommandContextResult(McpOperatorCommandContext? Context, IResult? Failure);
    private sealed record McpOperatorCommandRouteResult(McpOperatorCommandLease? Lease, McpOperatorCommandContext? Context, IResult? Failure);
    private sealed record McpOperatorCommand(string Command, string Shell, string WorkingDirectory, IReadOnlyList<string> EnvironmentReferences, int TimeoutSeconds, int MaximumOutputBytes, int CommandBytes, string CommandHash);
}

public record McpOperatorCommandExecuteRequest(
    string Command,
    string Shell,
    string WorkingDirectory,
    IReadOnlyList<string>? EnvironmentReferences = null,
    int? TimeoutSeconds = null,
    int? MaximumOutputBytes = null);

public sealed record McpOperatorCommandConfirmRequest(
    string PlanToken,
    string IdempotencyKey,
    string Command,
    string Shell,
    string WorkingDirectory,
    IReadOnlyList<string>? EnvironmentReferences = null,
    int? TimeoutSeconds = null,
    int? MaximumOutputBytes = null)
    : McpOperatorCommandExecuteRequest(Command, Shell, WorkingDirectory, EnvironmentReferences, TimeoutSeconds, MaximumOutputBytes);

public sealed record McpOperatorCommandAvailability(int TenantId, Guid AgentId, bool Available, Guid AuditId, string CorrelationId);
public sealed record McpOperatorCommandPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, int TenantId, Guid AgentId, string Shell, string CommandSummary, string WorkingDirectory, int TimeoutSeconds, int MaximumOutputBytes, Guid PolicyId, long PolicyVersion, McpOperatorConfirmationClass RiskClass, int TargetCount, string CorrelationId);
public sealed record McpOperatorCommandResult(string CommandId, string TaskId, string ResultId, int TenantId, Guid AgentId, string Shell, string? WorkingDirectory, int TimeoutSeconds, int MaximumOutputBytes, string State, string? FailureCode, DateTimeOffset CreatedAtUtc, DateTimeOffset LastUpdatedAtUtc, bool Replayed, string CorrelationId)
{
    public McpOperatorCommandOutput? Output { get; init; }
}
