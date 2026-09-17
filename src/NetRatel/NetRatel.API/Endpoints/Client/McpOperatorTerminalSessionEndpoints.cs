using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Production terminal adapter. A session is first bound to a confirmation,
/// policy snapshot, signed or explicitly allowlisted local operator identity,
/// target and durable lease; the gateway only transports bounded bytes for
/// that already-owned session.
/// </summary>
public static class McpOperatorTerminalSessionEndpoints
{
    private const string Tool = "netratel_terminal";
    private const int MaximumInputBytes = 16 * 1024;
    private const int MaximumWindowSeconds = 15;
    private const int MaximumRecords = 100;
    private const int MaximumRecordBytes = 16 * 1024;
    private const int MaximumTerminalLifetimeSeconds = 8 * 60 * 60;
    private const int MaximumTerminalIdleSeconds = 60 * 60;

    public static IEndpointRouteBuilder MapMcpOperatorTerminalSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/terminal")
            .WithTags("MCP Operator Terminal")
            .RequireAuthorization("M2MOnly");

        group.MapPost("/sessions/preview", PreviewOpenAsync);
        group.MapPost("/sessions/confirm", ConfirmOpenAsync);
        group.MapGet("/sessions/{sessionId}", GetAsync);
        group.MapPost("/sessions/{sessionId}/input", SendInputAsync);
        group.MapGet("/sessions/{sessionId}/stream-window", StreamWindowAsync);
        group.MapPost("/sessions/{sessionId}/resize", ResizeAsync);
        group.MapPost("/sessions/{sessionId}/close", CloseAsync);
        group.MapGet("/sessions/{sessionId}/diagnostics", DiagnosticsAsync);
        return app;
    }

    private static async Task<IResult> PreviewOpenAsync(
        int tenantId,
        Guid agentId,
        McpOperatorTerminalOpenPreviewRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentTerminalSessionRegistry terminals,
        CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync(http, environment, presence, admission, options, terminals, tenantId, agentId, "open", cancellationToken, "preview_open").ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (!TryNormalizeOpen(request, context.Decision, out var open, out var reason))
            return Failure(reason, context);

        try
        {
            var plan = await confirmations.CreatePlanAsync(
                new McpOperatorConfirmationPlanRequest(context.Decision, PayloadHash(open)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorTerminalOpenPreview(
                plan.PlanToken,
                plan.IdempotencyKey,
                plan.ExpiresAtUtc,
                plan.ConfirmationClass,
                tenantId,
                agentId,
                open.Shell,
                open.Columns,
                open.Rows,
                context.Decision.EffectiveConstraints!.MaxTerminalIdleSeconds!.Value,
                context.Decision.EffectiveConstraints.MaxTerminalLifetimeSeconds!.Value,
                context.CorrelationId));
        }
        catch (ArgumentException)
        {
            return Failure("confirmation_plan_invalid", context);
        }
    }

    private static async Task<IResult> ConfirmOpenAsync(
        int tenantId,
        Guid agentId,
        McpOperatorTerminalOpenConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        IMcpOperatorTerminalSessionStore sessions,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentTerminalSessionRegistry terminals,
        CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync(http, environment, presence, admission, options, terminals, tenantId, agentId, "open", cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (!HasPlanCredentials(request.PlanToken, request.IdempotencyKey))
            return Failure("confirmation_plan_invalid", context);
        if (!TryNormalizeOpen(request, context.Decision, out var open, out var reason))
            return Failure(reason, context);

        var confirmation = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken, request.IdempotencyKey, PayloadHash(open), context.Decision),
            cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, context);
        if (confirmation.IsReplay)
            return await ReplayOpenAsync(confirmation, sessions, context, cancellationToken).ConfigureAwait(false);
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

        var now = DateTimeOffset.UtcNow;
        var constraints = context.Decision.EffectiveConstraints!;
        var sessionId = Guid.NewGuid().ToString("N");
        McpOperatorTerminalSessionLease lease;
        try
        {
            lease = await sessions.CreateOrGetAsync(new McpOperatorTerminalSessionCreateRequest(
                sessionId,
                context.Decision,
                audit,
                idempotencyId,
                open.Shell,
                open.WorkingDirectory,
                open.Columns,
                open.Rows,
                now,
                now.AddSeconds(constraints.MaxTerminalIdleSeconds!.Value),
                now.AddSeconds(constraints.MaxTerminalLifetimeSeconds!.Value)), cancellationToken).ConfigureAwait(false);
        }
        catch (McpOperatorTerminalSessionLimitException exception)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, exception.Code, cancellationToken).ConfigureAwait(false);
            return Failure(exception.Code, context);
        }
        catch (ArgumentException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "terminal_policy_constraints_missing", cancellationToken).ConfigureAwait(false);
            return Failure("terminal_policy_constraints_missing", context);
        }

        if (terminals is not IAgentTerminalSessionLeaseRegistry leasedTerminals)
        {
            await sessions.MarkTerminalAsync(lease.SessionId, McpOperatorTerminalSessionState.Failed, "terminal_gateway_unavailable", now, cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "terminal_gateway_unavailable", cancellationToken).ConfigureAwait(false);
            return Failure("capability_unavailable", context);
        }

        try
        {
            var gateway = await leasedTerminals.OpenWithSessionIdAsync(
                new ClientKey(tenantId, agentId),
                lease.SessionId,
                lease.Generation,
                open.Shell,
                open.WorkingDirectory,
                open.Columns,
                open.Rows,
                cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, lease.SessionId, cancellationToken).ConfigureAwait(false);
            return Results.Accepted(SessionLocation(tenantId, agentId, lease.SessionId), ToSession(gateway, lease, context.CorrelationId, replayed: false));
        }
        catch (TerminalGatewayActionException exception)
        {
            await sessions.MarkTerminalAsync(lease.SessionId, McpOperatorTerminalSessionState.Failed, exception.Code, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, exception.Code, cancellationToken).ConfigureAwait(false);
            return GatewayFailure(exception.Code, context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await sessions.MarkTerminalAsync(lease.SessionId, McpOperatorTerminalSessionState.Failed, "terminal_open_cancelled", DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "terminal_open_cancelled", CancellationToken.None).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
    }

    private static async Task<IResult> GetAsync(
        int tenantId, Guid agentId, string sessionId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorTerminalSessionStore sessions,
        NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, CancellationToken cancellationToken)
    {
        var resolved = await RequireOwnedAsync("get", sessionId, tenantId, agentId, http, environment, presence, admission, sessions, options, terminals, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is { } failure) return failure;
        try { await admission.RecordAcceptedAsync(resolved.Context!.Request, cancellationToken).ConfigureAwait(false); }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, resolved.Context!); }
        return Results.Ok(ToSession(terminals.Get(sessionId), resolved.Lease!, resolved.Context!.CorrelationId, replayed: false));
    }

    private static async Task<IResult> DiagnosticsAsync(
        int tenantId, Guid agentId, string sessionId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorTerminalSessionStore sessions,
        NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, CancellationToken cancellationToken)
    {
        var resolved = await RequireOwnedAsync("diagnostics", sessionId, tenantId, agentId, http, environment, presence, admission, sessions, options, terminals, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is { } failure) return failure;
        try { await admission.RecordAcceptedAsync(resolved.Context!.Request, cancellationToken).ConfigureAwait(false); }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, resolved.Context!); }
        var gateway = terminals.Get(sessionId);
        return Results.Ok(new McpOperatorTerminalDiagnostics(
            sessionId, resolved.Lease!.State.ToString().ToLowerInvariant(), gateway?.State ?? "not_attached",
            resolved.Lease.CreatedAtUtc, resolved.Lease.LastActivityAtUtc, resolved.Lease.IdleExpiresAtUtc,
            resolved.Lease.ExpiresAtUtc, resolved.Lease.CloseReason, Context: "mcp-operator-policy", resolved.Context!.CorrelationId));
    }

    private static async Task<IResult> SendInputAsync(
        int tenantId, Guid agentId, string sessionId, McpOperatorTerminalInputRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorTerminalSessionStore sessions, IMcpOperatorTerminalActionStore actions,
        NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, CancellationToken cancellationToken)
    {
        if (!TryEncodeInput(request.Input, out var input))
            return GatewayFailure("terminal_input_invalid", MinimalContext(tenantId, agentId, "send_input", http.TraceIdentifier));
        var resolved = await RequireOwnedAsync("send_input", sessionId, tenantId, agentId, http, environment, presence, admission, sessions, options, terminals, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is { } failure) return failure;
        var context = resolved.Context!;
        var action = await actions.AdmitAsync(new McpOperatorTerminalActionAdmissionRequest(
            sessionId, "send_input", context.Request.RequestId, PayloadHash(input), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        if (action.FailureCode is { } actionFailure) return Failure(actionFailure, context);
        if (action.IsReplay) return ReplayAction(action, tenantId, agentId, sessionId, context);
        if (!action.IsNewDispatch || action.ActionId is not { } actionId) return Failure("idempotency_replay_unavailable", context);

        var lease = await sessions.TouchAsync(sessionId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (lease is null || lease.State is McpOperatorTerminalSessionState.Closing or McpOperatorTerminalSessionState.Closed or McpOperatorTerminalSessionState.Failed)
        {
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Failed, "terminal_session_expired", null, cancellationToken).ConfigureAwait(false);
            return Failure("terminal_session_expired", resolved.Context!);
        }
        McpOperatorAcceptedAudit? audit = null;
        try
        {
            audit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            await terminals.SendInputAsync(sessionId, lease.Generation, input, cancellationToken).ConfigureAwait(false);
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Succeeded, "input_queued", audit.AuditId, cancellationToken).ConfigureAwait(false);
            return Results.Accepted(SessionLocation(tenantId, agentId, sessionId), new McpOperatorTerminalAction(sessionId, "input_queued", context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, audit?.AuditId, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }
        catch (TerminalGatewayActionException exception)
        {
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Failed, exception.Code, audit?.AuditId, cancellationToken).ConfigureAwait(false);
            return GatewayFailure(exception.Code, context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Failed, "terminal_action_cancelled", audit?.AuditId, CancellationToken.None).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
    }

    private static async Task<IResult> ResizeAsync(
        int tenantId, Guid agentId, string sessionId, McpOperatorTerminalResizeRequest request, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorTerminalSessionStore sessions, IMcpOperatorTerminalActionStore actions,
        NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, CancellationToken cancellationToken)
    {
        if (request.Columns is < 40 or > 300 || request.Rows is < 10 or > 120)
            return GatewayFailure("terminal_resize_invalid", MinimalContext(tenantId, agentId, "resize", http.TraceIdentifier));
        var resolved = await RequireOwnedAsync("resize", sessionId, tenantId, agentId, http, environment, presence, admission, sessions, options, terminals, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is { } failure) return failure;
        var context = resolved.Context!;
        var action = await actions.AdmitAsync(new McpOperatorTerminalActionAdmissionRequest(
            sessionId, "resize", context.Request.RequestId, PayloadHash($"resize:{request.Columns}:{request.Rows}"), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        if (action.FailureCode is { } actionFailure) return Failure(actionFailure, context);
        if (action.IsReplay) return ReplayAction(action, tenantId, agentId, sessionId, context);
        if (!action.IsNewDispatch || action.ActionId is not { } actionId) return Failure("idempotency_replay_unavailable", context);

        var lease = await sessions.TouchAsync(sessionId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (lease is null || lease.State is McpOperatorTerminalSessionState.Closing or McpOperatorTerminalSessionState.Closed or McpOperatorTerminalSessionState.Failed)
        {
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Failed, "terminal_session_expired", null, cancellationToken).ConfigureAwait(false);
            return Failure("terminal_session_expired", context);
        }
        McpOperatorAcceptedAudit? audit = null;
        try
        {
            audit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            await terminals.ResizeAsync(sessionId, lease.Generation, request.Columns, request.Rows, cancellationToken).ConfigureAwait(false);
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Succeeded, "resize_queued", audit.AuditId, cancellationToken).ConfigureAwait(false);
            return Results.Accepted(SessionLocation(tenantId, agentId, sessionId), new McpOperatorTerminalAction(sessionId, "resize_queued", context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, audit?.AuditId, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }
        catch (TerminalGatewayActionException exception)
        {
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Failed, exception.Code, audit?.AuditId, cancellationToken).ConfigureAwait(false);
            return GatewayFailure(exception.Code, context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Failed, "terminal_action_cancelled", audit?.AuditId, CancellationToken.None).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
    }

    private static async Task<IResult> CloseAsync(
        int tenantId, Guid agentId, string sessionId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorTerminalSessionStore sessions, IMcpOperatorTerminalActionStore actions,
        NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync(http, environment, presence, admission, options, terminals, tenantId, agentId, "close", cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
        {
            // Once an owned lease is closing, the durable expiry worker owns
            // retries. A terminal's brief disconnect while it acknowledges the
            // initial close must not turn a duplicate close into a 503.
            if (admitted.Context is { } unavailableContext &&
                unavailableContext.Decision.FailureCode is "target_offline" or "capability_unavailable")
            {
                var unavailableLease = await sessions.GetOwnedAsync(sessionId, tenantId, agentId, unavailableContext.Request.Principal,
                    unavailableContext.Request.McpResource, unavailableContext.Request.McpInstance, cancellationToken).ConfigureAwait(false);
                if (unavailableLease is not null && IsCloseCompleteOrPending(unavailableLease))
                    return CloseCompletion(tenantId, agentId, sessionId, unavailableContext.CorrelationId, unavailableLease.State);
            }

            return failure;
        }

        var context = admitted.Context!;
        var ownedLease = await sessions.GetOwnedAsync(sessionId, tenantId, agentId, context.Request.Principal,
            context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
        if (ownedLease is null)
        {
            // Closing an already-absent session is intentionally successful.
            // This preserves close idempotency without exposing whether the
            // supplied opaque session ID ever existed or belonged to another caller.
            try
            {
                await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
                return CloseCompletion(tenantId, agentId, sessionId, context.CorrelationId, state: null);
            }
            catch (McpOperatorAdmissionRejectedException rejection)
            {
                return Failure(rejection.FailureCode, context);
            }
        }

        if (IsCloseCompleteOrPending(ownedLease))
        {
            try
            {
                await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
                return CloseCompletion(tenantId, agentId, sessionId, context.CorrelationId, ownedLease.State);
            }
            catch (McpOperatorAdmissionRejectedException rejection)
            {
                return Failure(rejection.FailureCode, context);
            }
        }

        var action = await actions.AdmitAsync(new McpOperatorTerminalActionAdmissionRequest(
            sessionId, "close", context.Request.RequestId, PayloadHash($"close:{sessionId}"), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        if (action.FailureCode is { } actionFailure) return Failure(actionFailure, context);
        if (action.IsReplay) return ReplayAction(action, tenantId, agentId, sessionId, context);
        if (!action.IsNewDispatch || action.ActionId is not { } actionId) return Failure("idempotency_replay_unavailable", context);

        var lease = await sessions.RequestCloseAsync(sessionId, "operator_requested_close", DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Failed, "terminal_session_not_found", null, cancellationToken).ConfigureAwait(false);
            return Failure("terminal_session_not_found", context);
        }
        McpOperatorAcceptedAudit? audit = null;
        try
        {
            audit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            // RequestCloseAsync intentionally transitions an active lease to
            // Closing before we dispatch to the gateway, making recovery safe
            // across a transport or API failure. Dispatch every close-pending
            // lease: the negotiated terminal close is idempotent, and this
            // includes the first close request after that transition.
            if (lease.State == McpOperatorTerminalSessionState.Closing)
                await terminals.CloseAsync(lease.SessionId, lease.Generation, lease.CloseReason!, cancellationToken).ConfigureAwait(false);
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Succeeded, "close_queued", audit.AuditId, cancellationToken).ConfigureAwait(false);
            return Results.Accepted(SessionLocation(tenantId, agentId, sessionId), new McpOperatorTerminalAction(sessionId, "close_queued", context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, audit?.AuditId, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }
        catch (TerminalGatewayActionException exception)
        {
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Failed, exception.Code, audit?.AuditId, cancellationToken).ConfigureAwait(false);
            return GatewayFailure(exception.Code, context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await actions.CompleteAsync(actionId, McpOperatorIdempotencyOutcome.Failed, "terminal_action_cancelled", audit?.AuditId, CancellationToken.None).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
    }

    private static async Task<IResult> StreamWindowAsync(
        int tenantId, Guid agentId, string sessionId, int? windowSeconds, int? maxRecords, string? afterSequence, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorTerminalSessionStore sessions,
        NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, CancellationToken cancellationToken)
    {
        var cursor = 0UL;
        if (windowSeconds is < 1 or > MaximumWindowSeconds || maxRecords is < 1 or > MaximumRecords ||
            (afterSequence is not null && !ulong.TryParse(afterSequence, NumberStyles.None, CultureInfo.InvariantCulture, out cursor)))
            return GatewayFailure("terminal_stream_invalid", MinimalContext(tenantId, agentId, "stream_window", http.TraceIdentifier));
        var resolved = await RequireOwnedAsync("stream_window", sessionId, tenantId, agentId, http, environment, presence, admission, sessions, options, terminals, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is { } failure) return failure;
        var lease = await sessions.TouchAsync(sessionId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (lease is null || lease.State == McpOperatorTerminalSessionState.Closing) return Failure("terminal_session_expired", resolved.Context!);
        try
        {
            await admission.RecordAcceptedAsync(resolved.Context!.Request, cancellationToken).ConfigureAwait(false);
            if (terminals is not IAgentTerminalOutputReplayRegistry replay)
                return Failure("terminal_gateway_unavailable", resolved.Context);
            var output = await replay.ReadOutputWindowAsync(sessionId, lease.Generation, cursor, maxRecords ?? 50,
                Math.Min(lease.EffectiveConstraints.MaxOutputBytes ?? 0, MaximumRecordBytes * MaximumRecords),
                TimeSpan.FromSeconds(windowSeconds ?? 5), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorTerminalStreamWindow(sessionId,
                output.Records.Select(record => Encoding.UTF8.GetString(record.Content.Span)).ToArray(),
                windowSeconds ?? 5, maxRecords ?? 50, resolved.Context.CorrelationId)
            {
                NextSequence = output.NextSequence.ToString(CultureInfo.InvariantCulture),
                HasMore = output.HasMore,
                Gap = output.Gap,
                Completed = output.Completed
            });
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, resolved.Context!); }
        catch (TerminalGatewayActionException exception) { return GatewayFailure(exception.Code, resolved.Context!); }
    }

    private static async Task<McpOperatorTerminalRouteResult> RequireOwnedAsync(
        string operation, string sessionId, int tenantId, Guid agentId, HttpContext http, IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission, IMcpOperatorTerminalSessionStore sessions,
        NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, CancellationToken cancellationToken)
    {
        var admitted = await TryCreateContextAsync(http, environment, presence, admission, options, terminals, tenantId, agentId, operation, cancellationToken).ConfigureAwait(false);
        if (admitted.Context is not { } context)
            return new(null, null, admitted.Failure!);
        if (admitted.Failure is { } failure)
        {
            // A policy edit must not let an already-open lease keep accepting
            // input. The signed caller is still sufficient to find only its
            // own durable lease; mark that lease close-pending before returning
            // the current-policy denial.
            var revokedLease = await sessions.GetOwnedAsync(sessionId, tenantId, agentId, context.Request.Principal,
                context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
            if (revokedLease is not null && operation is "send_input" or "resize" or "stream_window")
                await StopPolicyRevokedLeaseAsync(revokedLease, sessions, terminals, cancellationToken).ConfigureAwait(false);
            return new(null, context, failure);
        }
        var lease = await sessions.GetOwnedAsync(sessionId, tenantId, agentId, context.Request.Principal, context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
        return lease is null ? new(null, context, Failure("terminal_session_not_found", context)) : new(lease, context, null);
    }

    private static async Task<McpOperatorTerminalContextResult> TryCreateContextAsync(
        HttpContext http, IHostEnvironment environment, [FromServices] IClientPresenceRouter presence, IMcpOperatorRouteAdmission admission,
        NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, int tenantId, Guid agentId,
        string operation, CancellationToken cancellationToken, string? delegatedOperation = null)
    {
        var fallback = MinimalContext(tenantId, agentId, operation, http.TraceIdentifier);
        var localAgents = http.RequestServices.GetService<McpOperatorLocalAgentOptions>() ?? new McpOperatorLocalAgentOptions();
        var hasSignedDelegation = http.TryGetMcpOperatorDelegation(out var delegation) && delegation is not null;
        if (!hasSignedDelegation && !McpOperatorLocalAgentDelegation.TryCreate(http, environment, localAgents, Tool,
                delegatedOperation ?? operation, tenantId, agentId, out delegation))
        {
            return new(null, Failure(environment.IsProduction() && localAgents.Enabled ? "local_operator_identity_not_allowed" : "delegated_identity_required", fallback));
        }
        var effectiveDelegation = delegation!;
        var hasOperatorEnvironment = McpOperatorRuntimeEnvironment.TryResolve(environment, out var operatorEnvironment, out var expectedInstance);
        var access = McpOperationAccessCatalog.Find(Tool, operation);
        if (!hasOperatorEnvironment || access is null ||
            !string.Equals(effectiveDelegation.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(effectiveDelegation.Tool, Tool, StringComparison.Ordinal) ||
            !string.Equals(effectiveDelegation.Operation, delegatedOperation ?? operation, StringComparison.Ordinal) ||
            effectiveDelegation.TenantId != tenantId || effectiveDelegation.AgentId != agentId ||
            string.IsNullOrWhiteSpace(effectiveDelegation.Resource) || string.IsNullOrWhiteSpace(effectiveDelegation.CorrelationId))
        {
            return new(null, Failure("delegated_identity_invalid", fallback));
        }
        var online = (await presence.GetSnapshotAsync(new ClientKey(tenantId, agentId), cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var availability = terminals.GetAvailability(new ClientKey(tenantId, agentId));
        var request = new McpOperatorRouteAccessRequest(
            operatorEnvironment,
            new McpOperatorPrincipal(effectiveDelegation.Identity.Subject, effectiveDelegation.Identity.ClientId, effectiveDelegation.Identity.AuthorizedParty,
                effectiveDelegation.Identity.Groups.ToHashSet(StringComparer.Ordinal), effectiveDelegation.Identity.Roles.ToHashSet(StringComparer.Ordinal), effectiveDelegation.Identity.Scopes.ToHashSet(StringComparer.Ordinal)),
            effectiveDelegation.ServicePrincipal, effectiveDelegation.Resource!, effectiveDelegation.Instance!, Tool, operation, tenantId, agentId,
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal), effectiveDelegation.CorrelationId!, effectiveDelegation.RequestId,
            online, options.IsTerminalAuthorityActive && availability is { SupportsIdempotentClose: true });
        var evaluated = await admission.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        var context = new McpOperatorTerminalContext(request, evaluated.Decision, McpOperationAccessScopeNames.Canonical(access.RequiredScope));
        return evaluated.Decision.IsAllowed
            ? new(context, null)
            : new(context, Failure(evaluated.Decision.FailureCode ?? "target_policy_missing", context));
    }

    private static async Task StopPolicyRevokedLeaseAsync(
        McpOperatorTerminalSessionLease lease,
        IMcpOperatorTerminalSessionStore sessions,
        [FromServices] IAgentTerminalSessionRegistry terminals,
        CancellationToken cancellationToken)
    {
        var closing = await sessions.RequestCloseAsync(lease.SessionId, "terminal_policy_revoked", DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (closing is null)
            return;
        try
        {
            await terminals.CloseAsync(closing.SessionId, closing.Generation, closing.CloseReason!, cancellationToken).ConfigureAwait(false);
        }
        catch (TerminalGatewayActionException)
        {
            // The persisted closing state is the durable retry authority; the
            // expiry worker or reconnect path will issue the negotiated close.
            return;
        }
    }

    private static bool TryNormalizeOpen(McpOperatorTerminalOpenRequest request, McpOperatorDecision decision, out McpOperatorTerminalOpen open, out string failure)
    {
        open = default!;
        failure = "terminal_open_invalid";
        var shell = request.Shell?.Trim().ToLowerInvariant();
        var directory = request.WorkingDirectory?.Trim();
        var columns = request.Columns ?? 120;
        var rows = request.Rows ?? 32;
        var constraints = decision.EffectiveConstraints;
        if (shell is null || directory is null || columns is < 40 or > 300 || rows is < 10 or > 120 ||
            constraints?.AllowedShells is not { Count: > 0 } shells || !shells.Contains(shell, StringComparer.OrdinalIgnoreCase) ||
            constraints.WorkingDirectories is not { Count: > 0 } directories || !directories.AllowsWorkingDirectory(directory) ||
            constraints.MaxTerminalIdleSeconds is not > 0 and <= MaximumTerminalIdleSeconds ||
            constraints.MaxTerminalLifetimeSeconds is not > 0 and <= MaximumTerminalLifetimeSeconds ||
            constraints.MaxConcurrentTerminalSessions is not > 0 || constraints.MaxOutputBytes is not > 0)
        {
            failure = "terminal_policy_constraints_missing";
            return false;
        }
        open = new(shell, directory, columns, rows);
        return true;
    }

    private static bool TryEncodeInput(string? input, out byte[] bytes)
    {
        bytes = [];
        if (input is null || input.Length == 0 || input.Length > MaximumInputBytes ||
            input.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            return false;
        try { bytes = new UTF8Encoding(false, true).GetBytes(input); return bytes.Length is > 0 and <= MaximumInputBytes; }
        catch (EncoderFallbackException) { return false; }
    }

    private static string PayloadHash(McpOperatorTerminalOpen open) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"open:{open.Shell}:{Hash(open.WorkingDirectory)}:{open.Columns}:{open.Rows}")));
    private static string PayloadHash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    private static string PayloadHash(ReadOnlySpan<byte> payload) => Convert.ToHexString(SHA256.HashData(payload));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool HasPlanCredentials(string? token, string? key) => IsOpaqueCredential(token) && IsOpaqueCredential(key);
    private static bool IsOpaqueCredential(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static string SessionLocation(int tenantId, Guid agentId, string sessionId) => $"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/terminal/sessions/{sessionId}";

    private static bool IsCloseCompleteOrPending(McpOperatorTerminalSessionLease lease) =>
        lease.State is McpOperatorTerminalSessionState.Closing or McpOperatorTerminalSessionState.Closed or McpOperatorTerminalSessionState.Failed;

    private static IResult CloseCompletion(
        int tenantId,
        Guid agentId,
        string sessionId,
        string correlationId,
        McpOperatorTerminalSessionState? state) =>
        Results.Accepted(SessionLocation(tenantId, agentId, sessionId),
            new McpOperatorTerminalAction(sessionId,
                state == McpOperatorTerminalSessionState.Closing ? "close_pending" : "close_already_complete",
                correlationId));

    private static async Task<IResult> ReplayOpenAsync(McpOperatorConfirmationAdmission confirmation, IMcpOperatorTerminalSessionStore sessions, McpOperatorTerminalContext context, CancellationToken cancellationToken)
    {
        if (confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending) return Failure("idempotency_pending", context);
        if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded || !IsSessionId(confirmation.ResultReference)) return Failure("idempotency_replay_unavailable", context);
        var lease = await sessions.GetOwnedAsync(confirmation.ResultReference!, context.TenantId, context.AgentId, context.Request.Principal, context.Request.McpResource, context.Request.McpInstance, cancellationToken).ConfigureAwait(false);
        return lease is null ? Failure("idempotency_replay_unavailable", context) : Results.Ok(ToSession(null, lease, context.CorrelationId, replayed: true));
    }

    private static IResult ReplayAction(
        McpOperatorTerminalActionAdmission action,
        int tenantId,
        Guid agentId,
        string sessionId,
        McpOperatorTerminalContext context)
    {
        if (action.Outcome == McpOperatorIdempotencyOutcome.Pending)
            return Failure("idempotency_pending", context);
        if (action.Outcome == McpOperatorIdempotencyOutcome.Failed && action.ResultReference is { } failureCode)
            return Failure(failureCode, context);
        return action is { Outcome: McpOperatorIdempotencyOutcome.Succeeded, ResultReference: "input_queued" or "resize_queued" or "close_queued" }
            ? Results.Accepted(SessionLocation(tenantId, agentId, sessionId), new McpOperatorTerminalAction(sessionId, action.ResultReference!, context.CorrelationId) { Replayed = true })
            : Failure("idempotency_replay_unavailable", context);
    }

    private static bool IsSessionId(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);
    private static McpOperatorTerminalSessionResult ToSession(GatewayTerminalSession? gateway, McpOperatorTerminalSessionLease lease, string correlationId, bool replayed) => new(
        lease.SessionId, lease.TenantId, lease.AgentId, lease.ShellType, gateway?.State ?? lease.State.ToString().ToLowerInvariant(),
        gateway?.Columns ?? lease.Columns, gateway?.Rows ?? lease.Rows, lease.CreatedAtUtc, lease.IdleExpiresAtUtc, lease.ExpiresAtUtc, replayed, correlationId);
    private static McpOperatorTerminalContext MinimalContext(int tenantId, Guid agentId, string operation, string correlationId)
    {
        var access = McpOperationAccessCatalog.Find(Tool, operation)!;
        return new(null!, null!, McpOperationAccessScopeNames.Canonical(access.RequiredScope), tenantId, agentId, operation, correlationId);
    }
    private static IResult GatewayFailure(string code, McpOperatorTerminalContext context) => Failure(code, context);
    private static IResult Failure(string code, McpOperatorTerminalContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "terminal_stream_invalid" => StatusCodes.Status400BadRequest,
            "terminal_output_cursor_invalid" or "terminal_opening" => StatusCodes.Status409Conflict,
            "target_not_found" or "terminal_session_not_found" => StatusCodes.Status404NotFound,
            "target_offline" or "capability_unavailable" or "terminal_gateway_unavailable" => StatusCodes.Status503ServiceUnavailable,
            "terminal_output_limit_exceeded" => StatusCodes.Status413PayloadTooLarge,
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" or "terminal_session_collision" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "local_operator_identity_not_allowed" => "authentication",
            "oauth_scope_missing" => "oauth_scope",
            "target_not_found" or "target_disabled" or "target_offline" => "target",
            "capability_unavailable" or "terminal_gateway_unavailable" => "capability",
            "terminal_policy_constraints_missing" or "terminal_session_limit_reached" or "terminal_input_invalid" or "terminal_resize_invalid" or "terminal_output_limit_exceeded" or "terminal_stream_invalid" => "constraint",
            "terminal_output_cursor_invalid" or "terminal_opening" => "terminal",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            _ => "policy"
        };
        return Results.Problem(statusCode: status, title: "MCP operator terminal access was not admitted.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false,
            ["summary"] = "MCP operator terminal access was not admitted.",
            ["failure"] = new
            {
                code,
                layer,
                retryable = code is "target_offline" or "capability_unavailable" or "terminal_opening",
                requiredScopes = new[] { context.RequiredScope },
                requiredOperation = $"{Tool}/{context.Operation}",
                target = code == "tenant_not_authorized" ? null : new { context.TenantId, context.AgentId },
                remediation = code switch
                {
                    "local_operator_identity_not_allowed" => "Ask an API owner to allowlist this exact local OAuth client under both M2M and NetRatel:Mcp:LocalAgent, then review its scope and target policy.",
                    "terminal_output_cursor_invalid" => "The cursor is ahead of this session's output, possibly after API recovery. Omit afterSequence to restart from retained output and report any gap.",
                    "terminal_stream_invalid" => "Use windowSeconds from 1 through 15, maxRecords from 1 through 100, and the decimal-string nextSequence as afterSequence.",
                    "terminal_opening" => "Poll netratel_terminal/get until state is opened, then retry input or resize with a new request.",
                    _ => "Review the operator policy, target profile, and active terminal gateway session."
                }
            },
            ["correlationId"] = context.CorrelationId
        });
    }

    private sealed record McpOperatorTerminalContext(McpOperatorRouteAccessRequest Request, McpOperatorDecision Decision, string RequiredScope, int TenantId, Guid AgentId, string Operation, string CorrelationId)
    {
        public McpOperatorTerminalContext(McpOperatorRouteAccessRequest request, McpOperatorDecision decision, string requiredScope)
            : this(request, decision, requiredScope, request.TenantId, request.AgentId, request.Operation, request.CorrelationId) { }
    }
    private sealed record McpOperatorTerminalContextResult(McpOperatorTerminalContext? Context, IResult? Failure);
    private sealed record McpOperatorTerminalRouteResult(McpOperatorTerminalSessionLease? Lease, McpOperatorTerminalContext? Context, IResult? Failure);
    private sealed record McpOperatorTerminalOpen(string Shell, string WorkingDirectory, int Columns, int Rows);
}

public sealed record McpOperatorTerminalOpenPreviewRequest(string Shell, string WorkingDirectory, int? Columns = null, int? Rows = null) : McpOperatorTerminalOpenRequest(Shell, WorkingDirectory, Columns, Rows);
public sealed record McpOperatorTerminalOpenConfirmRequest(string PlanToken, string IdempotencyKey, string Shell, string WorkingDirectory, int? Columns = null, int? Rows = null) : McpOperatorTerminalOpenRequest(Shell, WorkingDirectory, Columns, Rows);
public record McpOperatorTerminalOpenRequest(string Shell, string WorkingDirectory, int? Columns = null, int? Rows = null);
public sealed record McpOperatorTerminalInputRequest(string Input);
public sealed record McpOperatorTerminalResizeRequest(int Columns, int Rows);
public sealed record McpOperatorTerminalOpenPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, int TenantId, Guid AgentId, string Shell, int Columns, int Rows, int MaxIdleSeconds, int MaxLifetimeSeconds, string CorrelationId);
public sealed record McpOperatorTerminalSessionResult(string SessionId, int TenantId, Guid AgentId, string Shell, string State, int Columns, int Rows, DateTimeOffset CreatedAtUtc, DateTimeOffset IdleExpiresAtUtc, DateTimeOffset ExpiresAtUtc, bool Replayed, string CorrelationId);
public sealed record McpOperatorTerminalAction(string SessionId, string Status, string CorrelationId)
{
    public bool Replayed { get; init; }
}
public sealed record McpOperatorTerminalStreamWindow(string SessionId, IReadOnlyList<string> Records, int WindowSeconds, int MaxRecords, string CorrelationId)
{
    public string NextSequence { get; init; } = "0";
    public bool HasMore { get; init; }
    public bool Gap { get; init; }
    public bool Completed { get; init; }
}
public sealed record McpOperatorTerminalDiagnostics(string SessionId, string LeaseState, string GatewayState, DateTimeOffset CreatedAtUtc, DateTimeOffset LastActivityAtUtc, DateTimeOffset IdleExpiresAtUtc, DateTimeOffset ExpiresAtUtc, string? CloseReason, string Context, string CorrelationId);
