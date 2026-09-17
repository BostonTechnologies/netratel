using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

/// <summary>
/// Authenticated, presence-fenced file browse and transfer transport.
/// File content is forwarded through bounded transport channels only and is
/// never copied into Akka actor state or application logs.
/// </summary>
[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentFileGatewayService(
    IClientPresenceRouter presenceRouter,
    IAgentManagementService agentManagement,
    IAgentFileGatewaySessionRegistry fileSessions,
    NetRatelAkkaMigrationOptions options,
    ILogger<AgentFileGatewayService> logger)
    : AgentFileGateway.AgentFileGatewayBase
{
    public override async Task Connect(
        IAsyncStreamReader<AgentFileFrame> requestStream,
        IServerStreamWriter<GatewayFileFrame> responseStream,
        ServerCallContext context)
    {
        if (!options.IsFileBrowseAuthorityActive)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The file gateway authority canary is disabled."));
        }

        if (!AgentGatewayIdentityResolver.TryResolve(context.GetHttpContext().User, out var identity, out var error) || identity is null)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, error));
        }

        var agent = await agentManagement.GetAsync(identity.TenantId, identity.AgentId, context.CancellationToken).ConfigureAwait(false);
        if (agent is null || !agent.IsEnabled || agent.RevokedAtUtc.HasValue)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The agent is not active."));
        }

        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A file gateway hello frame is required."));
        }

        var hello = requestStream.Current;
        var session = await ValidateHelloAsync(hello, identity, context.CancellationToken).ConfigureAwait(false);
        AgentFileGatewayRegistration registration;
        try
        {
            registration = fileSessions.Register(session.Client, session.ConnectionId, session.ConnectionEpoch, session.Capabilities, provisional: true);
        }
        catch (AgentGatewayRegistrationFencedException exception)
        {
            throw new RpcException(new Status(StatusCode.Aborted, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, exception.Message));
        }

        using (registration)
        {
            using var admissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, registration.CompletionToken);
            try
            {
                await RequirePresenceAsync(session.Client, session.ConnectionId, session.ConnectionEpoch, admissionCancellation.Token).WaitAsync(admissionCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (registration.CompletionToken.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
            {
                throw new RpcException(new Status(StatusCode.Aborted, "The gateway registration has been replaced."));
            }
            if (!registration.TryActivate())
            {
                throw new RpcException(new Status(StatusCode.Aborted, "The file gateway registration has been replaced."));
            }

            await GatewayDuplexSession.RunAsync(
                async cancellationToken =>
                {
                    ulong lastSequence = 0;
                    while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
                    {
                        if (!registration.IsCurrent) return;
                        lastSequence = await ProcessInboundAsync(requestStream.Current, session, registration, lastSequence, cancellationToken).ConfigureAwait(false);
                    }
                },
                async cancellationToken =>
                {
                    if (!registration.IsCurrent) return;
                    await responseStream.WriteAsync(CreateAccepted(session), cancellationToken).ConfigureAwait(false);
                    await WriteOutboundAsync(registration, responseStream, cancellationToken).ConfigureAwait(false);
                },
                context.CancellationToken, registration.CompletionToken, logger).ConfigureAwait(false);
        }
    }

    private async Task<ValidatedFileSession> ValidateHelloAsync(
        AgentFileFrame frame,
        AuthenticatedAgentIdentity identity,
        CancellationToken cancellationToken)
    {
        if (frame.PayloadCase != AgentFileFrame.PayloadOneofCase.Hello || frame.Sequence != 0 ||
            !string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != identity.TenantId || !Guid.TryParse(frame.ClientId, out var agentId) || agentId != identity.AgentId ||
            !Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty || frame.ConnectionEpoch == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The file gateway hello does not match the authenticated agent identity."));
        }

        var client = new ClientKey(identity.TenantId, identity.AgentId);
        await RequirePresenceAsync(client, connectionId, frame.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
        var capabilities = frame.Hello.Capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability) && capability.Length <= 128)
            .ToHashSet(StringComparer.Ordinal);
        return new ValidatedFileSession(client, connectionId, frame.ConnectionEpoch, capabilities);
    }

    private async Task<ulong> ProcessInboundAsync(
        AgentFileFrame frame,
        ValidatedFileSession session,
        AgentFileGatewayRegistration registration,
        ulong lastSequence,
        CancellationToken cancellationToken)
    {
        if (!MatchesSession(frame, session) || frame.Sequence == 0 || frame.Sequence <= lastSequence)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The file gateway frame is invalid or stale."));
        }

        await RequirePresenceAsync(session.Client, session.ConnectionId, session.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
        if (!registration.IsCurrent)
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The file gateway registration has been replaced."));
        }
        lastSequence = frame.Sequence;
        var accepted = frame.PayloadCase switch
        {
            AgentFileFrame.PayloadOneofCase.RequestAccepted => registration.TryAccept(frame.RequestAccepted),
            AgentFileFrame.PayloadOneofCase.ListPage => registration.TryAddPage(frame.ListPage),
            AgentFileFrame.PayloadOneofCase.TransferChunk => await registration.TryAddReadChunkAsync(frame.TransferChunk, cancellationToken).ConfigureAwait(false),
            AgentFileFrame.PayloadOneofCase.Metadata => registration.TrySetMetadata(frame.Metadata),
            AgentFileFrame.PayloadOneofCase.Completed => registration.TryComplete(frame.Completed),
            AgentFileFrame.PayloadOneofCase.Failed => registration.TryFail(frame.Failed),
            _ => false
        };

        if (!accepted)
        {
            logger.LogDebug("Ignoring unknown or invalid file gateway frame. tenantId={TenantId}, agentId={AgentId}, kind={FrameKind}",
                session.Client.TenantId, session.Client.AgentId, frame.PayloadCase);
        }

        return lastSequence;
    }

    private GatewayFileFrame CreateAccepted(ValidatedFileSession session) => new()
    {
        ProtocolVersion = options.ProtocolVersion,
        TenantId = session.Client.TenantId,
        ClientId = session.Client.AgentId.ToString("D"),
        ConnectionEpoch = session.ConnectionEpoch,
        ConnectionId = session.ConnectionId.ToString("D"),
        Sequence = 0,
        Accepted = new FileConnectAccepted { FileAuthority = options.PresenceAuthority }
    };

    private bool MatchesSession(AgentFileFrame frame, ValidatedFileSession session) =>
        string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) &&
        frame.TenantId == session.Client.TenantId &&
        string.Equals(frame.ClientId, session.Client.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
        frame.ConnectionEpoch == session.ConnectionEpoch &&
        string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase);

    private async Task RequirePresenceAsync(ClientKey client, Guid connectionId, ulong connectionEpoch, CancellationToken cancellationToken)
    {
        var presence = await presenceRouter.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (presence.Status != ShadowPresenceStatus.Online || presence.ConnectionId != connectionId ||
            presence.ConnectionEpoch != checked((long)connectionEpoch))
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The file gateway session is fenced by the active presence connection."));
        }
    }

    private static async Task WriteOutboundAsync(
        AgentFileGatewayRegistration registration,
        IServerStreamWriter<GatewayFileFrame> responseStream,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in registration.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!registration.IsCurrent) return;
            await responseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record ValidatedFileSession(
        ClientKey Client,
        Guid ConnectionId,
        ulong ConnectionEpoch,
        IReadOnlySet<string> Capabilities);
}
