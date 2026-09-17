using System.Net.WebSockets;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Terminals;

namespace NetRatel.API.Endpoints;

/// <summary>
/// Operator-facing adapter for the fenced terminal gRPC transport. It has no
/// SpacetimeDB or direct-tunnel dependency: when the authority flag is off,
/// these V2 routes do not exist from the caller's perspective.
/// </summary>
public static class AgentTerminalGatewayEndpoints
{
    public static IEndpointRouteBuilder MapAgentTerminalGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var agents = app.MapGroup("/api/v2/agents/{tenantId:int}/{agentId:guid}/terminal")
            .WithTags("Gateway Terminal")
            .RequireAuthorization("Operator");
        agents.MapPost("/sessions", OpenAsync);

        var sessions = app.MapGroup("/api/v2/gateway-terminal/{sessionId}")
            .WithTags("Gateway Terminal")
            .RequireAuthorization("Operator");
        sessions.MapGet("", GetAsync);
        sessions.MapPost("/close", CloseAsync);
        sessions.MapPost("/attachment/renew", RenewAttachmentAsync);
        sessions.MapPost("/stdin", SendInputAsync);
        sessions.MapGet("/stdin/ws", StreamInputWebSocketAsync);
        sessions.MapPost("/resize", ResizeAsync);
        sessions.MapGet("/stream", StreamAsync);
        return app;
    }

    private static async Task<IResult> OpenAsync(
        int tenantId,
        Guid agentId,
        OpenTerminalRequest request,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentTerminalSessionRegistry terminals,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsTerminalAuthorityActive)
        {
            return Results.NotFound();
        }

        var shell = request.ShellType?.Trim() ?? string.Empty;

        var columns = Math.Clamp(request.Cols ?? 120, 40, 300);
        var rows = Math.Clamp(request.Rows ?? 32, 10, 120);
        try
        {
            var session = await terminals.OpenAsync(
                new ClientKey(tenantId, agentId), shell, request.WorkingDirectory, columns, rows, cancellationToken)
                .ConfigureAwait(false);
            BrowserAttachments(services)?.Track(session);
            return Results.Accepted($"/api/v2/gateway-terminal/{session.SessionId}", new TerminalOpenResponse(NewTrackingId(), session.SessionId, "Akka terminal opening."));
        }
        catch (TerminalGatewayActionException exception) { return GatewayFailure(exception); }
        catch (ArgumentException exception) { return Results.BadRequest(new TerminalActionResponse(NewTrackingId(), exception.Message)); }
    }

    private static IResult GetAsync(
        string sessionId,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentTerminalSessionRegistry terminals,
        IServiceProvider services)
    {
        if (!options.IsTerminalAuthorityActive)
        {
            return Results.NotFound();
        }

        if (terminals.Get(sessionId) is not { } session)
        {
            return Results.NotFound();
        }

        // Reading state never renews the attachment; it only supplies the
        // opaque current fence needed by a browser-executed heartbeat.
        return Results.Ok(ToDto(session, BrowserAttachments(services)?.GetAttachmentLeaseId(session)));
    }

    private static async Task<IResult> CloseAsync(string sessionId, CloseTerminalRequest? request, NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!options.IsTerminalAuthorityActive) return Results.NotFound();
        if (terminals.Get(sessionId) is not { } session) return Results.NotFound();
        BrowserAttachments(services)?.MarkClosePending(session);
        try
        {
            await terminals.CloseAsync(sessionId, session.Generation, request?.Reason ?? "operator_closed", cancellationToken).ConfigureAwait(false);
            return Results.Accepted($"/api/v2/gateway-terminal/{sessionId}", new TerminalActionResponse(NewTrackingId(), "Close requested.", sessionId));
        }
        catch (TerminalGatewayActionException exception) { return GatewayFailure(exception, sessionId); }
    }

    /// <summary>
    /// Extends a browser attachment only after JavaScript has executed a
    /// heartbeat callback over the live Blazor circuit. The caller supplies the
    /// generation and attachment fences it observed so a stale tab cannot
    /// prolong a replacement PTY or another browser runtime's ownership.
    /// </summary>
    private static IResult RenewAttachmentAsync(
        string sessionId,
        TerminalAttachmentRenewalRequest request,
        NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentTerminalSessionRegistry terminals,
        IServiceProvider services)
    {
        if (!options.IsTerminalAuthorityActive)
        {
            return Results.NotFound();
        }

        if (request.Generation == 0 ||
            string.IsNullOrWhiteSpace(request.AttachmentLeaseId) ||
            string.IsNullOrWhiteSpace(request.BrowserAttachmentId) ||
            request.AttachmentLeaseId.Length > 128 ||
            request.BrowserAttachmentId.Length > 128)
        {
            return Results.BadRequest(new TerminalActionResponse(
                NewTrackingId(),
                "A terminal generation and browser attachment fences are required.",
                sessionId,
                "terminal_browser_attachment_invalid"));
        }

        if (terminals.Get(sessionId) is not { } session)
        {
            return Results.NotFound();
        }

        var attachments = BrowserAttachments(services);
        if (attachments is null)
        {
            return Results.NotFound();
        }

        var renewal = attachments.TryRenew(
            session,
            request.Generation,
            request.AttachmentLeaseId,
            request.BrowserAttachmentId,
            request.ClaimOwnership);
        return renewal.Outcome switch
        {
            GatewayTerminalBrowserAttachmentRenewalOutcome.Renewed => Results.Accepted(
                $"/api/v2/gateway-terminal/{sessionId}",
                new TerminalActionResponse(NewTrackingId(), "Browser attachment renewed.", sessionId)
                {
                    AttachmentLeaseId = renewal.AttachmentLeaseId
                }),
            GatewayTerminalBrowserAttachmentRenewalOutcome.StaleGeneration => GatewayFailure(
                new TerminalGatewayActionException("terminal_browser_attachment_stale_generation", "The browser attachment belongs to a superseded terminal generation."),
                sessionId),
            GatewayTerminalBrowserAttachmentRenewalOutcome.StaleAttachment => GatewayFailure(
                new TerminalGatewayActionException("terminal_browser_attachment_stale", "The browser attachment has been replaced by a newer browser runtime."),
                sessionId),
            GatewayTerminalBrowserAttachmentRenewalOutcome.ClosePending => GatewayFailure(
                new TerminalGatewayActionException("terminal_browser_attachment_closing", "The browser attachment is already closing."),
                sessionId),
            GatewayTerminalBrowserAttachmentRenewalOutcome.Final => GatewayFailure(
                new TerminalGatewayActionException(
                    session.State == "failed" ? "terminal_session_failed" : "terminal_session_closed",
                    "The terminal session is final and cannot be renewed."),
                sessionId),
            _ => GatewayFailure(
                new TerminalGatewayActionException("terminal_browser_attachment_not_found", "No active browser attachment lease exists for this terminal session."),
                sessionId)
        };
    }

    private static async Task<IResult> SendInputAsync(string sessionId, TerminalInputRequest request, NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, CancellationToken cancellationToken)
    {
        if (!options.IsTerminalAuthorityActive) return Results.NotFound();
        if (string.IsNullOrEmpty(request?.Data)) return Results.BadRequest(new TerminalActionResponse(NewTrackingId(), "Input data is required.", sessionId));
        if (terminals.Get(sessionId) is not { } session) return Results.NotFound();
        try
        {
            await terminals.SendInputAsync(sessionId, session.Generation, Encoding.UTF8.GetBytes(request.Data), cancellationToken).ConfigureAwait(false);
            return Results.Accepted($"/api/v2/gateway-terminal/{sessionId}", new TerminalActionResponse(NewTrackingId(), "Input queued.", sessionId));
        }
        catch (TerminalGatewayActionException exception) { return GatewayFailure(exception, sessionId); }
        catch (ArgumentException exception) { return Results.BadRequest(new TerminalActionResponse(NewTrackingId(), exception.Message, sessionId)); }
    }

    private static async Task<IResult> ResizeAsync(string sessionId, TerminalResizeRequest request, NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, CancellationToken cancellationToken)
    {
        if (!options.IsTerminalAuthorityActive) return Results.NotFound();
        if (terminals.Get(sessionId) is not { } session) return Results.NotFound();
        try
        {
            await terminals.ResizeAsync(sessionId, session.Generation, request.Cols, request.Rows, cancellationToken).ConfigureAwait(false);
            return Results.Accepted($"/api/v2/gateway-terminal/{sessionId}", new TerminalActionResponse(NewTrackingId(), "Resize queued.", sessionId));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Results.BadRequest(new TerminalActionResponse(NewTrackingId(), exception.Message, sessionId));
        }
        catch (TerminalGatewayActionException exception) { return GatewayFailure(exception, sessionId); }
    }

    private static async Task<IResult> StreamInputWebSocketAsync(string sessionId, HttpContext httpContext, NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("NetRatel.API.TerminalInputWebSocket");
        if (!options.IsTerminalAuthorityActive) return Results.NotFound();
        if (!httpContext.WebSockets.IsWebSocketRequest) return Results.BadRequest(new TerminalActionResponse(NewTrackingId(), "Expected a websocket request.", sessionId));
        if (terminals.Get(sessionId) is not { } session) return Results.NotFound();
        if (session.State is "suspended") return GatewayFailure(new TerminalGatewayActionException("terminal_transport_reconnecting", "The terminal transport is reconnecting."), sessionId);
        if (session.State is "opening" or "requested") return GatewayFailure(new TerminalGatewayActionException("terminal_opening", "The terminal session has not opened yet."), sessionId);
        if (session.State is "closing" or "closed" or "failed") return GatewayFailure(new TerminalGatewayActionException(session.State == "failed" ? "terminal_session_failed" : "terminal_session_closed", "The terminal session is not input-capable."), sessionId);
        WebSocket socket;
        try
        {
            socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || httpContext.RequestAborted.IsCancellationRequested)
        {
            // A browser can disappear between the route checks and the HTTP
            // upgrade. Treat that request-abort race like an ordinary socket
            // detach instead of allowing it to reach notification middleware.
            logger.LogDebug("api.terminal.stdin.websocket.upgrade-cancelled sessionId={SessionId}", sessionId);
            return Results.Empty;
        }
        catch (WebSocketException exception) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug(exception, "api.terminal.stdin.websocket.upgrade-aborted sessionId={SessionId}", sessionId);
            return Results.Empty;
        }

        using var socketLease = socket;
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseSocketIfOpenAsync(socket, result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, "terminal-client-closed", logger, cancellationToken).ConfigureAwait(false);
                    break;
                }
                if (result.MessageType != WebSocketMessageType.Text || result.Count == 0 || !result.EndOfMessage)
                {
                    await CloseSocketIfOpenAsync(socket, WebSocketCloseStatus.InvalidPayloadData, "terminal-input-frame-invalid", logger, cancellationToken).ConfigureAwait(false);
                    break;
                }

                try
                {
                    await terminals.SendInputAsync(sessionId, session.Generation, buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
                }
                catch (TerminalGatewayActionException exception) when (IsRecoverableTransportLoss(exception))
                {
                    // This is the reported race: after upgrade, a terminal
                    // stream handoff must produce a clear reconnect signal,
                    // not an exception that reaches notification middleware.
                    await CloseSocketIfOpenAsync(socket, WebSocketCloseStatus.EndpointUnavailable, exception.Code, logger, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (TerminalGatewayActionException exception)
                {
                    await CloseSocketIfOpenAsync(socket, WebSocketCloseStatus.NormalClosure, exception.Code, logger, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("api.terminal.stdin.websocket.cancelled sessionId={SessionId}", sessionId);
        }
        catch (WebSocketException exception) when (httpContext.RequestAborted.IsCancellationRequested || socket.State is WebSocketState.Aborted or WebSocketState.Closed)
        {
            logger.LogDebug(exception, "api.terminal.stdin.websocket.closed-by-client sessionId={SessionId}", sessionId);
        }
        return Results.Empty;
    }

    private static async Task StreamAsync(string sessionId, HttpResponse response, NetRatelAkkaMigrationOptions options, [FromServices] IAgentTerminalSessionRegistry terminals, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("NetRatel.API.TerminalStream");
        if (!options.IsTerminalAuthorityActive || terminals.Get(sessionId) is not { } session)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        GatewayTerminalOutputSubscription subscription;
        try
        {
            // Subscribe before the first response write. If a tombstone is
            // removed in the small window after Get(), the endpoint can still
            // return a normal status rather than throwing after SSE starts.
            subscription = terminals.Subscribe(sessionId, session.Generation);
        }
        catch (TerminalGatewayActionException exception)
        {
            response.StatusCode = exception.Code == "terminal_session_not_found"
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status409Conflict;
            logger.LogDebug(exception, "api.terminal.stream.subscription.unavailable sessionId={SessionId} code={Code}", sessionId, exception.Code);
            return;
        }

        using (subscription)
        {
            var lastState = session.State;
            var nextKeepAliveAtUtc = DateTimeOffset.UtcNow.AddSeconds(10);
            try
            {
                response.ContentType = "text/event-stream";
                response.Headers.CacheControl = "no-store";
                response.Headers["X-Accel-Buffering"] = "no";
                await WriteEventAsync(response, new TerminalStreamMessage("status", Reason: $"terminal-stream-connected:akka-gateway:{session.State}"), cancellationToken).ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Cancel and observe the losing waiter on every poll. Leaving
                    // a new WaitToRead task behind for every SSE keepalive leaks
                    // waiters for a detached browser and can eventually make a
                    // terminal's output path appear permanently backpressured.
                    using var raceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var waitForOutput = subscription.Reader.WaitToReadAsync(raceCancellation.Token).AsTask();
                    var poll = Task.Delay(TimeSpan.FromSeconds(1), raceCancellation.Token);
                    var completed = await Task.WhenAny(waitForOutput, poll).ConfigureAwait(false);
                    raceCancellation.Cancel();

                    if (completed == poll)
                    {
                        await ObserveCancelledWaiterAsync(waitForOutput).ConfigureAwait(false);
                        if (DateTimeOffset.UtcNow >= nextKeepAliveAtUtc)
                        {
                            nextKeepAliveAtUtc = DateTimeOffset.UtcNow.AddSeconds(10);
                            await response.WriteAsync(": terminal-keepalive\n\n", cancellationToken).ConfigureAwait(false);
                            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await ObserveCancelledWaiterAsync(poll).ConfigureAwait(false);
                        if (!await waitForOutput.ConfigureAwait(false)) break;
                        while (subscription.Reader.TryRead(out var output))
                        {
                            await WriteEventAsync(response, new TerminalStreamMessage("data", Encoding.UTF8.GetString(output.Span), "stdout"), cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (terminals.Get(sessionId) is not { } latest)
                    {
                        break;
                    }

                    if (!string.Equals(lastState, latest.State, StringComparison.OrdinalIgnoreCase))
                    {
                        lastState = latest.State;
                        await WriteEventAsync(response, new TerminalStreamMessage("status", Reason: $"terminal-stream-state:akka-gateway:{latest.State}"), cancellationToken).ConfigureAwait(false);
                    }

                    if (latest.State is "closed" or "failed") break;
                }

                if (terminals.Get(sessionId) is { State: "failed" } failed)
                {
                    await WriteEventAsync(response, new TerminalStreamMessage("error", Reason: failed.FailureCode ?? "terminal_agent_rejected"), cancellationToken).ConfigureAwait(false);
                }
                else if (terminals.Get(sessionId) is { State: "closed" } closed)
                {
                    await WriteEventAsync(response, new TerminalStreamMessage("close", Reason: closed.FailureMessage ?? closed.State), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || response.HttpContext.RequestAborted.IsCancellationRequested)
            {
                // A disconnected browser is a normal SSE detach. The bounded
                // registry output policy prevents it from affecting lifecycle IO.
                logger.LogDebug("api.terminal.stream.cancelled sessionId={SessionId}", sessionId);
            }
            catch (IOException exception) when (response.HttpContext.RequestAborted.IsCancellationRequested || IsClientDisconnect(exception))
            {
                // Kestrel can surface a peer reset as IOException before
                // RequestAborted is signalled. This is limited to known socket
                // disconnects; unexpected response write faults still escape.
                logger.LogDebug(exception, "api.terminal.stream.disconnected sessionId={SessionId}", sessionId);
            }
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, TerminalStreamMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await response.WriteAsync($"event: terminal\ndata: {payload}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ObserveCancelledWaiterAsync(Task waiter)
    {
        try
        {
            await waiter.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!waiter.IsCanceled)
            {
                throw;
            }
        }
    }

    private static TerminalSessionDto ToDto(GatewayTerminalSession session, string? attachmentLeaseId = null) => new(
        session.SessionId,
        session.AgentId.ToString("N"),
        session.ShellType,
        session.State == "opened" ? "active" : session.State,
        HasWebSocket: true,
        session.CreatedAtUtc.ToUnixTimeSeconds(),
        DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        CloseReason: session.FailureMessage,
        Backend: "agent-pty",
        session.Columns,
        session.Rows,
        ["akka-gateway", session.Authority],
        TerminalTransportKind.AkkaGateway,
        session.FailureCode)
    {
        Generation = session.Generation,
        AttachmentLeaseId = attachmentLeaseId
    };

    private static IResult GatewayFailure(TerminalGatewayActionException exception, string? sessionId = null)
    {
        var response = new TerminalActionResponse(NewTrackingId(), exception.Message, sessionId, exception.Code);
        return exception.Code switch
        {
            "terminal_shell_unavailable" => Results.UnprocessableEntity(response),
            "terminal_session_not_found" => Results.NotFound(response),
            "terminal_transport_reconnecting" or "terminal_transport_unavailable" or "terminal_transport_backpressured" => new TerminalGatewayFailureResult(response, StatusCodes.Status503ServiceUnavailable, retryAfterSeconds: 1),
            _ => Results.Conflict(response)
        };
    }

    private static bool IsRecoverableTransportLoss(TerminalGatewayActionException exception) =>
        exception.Code is "terminal_transport_reconnecting" or "terminal_transport_unavailable" or "terminal_transport_backpressured";

    private static bool IsClientDisconnect(IOException exception) =>
        exception.InnerException is SocketException
        {
            SocketErrorCode: SocketError.ConnectionAborted or SocketError.ConnectionReset or SocketError.Shutdown
        } || exception.Message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase);

    private static GatewayTerminalBrowserAttachmentLeaseRegistry? BrowserAttachments(IServiceProvider services) =>
        services.GetService(typeof(GatewayTerminalBrowserAttachmentLeaseRegistry)) as GatewayTerminalBrowserAttachmentLeaseRegistry;

    private static async Task CloseSocketIfOpenAsync(WebSocket socket, WebSocketCloseStatus status, string reason, ILogger logger, CancellationToken cancellationToken)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var closeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            closeCancellation.CancelAfter(TimeSpan.FromSeconds(2));
            // Complete the close handshake when the browser is responsive,
            // while the bounded token prevents a dead peer from retaining the
            // upgraded request indefinitely.
            await socket.CloseAsync(status, reason[..Math.Min(reason.Length, 96)], closeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("api.terminal.stdin.websocket.close.cancelled status={Status}", status);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("api.terminal.stdin.websocket.close.timed-out status={Status}", status);
        }
        catch (WebSocketException exception)
        {
            logger.LogDebug(exception, "api.terminal.stdin.websocket.close-raced status={Status}", status);
        }
    }

    private sealed class TerminalGatewayFailureResult(TerminalActionResponse response, int statusCode, int? retryAfterSeconds = null) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            if (retryAfterSeconds is { } retryAfter)
            {
                httpContext.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await httpContext.Response.WriteAsJsonAsync(response, cancellationToken: httpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    private static string NewTrackingId() => Guid.NewGuid().ToString("N");
}
