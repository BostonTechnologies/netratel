using Grpc.Core;
using Google.Protobuf;
using Microsoft.AspNetCore.Authorization;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.RemoteSupport;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.RemoteSupport;
using V2Negotiation = NetRatel.Shared.Contracts.RemoteSupport.RemoteSupportV2NegotiationEnvelope;

namespace NetRatel.API.Gateway;

/// <summary>
/// Authenticated, fenced agent leg of the V2 remote-support signalling broker.
/// This service carries signalling only; it never carries terminal/PTY data or
/// WebRTC media frames.
/// </summary>
[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentRemoteSupportGatewayService(
    IClientPresenceRouter presenceRouter,
    IAgentManagementService agentManagement,
    IServiceProvider serviceProvider,
    NetRatelAkkaMigrationOptions options,
    TimeProvider timeProvider,
    ILogger<AgentRemoteSupportGatewayService> logger)
    : AgentRemoteSupportGateway.AgentRemoteSupportGatewayBase
{
    public override async Task Connect(
        IAsyncStreamReader<AgentRemoteSupportFrame> requestStream,
        IServerStreamWriter<GatewayRemoteSupportFrame> responseStream,
        ServerCallContext context)
    {
        if (!options.IsLegacyRemoteSupportGatewayActive && !options.IsRemoteSupportV2ReplicaSafeEdgeActive)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The remote-support authority canary is disabled."));
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
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A remote-support gateway hello frame is required."));
        }

        var session = await ValidateHelloAsync(requestStream.Current, identity, context.CancellationToken).ConfigureAwait(false);
        if (options.IsRemoteSupportV2ReplicaSafeEdgeActive)
        {
            await ConnectReplicaSafeAsync(requestStream, responseStream, context, session).ConfigureAwait(false);
            return;
        }

        var supportSessions = serviceProvider.GetRequiredService<IGatewayRemoteSupportSessionRegistry>();
        AgentRemoteSupportGatewayRegistration registration;
        try
        {
            registration = supportSessions.Register(session.Client, session.ConnectionId, session.ConnectionEpoch);
        }
        catch (InvalidOperationException exception)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, exception.Message));
        }

        try
        {
            await responseStream.WriteAsync(new GatewayRemoteSupportFrame
            {
                ProtocolVersion = options.ProtocolVersion,
                TenantId = session.Client.TenantId,
                ClientId = session.Client.AgentId.ToString("D"),
                ConnectionEpoch = session.ConnectionEpoch,
                ConnectionId = session.ConnectionId.ToString("D"),
                Sequence = 0,
                Accepted = new RemoteSupportConnectAccepted { SupportAuthority = options.PresenceAuthority }
            }).ConfigureAwait(false);

            var writer = WriteOutboundAsync(registration.Reader, responseStream, context.CancellationToken);
            try
            {
                ulong lastSequence = 0;
                while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
                {
                    lastSequence = await ProcessInboundAsync(requestStream.Current, session, lastSequence, supportSessions, context.CancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                registration.Dispose();
                await writer.ConfigureAwait(false);
            }
        }
        finally
        {
            registration.Dispose();
        }
    }

    private async Task<ValidatedSupportSession> ValidateHelloAsync(AgentRemoteSupportFrame frame, AuthenticatedAgentIdentity identity, CancellationToken cancellationToken)
    {
        if (frame.PayloadCase != AgentRemoteSupportFrame.PayloadOneofCase.Hello || frame.Sequence != 0 ||
            !string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) || frame.TenantId != identity.TenantId ||
            !Guid.TryParse(frame.ClientId, out var agentId) || agentId != identity.AgentId ||
            !Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty || frame.ConnectionEpoch == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The remote-support gateway hello does not match the authenticated agent identity."));
        }

        var client = new ClientKey(identity.TenantId, identity.AgentId);
        await RequirePresenceAsync(client, connectionId, frame.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
        return new ValidatedSupportSession(client, connectionId, frame.ConnectionEpoch);
    }

    private async Task<ulong> ProcessInboundAsync(AgentRemoteSupportFrame frame, ValidatedSupportSession session, ulong lastSequence, IGatewayRemoteSupportSessionRegistry supportSessions, CancellationToken cancellationToken)
    {
        if (!Matches(frame, session) || frame.Sequence == 0 || frame.Sequence <= lastSequence)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The remote-support gateway frame is invalid or stale."));
        }

        await RequirePresenceAsync(session.Client, session.ConnectionId, session.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
        lastSequence = frame.Sequence;
        var accepted = frame.PayloadCase switch
        {
            AgentRemoteSupportFrame.PayloadOneofCase.Signal => supportSessions.TryReceiveAgentSignal(session.Client, frame.Signal),
            AgentRemoteSupportFrame.PayloadOneofCase.Closed => supportSessions.TryReceiveAgentClose(session.Client, frame.Closed),
            _ => false
        };
        if (!accepted)
        {
            logger.LogDebug("Ignoring invalid remote-support signalling frame. tenantId={TenantId}, agentId={AgentId}, kind={FrameKind}",
                session.Client.TenantId, session.Client.AgentId, frame.PayloadCase);
        }

        return lastSequence;
    }

    private async Task ConnectReplicaSafeAsync(
        IAsyncStreamReader<AgentRemoteSupportFrame> requestStream,
        IServerStreamWriter<GatewayRemoteSupportFrame> responseStream,
        ServerCallContext context,
        ValidatedSupportSession session)
    {
        var edges = serviceProvider.GetRequiredService<IRemoteSupportV2AgentEdgeRegistry>();
        using var registration = edges.Register(session.Client, session.ConnectionId, session.ConnectionEpoch);
        await responseStream.WriteAsync(new GatewayRemoteSupportFrame
        {
            ProtocolVersion = options.ProtocolVersion,
            TenantId = session.Client.TenantId,
            ClientId = session.Client.AgentId.ToString("D"),
            ConnectionEpoch = session.ConnectionEpoch,
            ConnectionId = session.ConnectionId.ToString("D"),
            Sequence = 0,
            Accepted = new RemoteSupportConnectAccepted { SupportAuthority = options.PresenceAuthority }
        }).ConfigureAwait(false);

        var writer = WriteReplicaSafeOutboundAsync(registration.Reader, responseStream, session, context.CancellationToken);
        var renewal = RenewReplicaSafeEdgeAsync(registration, context.CancellationToken);
        try
        {
            ulong lastSequence = 0;
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var frame = requestStream.Current;
                if (!Matches(frame, session) || frame.Sequence == 0 || frame.Sequence <= lastSequence)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "The remote-support V2 edge frame is invalid or stale."));
                }

                await RequirePresenceAsync(session.Client, session.ConnectionId, session.ConnectionEpoch, context.CancellationToken).ConfigureAwait(false);
                lastSequence = frame.Sequence;
                var accepted = frame.PayloadCase switch
                {
                    AgentRemoteSupportFrame.PayloadOneofCase.V2EdgeRegistration =>
                        TryGetSession(frame.V2EdgeRegistration.Session, session.Client, out var supportSession) &&
                        frame.V2EdgeRegistration.RouteGeneration is > 0 and <= long.MaxValue &&
                        await registration.RegisterAsync(supportSession!, checked((long)frame.V2EdgeRegistration.RouteGeneration), context.CancellationToken).ConfigureAwait(false),
                    AgentRemoteSupportFrame.PayloadOneofCase.V2Envelope =>
                        options.IsRemoteSupportV2MediaActive &&
                        ((TryMapNegotiation(frame.V2Envelope, session.Client, out var negotiation, out var edgeRouteId, out var routeGeneration) &&
                          await registration.RouteAsync(negotiation!, edgeRouteId, routeGeneration, context.CancellationToken).ConfigureAwait(false)) ||
                         (TryMapTransitionEvidence(frame.V2Envelope, session.Client, out var evidence, out edgeRouteId, out routeGeneration) &&
                          await registration.RouteEvidenceAsync(evidence!, edgeRouteId, routeGeneration, context.CancellationToken).ConfigureAwait(false))),
                    _ => false
                };
                if (!accepted)
                {
                    throw new RpcException(new Status(StatusCode.Aborted, "The remote-support V2 edge envelope is fenced or invalid."));
                }
            }
        }
        finally
        {
            registration.Dispose();
            try
            {
                await Task.WhenAll(writer, renewal).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Remote-support V2 edge stream cancelled for tenant {TenantId}, agent {AgentId}.",
                    session.Client.TenantId, session.Client.AgentId);
            }
        }
    }

    private bool Matches(AgentRemoteSupportFrame frame, ValidatedSupportSession session) =>
        string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) && frame.TenantId == session.Client.TenantId &&
        string.Equals(frame.ClientId, session.Client.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
        frame.ConnectionEpoch == session.ConnectionEpoch && string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase);

    private static bool TryGetSession(RemoteSupportV2SessionKey? candidate, ClientKey client, out RemoteSupportSessionKey? session)
    {
        session = null;
        if (candidate is null || candidate.TenantId != client.TenantId ||
            !Guid.TryParse(candidate.AgentId, out var agentId) || agentId != client.AgentId ||
            !Guid.TryParse(candidate.RemoteSupportSessionId, out var sessionId) || sessionId == Guid.Empty)
        {
            return false;
        }

        session = new RemoteSupportSessionKey(client.TenantId, client.AgentId, sessionId);
        return true;
    }

    private static bool TryMapNegotiation(
        RemoteSupportV2RouteEnvelope candidate,
        ClientKey client,
        out V2Negotiation? negotiation,
        out Guid edgeRouteId,
        out long routeGeneration)
    {
        negotiation = null;
        edgeRouteId = Guid.Empty;
        routeGeneration = 0;
        if (candidate.Negotiation is null || !TryGetSession(candidate.Session, client, out var session) ||
            !Guid.TryParse(candidate.EdgeRouteId, out edgeRouteId) || edgeRouteId == Guid.Empty ||
            candidate.RouteGeneration is 0 or > long.MaxValue ||
            candidate.Negotiation.NegotiationGeneration is 0 or > long.MaxValue ||
            candidate.Negotiation.Sequence is 0 or > long.MaxValue ||
            !Guid.TryParse(candidate.Negotiation.MessageId, out var messageId) || messageId == Guid.Empty)
        {
            return false;
        }

        routeGeneration = checked((long)candidate.RouteGeneration);
        negotiation = new V2Negotiation(
            session!,
            checked((long)candidate.Negotiation.NegotiationGeneration),
            candidate.Negotiation.Direction,
            checked((long)candidate.Negotiation.Sequence),
            messageId,
            candidate.Negotiation.SignalType,
            candidate.Negotiation.Payload.ToByteArray());
        return RemoteSupportV2ContractValidator.TryValidate(negotiation, out _);
    }

    private static bool TryMapTransitionEvidence(
        RemoteSupportV2RouteEnvelope candidate,
        ClientKey client,
        out NetRatel.Shared.Contracts.RemoteSupport.RemoteSupportV2TransitionEvidence? evidence,
        out Guid edgeRouteId,
        out long routeGeneration)
    {
        evidence = null;
        edgeRouteId = Guid.Empty;
        routeGeneration = 0;
        if (!string.Equals(candidate.Kind, "v2_transition_evidence", StringComparison.Ordinal) ||
            candidate.Negotiation is not null || !TryGetSession(candidate.Session, client, out var session) ||
            !Guid.TryParse(candidate.EdgeRouteId, out edgeRouteId) || edgeRouteId == Guid.Empty ||
            candidate.RouteGeneration is 0 or > long.MaxValue)
        {
            return false;
        }

        NetRatel.AgentGateway.Contracts.V1.RemoteSupportV2TransitionEvidence transport;
        try
        {
            transport = NetRatel.AgentGateway.Contracts.V1.RemoteSupportV2TransitionEvidence.Parser.ParseFrom(candidate.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }

        if (!Guid.TryParse(transport.EvidenceId, out var evidenceId) || evidenceId == Guid.Empty ||
            transport.NegotiationGeneration is 0 or > long.MaxValue || transport.TransitionSequence == 0 ||
            (transport.HasWindowsSessionId && transport.WindowsSessionId <= 0) ||
            (transport.HasActiveConsoleSessionId && transport.ActiveConsoleSessionId <= 0) ||
            (transport.HasHelperRouteId && !Guid.TryParse(transport.HelperRouteId, out _)))
        {
            return false;
        }

        routeGeneration = checked((long)candidate.RouteGeneration);
        evidence = new NetRatel.Shared.Contracts.RemoteSupport.RemoteSupportV2TransitionEvidence(
            session!, evidenceId, checked((long)transport.NegotiationGeneration), transport.TransitionSequence,
            DateTimeOffset.FromUnixTimeMilliseconds(transport.ObservedUnixMs), transport.EvidenceKind,
            transport.HasWindowsSessionId ? transport.WindowsSessionId : null,
            string.IsNullOrWhiteSpace(transport.UserSidHash) ? null : transport.UserSidHash,
            transport.HasActiveConsoleSessionId ? transport.ActiveConsoleSessionId : null,
            string.IsNullOrWhiteSpace(transport.WindowsSessionState) ? null : transport.WindowsSessionState,
            string.IsNullOrWhiteSpace(transport.DesktopKind) ? null : transport.DesktopKind,
            string.IsNullOrWhiteSpace(transport.ProviderKind) ? null : transport.ProviderKind,
            transport.HasHelperRouteId ? Guid.Parse(transport.HelperRouteId) : null,
            transport.HasInventorySequence ? transport.InventorySequence : null);
        return RemoteSupportV2ContractValidator.TryValidate(evidence, out _);
    }

    private async Task RequirePresenceAsync(ClientKey client, Guid connectionId, ulong epoch, CancellationToken cancellationToken)
    {
        var presence = await presenceRouter.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (presence.Status != ShadowPresenceStatus.Online || presence.ConnectionId != connectionId || presence.ConnectionEpoch != checked((long)epoch))
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The remote-support gateway session is fenced by the active presence connection."));
        }
    }

    private static async Task WriteOutboundAsync(System.Threading.Channels.ChannelReader<GatewayRemoteSupportFrame> reader, IServerStreamWriter<GatewayRemoteSupportFrame> responseStream, CancellationToken cancellationToken)
    {
        await foreach (var frame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(frame).ConfigureAwait(false);
        }
    }

    private async Task WriteReplicaSafeOutboundAsync(
        System.Threading.Channels.ChannelReader<RemoteSupportAgentRouteEnvelope> reader,
        IServerStreamWriter<GatewayRemoteSupportFrame> responseStream,
        ValidatedSupportSession connection,
        CancellationToken cancellationToken)
    {
        ulong sequence = 0;
        await foreach (var envelope in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(new GatewayRemoteSupportFrame
            {
                ProtocolVersion = options.ProtocolVersion,
                TenantId = connection.Client.TenantId,
                ClientId = connection.Client.AgentId.ToString("D"),
                ConnectionEpoch = connection.ConnectionEpoch,
                ConnectionId = connection.ConnectionId.ToString("D"),
                Sequence = checked(++sequence),
                V2Envelope = new RemoteSupportV2RouteEnvelope
                {
                    Session = new RemoteSupportV2SessionKey
                    {
                        TenantId = envelope.Session.TenantId,
                        AgentId = envelope.Session.AgentId.ToString("D"),
                        RemoteSupportSessionId = envelope.Session.RemoteSupportSessionId.ToString("D")
                    },
                    EdgeRouteId = envelope.EdgeRouteId.ToString("D"),
                    RouteGeneration = checked((ulong)envelope.RouteGeneration),
                    Kind = envelope.Kind,
                    Payload = ByteString.CopyFrom(envelope.Payload),
                    Negotiation = envelope.Negotiation is { } negotiation
                        ? new NetRatel.AgentGateway.Contracts.V1.RemoteSupportV2NegotiationEnvelope
                        {
                            Session = new RemoteSupportV2SessionKey
                            {
                                TenantId = negotiation.Session.TenantId,
                                AgentId = negotiation.Session.AgentId.ToString("D"),
                                RemoteSupportSessionId = negotiation.Session.RemoteSupportSessionId.ToString("D")
                            },
                            NegotiationGeneration = checked((ulong)negotiation.Generation),
                            Direction = negotiation.Direction,
                            Sequence = checked((ulong)negotiation.Sequence),
                            MessageId = negotiation.MessageId.ToString("D"),
                            SignalType = negotiation.SignalType,
                            Payload = ByteString.CopyFrom(negotiation.Payload),
                            HasIceConfiguration = negotiation.IceConfiguration is not null,
                            IceExpiresUnixMs = negotiation.IceConfiguration?.ExpiresAtUtc.ToUnixTimeMilliseconds() ?? 0,
                            IceServers =
                            {
                                negotiation.IceConfiguration?.Servers.Select(server =>
                                {
                                    var mapped = new RemoteSupportV2IceServer
                                    {
                                        Username = server.Username ?? string.Empty,
                                        Credential = server.Credential ?? string.Empty
                                    };
                                    mapped.Urls.Add(server.Urls);
                                    return mapped;
                                }) ?? []
                            }
                        }
                        : null
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task RenewReplicaSafeEdgeAsync(RemoteSupportV2AgentEdgeConnection registration, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.RemoteSupportV2AgentEdgeRenewalInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await registration.RenewAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record ValidatedSupportSession(ClientKey Client, Guid ConnectionId, ulong ConnectionEpoch);
}
