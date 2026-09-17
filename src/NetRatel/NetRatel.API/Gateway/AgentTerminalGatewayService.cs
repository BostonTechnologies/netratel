using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

/// <summary>Authenticated, presence-fenced agent leg for the additive terminal transport.</summary>
[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentTerminalGatewayService(
    IClientPresenceRouter presenceRouter,
    IAgentTerminalSessionRegistry terminals,
    NetRatelAkkaMigrationOptions options,
    IServiceScopeFactory scopes,
    ILogger<AgentTerminalGatewayService> logger)
    : AgentTerminalGateway.AgentTerminalGatewayBase
{
    public override async Task Connect(IAsyncStreamReader<AgentTerminalFrame> requestStream, IServerStreamWriter<GatewayTerminalFrame> responseStream, ServerCallContext context)
    {
        if (!options.IsTerminalAuthorityActive)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The terminal gateway is disabled."));
        if (!AgentGatewayIdentityResolver.TryResolve(context.GetHttpContext().User, out var identity, out var error) || identity is null)
            throw new RpcException(new Status(StatusCode.PermissionDenied, error));
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A terminal hello frame is required."));
        var session = await ValidateHello(requestStream.Current, identity, context.CancellationToken).ConfigureAwait(false);
        AgentTerminalGatewayRegistration candidate;
        try
        {
            candidate = terminals.RegisterProvisional(session.Client, session.ConnectionId, session.ConnectionEpoch, session.AvailableShells, session.Capabilities);
        }
        catch (AgentGatewayRegistrationFencedException exception)
        {
            throw new RpcException(new Status(StatusCode.Aborted, exception.Message));
        }
        using var registration = candidate;
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, registration.CompletionToken);
        try
        {
            await RequirePresenceAsync(session, streamCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (registration.CompletionToken.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The terminal gateway registration has been replaced."));
        }
        if (!registration.IsCurrent || !registration.TryActivate())
            throw new RpcException(new Status(StatusCode.Aborted, "The terminal gateway registration has been replaced."));
        logger.LogInformation(
            "api.terminal.transport.registration.created tenantId={TenantId} agentId={AgentId} connectionId={ConnectionId} connectionEpoch={ConnectionEpoch}",
            session.Client.TenantId,
            session.Client.AgentId,
            session.ConnectionId,
            session.ConnectionEpoch);
        await responseStream.WriteAsync(NewFrame(session, 0, new GatewayTerminalFrame { Accepted = new TerminalConnectAccepted { TerminalAuthority = options.PresenceAuthority, MaximumFrameBytes = 16 * 1024, MaximumInFlightFrames = 64 } }), streamCancellation.Token).ConfigureAwait(false);
        var writer = WriteAsync(registration, responseStream, streamCancellation, session);
        var reader = ReadInboundAsync(requestStream, session, registration, streamCancellation.Token);
        try
        {
            var completed = await Task.WhenAny(reader, writer).ConfigureAwait(false);
            if (completed.IsFaulted)
            {
                await completed.ConfigureAwait(false);
            }
        }
        finally
        {
            streamCancellation.Cancel();
            registration.Dispose();
            try
            {
                await Task.WhenAll(reader, writer).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (streamCancellation.IsCancellationRequested)
            {
                logger.LogDebug(
                    "api.terminal.transport.duplex.join.cancelled tenantId={TenantId} agentId={AgentId} connectionId={ConnectionId} connectionEpoch={ConnectionEpoch}",
                    session.Client.TenantId,
                    session.Client.AgentId,
                    session.ConnectionId,
                    session.ConnectionEpoch);
            }
            finally
            {
                logger.LogInformation(
                    "api.terminal.transport.registration.removed tenantId={TenantId} agentId={AgentId} connectionId={ConnectionId} connectionEpoch={ConnectionEpoch} registrationId={RegistrationId}",
                    session.Client.TenantId,
                    session.Client.AgentId,
                    session.ConnectionId,
                    session.ConnectionEpoch,
                    registration.RegistrationId);
            }
        }
    }

    private async Task ReadInboundAsync(
        IAsyncStreamReader<AgentTerminalFrame> requestStream,
        Session session,
        AgentTerminalGatewayRegistration registration,
        CancellationToken cancellationToken)
    {
        ulong last = 0;
        try
        {
            while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                if (!registration.IsCurrent)
                {
                    throw new RpcException(new Status(StatusCode.Aborted, "The terminal gateway registration has been replaced."));
                }

                var frame = requestStream.Current;
                if (!ValidFrame(frame, session, ref last)) throw new RpcException(new Status(StatusCode.InvalidArgument, "The terminal frame is invalid or stale."));
                await RequirePresenceAsync(session, cancellationToken).ConfigureAwait(false);
                var accepted = await ReceiveFrameAsync(session, registration.RegistrationId, frame, cancellationToken).ConfigureAwait(false);
                if (accepted) continue;

                if (frame.PayloadCase == AgentTerminalFrame.PayloadOneofCase.Opened &&
                    await TryRejectOpenedAsync(session, registration.RegistrationId, frame.Opened, cancellationToken).ConfigureAwait(false))
                {
                    logger.LogDebug("Rejected an unowned terminal reannouncement without aborting the admitted transport. tenantId={TenantId}, agentId={AgentId}", session.Client.TenantId, session.Client.AgentId);
                    continue;
                }

                // A per-session race (duplicate close, stale output, or an
                // already-fenced PTY) is not transport corruption. Ignore it
                // so a new server-issued terminal may still be started over
                // this admitted stream. Identity, protocol, sequence, and
                // presence-fence failures remain stream-fatal above.
                logger.LogDebug("Ignoring an unowned terminal frame without aborting the admitted transport. tenantId={TenantId}, agentId={AgentId}, kind={FrameKind}", session.Client.TenantId, session.Client.AgentId, frame.PayloadCase);
            }

            logger.LogInformation("api.terminal.transport.inbound.completed tenantId={TenantId} agentId={AgentId}", session.Client.TenantId, session.Client.AgentId);
        }
        catch (Exception exception) when (IsExpectedStreamCancellation(exception, cancellationToken))
        {
            logger.LogDebug(
                "api.terminal.transport.inbound.cancelled tenantId={TenantId} agentId={AgentId} connectionId={ConnectionId} connectionEpoch={ConnectionEpoch}",
                session.Client.TenantId,
                session.Client.AgentId,
                session.ConnectionId,
                session.ConnectionEpoch);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "api.terminal.transport.inbound.faulted tenantId={TenantId} agentId={AgentId}", session.Client.TenantId, session.Client.AgentId);
            throw;
        }
    }

    private async Task<bool> ReceiveOpenedAsync(Session session, Guid registrationId, TerminalSessionOpened opened, CancellationToken cancellationToken)
    {
        var client = session.Client;
        if (terminals is IAgentTerminalRegistrationBoundRegistry bound)
        {
            if (bound.TryReceiveOpened(client, registrationId, opened))
            {
                return true;
            }

            if (!bound.IsCurrentRegistration(client, registrationId))
            {
                return false;
            }

            if (bound is IAgentTerminalRegistrationBoundRecoveryAdmissionRegistry recoveryAdmission)
            {
                return await recoveryAdmission.TryRecoverOpenedAsync(
                    client,
                    registrationId,
                    opened,
                    admissionCancellationToken => RequirePresenceAsync(session, admissionCancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            return await bound.TryRecoverOpenedAsync(client, registrationId, opened, cancellationToken).ConfigureAwait(false);
        }
        else if (terminals.TryReceiveOpened(client, opened))
        {
            return true;
        }

        return terminals is IAgentTerminalSessionRecoveryRegistry recovery &&
               await recovery.TryRecoverOpenedAsync(client, opened, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReceiveFrameAsync(Session session, Guid registrationId, AgentTerminalFrame frame, CancellationToken cancellationToken)
    {
        var client = session.Client;
        var bound = terminals as IAgentTerminalRegistrationBoundRegistry;
        var wasAlreadyOpened = frame.PayloadCase == AgentTerminalFrame.PayloadOneofCase.Opened &&
                               terminals.Get(frame.Opened.SessionId) is { State: "opened" } existing &&
                               existing.Generation == frame.Opened.Generation;
        var accepted = frame.PayloadCase switch
        {
            AgentTerminalFrame.PayloadOneofCase.Opened => await ReceiveOpenedAsync(session, registrationId, frame.Opened, cancellationToken).ConfigureAwait(false),
            AgentTerminalFrame.PayloadOneofCase.Output => bound is not null
                ? await bound.TryReceiveOutputAsync(client, registrationId, frame.Output, cancellationToken).ConfigureAwait(false)
                : await terminals.TryReceiveOutputAsync(client, frame.Output, cancellationToken).ConfigureAwait(false),
            AgentTerminalFrame.PayloadOneofCase.ResizeApplied => bound?.TryReceiveResizeApplied(client, registrationId, frame.ResizeApplied) ?? terminals.TryReceiveResizeApplied(client, frame.ResizeApplied),
            AgentTerminalFrame.PayloadOneofCase.Closed => bound?.TryReceiveClosed(client, registrationId, frame.Closed) ?? terminals.TryReceiveClosed(client, frame.Closed),
            AgentTerminalFrame.PayloadOneofCase.Failed => bound?.TryReceiveFailed(client, registrationId, frame.Failed) ?? terminals.TryReceiveFailed(client, frame.Failed),
            _ => false
        };
        if (!accepted || frame.PayloadCase is not (AgentTerminalFrame.PayloadOneofCase.Opened or AgentTerminalFrame.PayloadOneofCase.Closed or AgentTerminalFrame.PayloadOneofCase.Failed))
            return accepted;

        if (frame.PayloadCase == AgentTerminalFrame.PayloadOneofCase.Opened)
        {
            var current = terminals.Get(frame.Opened.SessionId);
            if (wasAlreadyOpened || current is not { State: "opened" } || current.Generation != frame.Opened.Generation)
            {
                // A close-pending reannouncement is accepted only so the exact
                // cleanup Close can be delivered. It must never revive a durable
                // lease by recording a late Opened transition.
                return true;
            }
        }

        await RecordTerminalTransitionAsync(frame, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task<bool> TryRejectOpenedAsync(Session session, Guid registrationId, TerminalSessionOpened opened, CancellationToken cancellationToken) =>
        terminals is IAgentTerminalRegistrationBoundRegistry bound
            ? bound.TryRejectOpenedAsync(session.Client, registrationId, session.ConnectionId, session.ConnectionEpoch, opened, cancellationToken)
            : terminals is IAgentTerminalSessionRejectionRegistry rejection
                ? rejection.TryRejectOpenedAsync(session.Client, session.ConnectionId, session.ConnectionEpoch, opened, cancellationToken)
                : Task.FromResult(false);

    private async Task RecordTerminalTransitionAsync(AgentTerminalFrame frame, CancellationToken cancellationToken)
    {
        var (sessionId, state, reason) = frame.PayloadCase switch
        {
            AgentTerminalFrame.PayloadOneofCase.Opened => (frame.Opened.SessionId, McpOperatorTerminalSessionState.Opened, (string?)null),
            AgentTerminalFrame.PayloadOneofCase.Closed => (frame.Closed.SessionId, McpOperatorTerminalSessionState.Closed, frame.Closed.Reason),
            AgentTerminalFrame.PayloadOneofCase.Failed => (frame.Failed.SessionId, McpOperatorTerminalSessionState.Failed, frame.Failed.Code),
            _ => throw new InvalidOperationException("Only terminal lifecycle frames can be persisted.")
        };

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMcpOperatorTerminalSessionStore>();
            await store.MarkTerminalAsync(sessionId, state, string.IsNullOrWhiteSpace(reason) ? null : reason, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The gateway remains authoritative for live PTY control. A failed
            // audit write must not strand a session; expiry recovery will retry
            // idempotent close from its durable lease on the next sweep.
            logger.LogError(exception, "Could not record terminal lifecycle transition {TerminalState} for {SessionId}", state, sessionId);
        }
    }

    private async Task<Session> ValidateHello(AgentTerminalFrame frame, AuthenticatedAgentIdentity identity, CancellationToken ct)
    {
        if (frame.PayloadCase != AgentTerminalFrame.PayloadOneofCase.Hello || !string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) || frame.TenantId != identity.TenantId || !Guid.TryParse(frame.ClientId, out var id) || id != identity.AgentId || !Guid.TryParse(frame.ConnectionId, out var connection) || connection == Guid.Empty || frame.ConnectionEpoch == 0 || frame.Sequence != 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The terminal hello does not match the authenticated agent identity."));
        var terminalCapability = frame.Hello.TerminalCapability;
        if (terminalCapability is not { Supported: true })
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The terminal hello does not advertise terminal support."));
        var shells = terminalCapability.AvailableShells
            .Select(shell => shell.Trim().ToLowerInvariant())
            .Where(shell => shell is "pwsh" or "powershell" or "cmd" or "bash" or "sh" or "zsh")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (shells.Length == 0)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The terminal hello does not advertise any executable shells."));
        var client = new ClientKey(identity.TenantId, identity.AgentId);
        var session = new Session(client, connection, frame.ConnectionEpoch, shells, frame.Hello.Capabilities);
        await RequirePresenceAsync(session, ct).ConfigureAwait(false);
        return session;
    }

    private async Task RequirePresenceAsync(Session session, CancellationToken cancellationToken)
    {
        var presence = await presenceRouter.GetSnapshotAsync(session.Client, cancellationToken).ConfigureAwait(false);
        if (presence.Status != ShadowPresenceStatus.Online || presence.ConnectionId != session.ConnectionId ||
            presence.ConnectionEpoch != checked((long)session.ConnectionEpoch))
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The terminal gateway session is fenced by the active presence connection."));
        }
    }
    private bool ValidFrame(AgentTerminalFrame f, Session s, ref ulong last) { if (!string.Equals(f.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) || f.TenantId != s.Client.TenantId || !string.Equals(f.ClientId, s.Client.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) || f.ConnectionEpoch != s.ConnectionEpoch || !string.Equals(f.ConnectionId, s.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase) || f.Sequence == 0 || f.Sequence <= last) return false; last = f.Sequence; return true; }
    private static GatewayTerminalFrame NewFrame(Session s, ulong sequence, GatewayTerminalFrame f) { f.ProtocolVersion = "1.0"; f.TenantId = s.Client.TenantId; f.ClientId = s.Client.AgentId.ToString("D"); f.ConnectionEpoch = s.ConnectionEpoch; f.ConnectionId = s.ConnectionId.ToString("D"); f.Sequence = sequence; return f; }
    private async Task WriteAsync(
        AgentTerminalGatewayRegistration registration,
        IServerStreamWriter<GatewayTerminalFrame> writer,
        CancellationTokenSource streamCancellation,
        Session session)
    {
        try
        {
            await foreach (var frame in registration.Reader.ReadAllAsync(streamCancellation.Token).ConfigureAwait(false))
            {
                await writer.WriteAsync(frame).ConfigureAwait(false);
                // Start deadlines are tied to actual gRPC delivery, not to a
                // successful in-memory channel enqueue.
                registration.MarkWritten(frame);
            }

            logger.LogInformation(
                "api.terminal.transport.outbound.completed tenantId={TenantId} agentId={AgentId} connectionId={ConnectionId} connectionEpoch={ConnectionEpoch} registrationId={RegistrationId}",
                session.Client.TenantId,
                session.Client.AgentId,
                session.ConnectionId,
                session.ConnectionEpoch,
                registration.RegistrationId);
        }
        catch (OperationCanceledException) when (streamCancellation.IsCancellationRequested)
        {
            logger.LogDebug(
                "api.terminal.transport.outbound.cancelled tenantId={TenantId} agentId={AgentId} connectionId={ConnectionId} connectionEpoch={ConnectionEpoch} registrationId={RegistrationId}",
                session.Client.TenantId,
                session.Client.AgentId,
                session.ConnectionId,
                session.ConnectionEpoch,
                registration.RegistrationId);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "api.terminal.transport.outbound.faulted tenantId={TenantId} agentId={AgentId} connectionId={ConnectionId} connectionEpoch={ConnectionEpoch} registrationId={RegistrationId}",
                session.Client.TenantId,
                session.Client.AgentId,
                session.ConnectionId,
                session.ConnectionEpoch,
                registration.RegistrationId);
            streamCancellation.Cancel();
            throw;
        }
        finally
        {
            // The writer can fault while the client continues to hold its
            // inbound stream open. Remove this exact registration before the
            // duplex supervisor observes/join it so availability never points
            // at a dead outbound writer.
            registration.Dispose();
        }
    }

    private static bool IsExpectedStreamCancellation(Exception exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested &&
        (exception is OperationCanceledException || exception is RpcException { StatusCode: StatusCode.Cancelled });
    private sealed record Session(ClientKey Client, Guid ConnectionId, ulong ConnectionEpoch, IReadOnlyList<string> AvailableShells, IReadOnlyList<string> Capabilities);
}
