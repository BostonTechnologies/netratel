using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.API.Services;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.API.Gateway;

/// <summary>
/// Authenticated, fenced control transport for non-terminal agent operations.
/// It has a separate stream from presence and telemetry so a slow control client
/// cannot block heartbeat acknowledgement.
/// </summary>
[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentControlGatewayService(
    IClientPresenceRouter presenceRouter,
    IAgentManagementService agentManagement,
    IAgentControlSessionRegistry controlSessions,
    ILogger<AgentControlGatewayService> logger)
    : global::NetRatel.AgentGateway.Contracts.V1.AgentControlGateway.AgentControlGatewayBase
{
    public override async Task Connect(
        IAsyncStreamReader<AgentControlFrame> requestStream,
        IServerStreamWriter<GatewayControlFrame> responseStream,
        ServerCallContext context)
    {
        if (!AgentGatewayIdentityResolver.TryResolve(
                context.GetHttpContext().User,
                out var authenticatedIdentity,
                out var identityError) ||
            authenticatedIdentity is null)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, identityError));
        }

        await EnsureAgentIsActiveAsync(authenticatedIdentity, context.CancellationToken).ConfigureAwait(false);
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A control hello frame is required."));
        }

        var hello = requestStream.Current;
        var session = await ValidateHelloAsync(hello, authenticatedIdentity, context.CancellationToken).ConfigureAwait(false);
        await using var authority = await AgentGatewayAuthenticationLifetime.AttachAsync(context, session.Client, session.ConnectionId, session.ConnectionEpoch).ConfigureAwait(false);
        AgentControlSessionRegistration registration;
        try
        {
            registration = controlSessions.Register(session.Client, session.ConnectionId, session.ConnectionEpoch, provisional: true);
            registration.SetCapabilities(hello.Hello.Capabilities);
        }
        catch (AgentGatewayRegistrationFencedException exception)
        {
            throw new RpcException(new Status(StatusCode.Aborted, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, exception.Message));
        }

        using var admissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            authority.Token, registration.CompletionToken);
        try
        {
            await RequirePresenceAsync(session.Client, session.ConnectionId, session.ConnectionEpoch, admissionCancellation.Token).ConfigureAwait(false);
            if (!registration.Activate())
                throw new RpcException(new Status(StatusCode.Aborted, "The gateway registration was replaced during admission."));
            RequireCurrent(registration);
            await responseStream.WriteAsync(new GatewayControlFrame
            {
                ProtocolVersion = NetRatelAkkaOptions.ProtocolVersion,
                TenantId = session.Client.TenantId,
                ClientId = session.Client.AgentId.ToString("D"),
                ConnectionEpoch = session.ConnectionEpoch,
                ConnectionId = session.ConnectionId.ToString("D"),
                Sequence = 0,
                Accepted = new ControlAccepted
                {
                    ControlAuthority = "akka"
                }
            }, admissionCancellation.Token).ConfigureAwait(false);

            await GatewayDuplexSession.RunAsync(async cancellationToken =>
            {
                ulong lastSequence = 0;
                while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireCurrent(registration);
                    ProcessInboundFrame(requestStream.Current, session, ref lastSequence, registration);
                }
            }, cancellationToken => WriteOutboundFramesAsync(registration, responseStream, cancellationToken),
                authority.Token, registration.CompletionToken, logger, context.GetHttpContext().Abort).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (registration.CompletionToken.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The gateway registration was replaced during admission."));
        }
        finally
        {
            registration.Dispose();
        }
    }

    private async Task<ValidatedControlSession> ValidateHelloAsync(
        AgentControlFrame frame,
        AuthenticatedAgentIdentity identity,
        CancellationToken cancellationToken)
    {
        if (frame.PayloadCase != AgentControlFrame.PayloadOneofCase.Hello ||
            !string.Equals(frame.ProtocolVersion, NetRatelAkkaOptions.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != identity.TenantId ||
            !Guid.TryParse(frame.ClientId, out var clientId) || clientId != identity.AgentId ||
            !Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty ||
            frame.ConnectionEpoch == 0 ||
            frame.Sequence != 0 || frame.Hello.Capabilities.Count > 32 ||
            frame.Hello.Capabilities.Any(capability => capability.Length is < 1 or > 64 || capability.Any(char.IsControl)))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The control hello does not match the authenticated agent identity."));
        }

        var client = new ClientKey(identity.TenantId, identity.AgentId);
        var presence = await presenceRouter.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (presence.Status != ClientPresenceStatus.Online ||
            presence.ConnectionId != connectionId ||
            presence.ConnectionEpoch != checked((long)frame.ConnectionEpoch))
        {
            throw new RpcException(new Status(
                StatusCode.Aborted,
                "The control hello does not match an active authenticated presence session."));
        }

        return new ValidatedControlSession(client, connectionId, frame.ConnectionEpoch);
    }

    private async Task RequirePresenceAsync(ClientKey client, Guid connectionId, ulong connectionEpoch, CancellationToken cancellationToken)
    {
        var presence = await presenceRouter.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (presence.Status != ClientPresenceStatus.Online || presence.ConnectionId != connectionId || presence.ConnectionEpoch != checked((long)connectionEpoch))
            throw new RpcException(new Status(StatusCode.Aborted, "The control gateway session is fenced by the active presence connection."));
    }

    private void ProcessInboundFrame(
        AgentControlFrame frame,
        ValidatedControlSession session,
        ref ulong lastInboundSequence,
        AgentControlSessionRegistration registration)
    {
        if (!string.Equals(frame.ProtocolVersion, NetRatelAkkaOptions.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != session.Client.TenantId ||
            !string.Equals(frame.ClientId, session.Client.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != session.ConnectionEpoch ||
            !string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.Sequence == 0 ||
            frame.Sequence <= lastInboundSequence)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The control response is invalid or stale."));
        }

        lastInboundSequence = frame.Sequence;
        if (frame.PayloadCase == AgentControlFrame.PayloadOneofCase.ComputerUseCompletion)
        {
            if (!Guid.TryParse(frame.ComputerUseCompletion.OperationId, out var operationId) || operationId == Guid.Empty ||
                frame.ComputerUseCompletion.ResultJson.Length is 0 or > ComputerUseContract.MaximumResponseBytes)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "The computer-use result is invalid."));
            try { registration.TryCompleteComputerUse(frame.ComputerUseCompletion); }
            catch (InvalidDataException)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "The computer-use result does not match its command."));
            }
            return;
        }
        if (frame.PayloadCase == AgentControlFrame.PayloadOneofCase.UninstallAcknowledgement)
        {
            var acknowledgement = frame.UninstallAcknowledgement;
            if (!Guid.TryParse(acknowledgement.OperationId, out var operationId) || operationId == Guid.Empty || acknowledgement.FailureCode.Length > 128)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "The uninstall acknowledgement is invalid."));
            registration.TryCompleteUninstall(acknowledgement);
            return;
        }
        if (frame.PayloadCase != AgentControlFrame.PayloadOneofCase.PingResponse ||
            !Guid.TryParse(frame.PingResponse.RequestId, out var requestId) || requestId == Guid.Empty || frame.PingResponse.RespondedAtUtc is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The control response is invalid."));
        if (!registration.TryCompletePing(requestId, frame.PingResponse.RespondedAtUtc.ToDateTimeOffset()))
        {
            logger.LogDebug(
                "Ignoring late or unknown control ping response. tenantId={TenantId}, agentId={AgentId}",
                session.Client.TenantId,
                session.Client.AgentId);
        }
    }

    // The helper has no agent credential after removal. This method validates only
    // the high-entropy credential for one persisted operation, never a new session.
    [AllowAnonymous]
    public override async Task<UninstallReceiptAcknowledgement> CompleteUninstall(UninstallReceipt request, ServerCallContext context)
    {
        var offboarding = context.GetHttpContext().RequestServices.GetRequiredService<ClientOffboardingService>();
        if (!await offboarding.CompleteAsync(request, context.CancellationToken).ConfigureAwait(false))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The uninstall receipt was not accepted."));
        return new UninstallReceiptAcknowledgement { Accepted = true };
    }

    private static async Task WriteOutboundFramesAsync(
        AgentControlSessionRegistration registration,
        IServerStreamWriter<GatewayControlFrame> responseStream,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in registration.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            RequireCurrent(registration);
            await responseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task EnsureAgentIsActiveAsync(
        AuthenticatedAgentIdentity identity,
        CancellationToken cancellationToken)
    {
        AgentDetailDto? agent;
        try
        {
            agent = await agentManagement.GetAsync(identity.TenantId, identity.AgentId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Control gateway admission lookup failed. tenantId={TenantId}, agentId={AgentId}",
                identity.TenantId,
                identity.AgentId);
            throw new RpcException(new Status(StatusCode.Unavailable, "Agent admission state is unavailable."));
        }

        if (agent is null || !agent.IsEnabled || agent.RevokedAtUtc.HasValue)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The agent is not active."));
        }
    }

    private static void RequireCurrent(AgentControlSessionRegistration registration)
    {
        if (!registration.IsCurrent)
            throw new RpcException(new Status(StatusCode.Aborted, "The gateway registration is no longer current."));
    }

    private sealed record ValidatedControlSession(
        ClientKey Client,
        Guid ConnectionId,
        ulong ConnectionEpoch);
}
