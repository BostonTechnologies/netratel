using Grpc.Core;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.Authorization;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Hosting;
using NetRatel.Akka.RemoteSupport;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.RemoteSupport;
using GrpcStatus = Grpc.Core.Status;

namespace NetRatel.API.Gateway;

/// <summary>
/// Authenticated and presence-fenced agent stream for V2 WTS inventory and
/// exact-target preparation. It is a transient edge projection, not lifecycle
/// or media authority.
/// </summary>
[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentRemoteSupportPreparationGatewayService(
    IClientPresenceRouter presenceRouter,
    IAgentManagementService agentManagement,
    IRemoteSupportV2PreparationRegistry preparations,
    IRequiredActor<RemoteSupportSessionAuthorityRegion> authorityRegion,
    NetRatelAkkaOptions options,
    ILogger<AgentRemoteSupportPreparationGatewayService> logger)
    : AgentRemoteSupportPreparationGateway.AgentRemoteSupportPreparationGatewayBase
{
    public override async Task Connect(
        IAsyncStreamReader<AgentRemoteSupportPreparationFrame> requestStream,
        IServerStreamWriter<GatewayRemoteSupportPreparationFrame> responseStream,
        ServerCallContext context)
    {
        if (!AgentGatewayIdentityResolver.TryResolve(context.GetHttpContext().User, out var identity, out var error) || identity is null)
        {
            throw new RpcException(new GrpcStatus(StatusCode.PermissionDenied, error));
        }

        var agent = await agentManagement.GetAsync(identity.TenantId, identity.AgentId, context.CancellationToken).ConfigureAwait(false);
        if (agent is null || !agent.IsEnabled || agent.RevokedAtUtc.HasValue)
        {
            throw new RpcException(new GrpcStatus(StatusCode.PermissionDenied, "The agent is not active."));
        }

        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new GrpcStatus(StatusCode.InvalidArgument, "A Remote Support V2 preparation hello frame is required."));
        }

        var hello = requestStream.Current;
        var session = await ValidateHelloAsync(hello, identity, context.CancellationToken).ConfigureAwait(false);
        await using var authority = await AgentGatewayAuthenticationLifetime.AttachAsync(context, session.Client, session.ConnectionId, session.ConnectionEpoch).ConfigureAwait(false);
        authority.Token.ThrowIfCancellationRequested();
        using var registration = preparations.Register(
            session.Client,
            session.ConnectionId,
            session.ConnectionEpoch,
            NetRatelAkkaOptions.ProtocolVersion,
            hello.Hello.Capabilities);

        await GatewayDuplexSession.RunAsync(async cancellationToken =>
        {
            ulong lastSequence = 0;
            while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastSequence = await ProcessInboundAsync(requestStream.Current, session, lastSequence, cancellationToken).ConfigureAwait(false);
            }
        }, async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await responseStream.WriteAsync(CreateAccepted(session), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await WriteOutboundAsync(registration.Reader, responseStream, cancellationToken).ConfigureAwait(false);
        }, authority.Token, CancellationToken.None, logger, context.GetHttpContext().Abort).ConfigureAwait(false);
    }

    private async Task<ValidatedPreparationSession> ValidateHelloAsync(
        AgentRemoteSupportPreparationFrame frame,
        AuthenticatedAgentIdentity identity,
        CancellationToken cancellationToken)
    {
        if (frame.PayloadCase != AgentRemoteSupportPreparationFrame.PayloadOneofCase.Hello || frame.Sequence != 0 ||
            !string.Equals(frame.ProtocolVersion, NetRatelAkkaOptions.ProtocolVersion, StringComparison.Ordinal) || frame.TenantId != identity.TenantId ||
            !Guid.TryParse(frame.ClientId, out var agentId) || agentId != identity.AgentId ||
            !Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty || frame.ConnectionEpoch == 0)
        {
            throw new RpcException(new GrpcStatus(StatusCode.InvalidArgument, "The Remote Support V2 preparation hello does not match the authenticated agent identity."));
        }

        var client = new ClientKey(identity.TenantId, identity.AgentId);
        await RequirePresenceAsync(client, connectionId, frame.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
        return new ValidatedPreparationSession(client, connectionId, frame.ConnectionEpoch);
    }

    private async Task<ulong> ProcessInboundAsync(
        AgentRemoteSupportPreparationFrame frame,
        ValidatedPreparationSession session,
        ulong lastSequence,
        CancellationToken cancellationToken)
    {
        if (!Matches(frame, session) || frame.Sequence == 0 || frame.Sequence <= lastSequence)
        {
            throw new RpcException(new GrpcStatus(StatusCode.InvalidArgument, "The Remote Support V2 preparation frame is invalid or stale."));
        }

        await RequirePresenceAsync(session.Client, session.ConnectionId, session.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lastSequence = frame.Sequence;
        var accepted = frame.PayloadCase switch
        {
            AgentRemoteSupportPreparationFrame.PayloadOneofCase.InventorySnapshot =>
                await ReceiveInventoryAsync(session, frame.InventorySnapshot, cancellationToken).ConfigureAwait(false),
            AgentRemoteSupportPreparationFrame.PayloadOneofCase.PreparedTarget =>
                await ReceivePreparedTargetAsync(session, frame.PreparedTarget, cancellationToken).ConfigureAwait(false),
            _ => false
        };
        if (!accepted)
        {
            logger.LogDebug("Ignoring invalid Remote Support V2 preparation frame. tenantId={TenantId}, agentId={AgentId}, kind={FrameKind}",
                session.Client.TenantId, session.Client.AgentId, frame.PayloadCase);
        }

        return lastSequence;
    }

    private async Task<bool> ReceiveInventoryAsync(
        ValidatedPreparationSession session,
        RemoteSupportV2InventorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RemoteSupportV2PreparationRegistry.TryMapInventory(session.Client, snapshot, out var inventory) ||
            !preparations.TryReceiveInventory(session.Client, snapshot))
        {
            return false;
        }

        foreach (var correlation in snapshot.TransitionCorrelations)
        {
            if (!TryMapCorrelation(session, correlation, out var mapped))
            {
                return false;
            }

            var authority = await authorityRegion.GetAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await authority.Ask<RemoteSupportTransitionDecision>(
                    new ObserveRemoteSupportTransitionInventory(
                        mapped.Session,
                        mapped.EffectId,
                        mapped.TransitionId,
                        mapped.PresenceEpoch,
                        inventory!,
                        DateTimeOffset.UtcNow),
                    options.AskTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return true;
    }

    private async Task<bool> ReceivePreparedTargetAsync(
        ValidatedPreparationSession session,
        RemoteSupportV2PreparedTarget preparedTarget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completedPendingPreparation = preparations.TryCompletePreparation(session.Client, preparedTarget);
        if (!preparedTarget.HasTransitionCorrelation)
        {
            return completedPendingPreparation;
        }

        if (!TryMapCorrelation(session, preparedTarget.TransitionCorrelation, out var correlation) ||
            !RemoteSupportV2PreparationRegistry.TryMapPrepared(session.Client, preparedTarget, out var prepared) ||
            !RemoteSupportV2PreparationValidator.TryValidate(prepared!, out _))
        {
            return false;
        }

        var authority = await authorityRegion.GetAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await authority.Ask<RemoteSupportTransitionDecision>(
                new CompleteRemoteSupportReplacementPreparation(
                    correlation.Session,
                    correlation.EffectId,
                    correlation.TransitionId,
                    correlation.PresenceEpoch,
                    prepared!,
                    DateTimeOffset.UtcNow),
                options.AskTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }

    private static bool TryMapCorrelation(
        ValidatedPreparationSession connection,
        RemoteSupportV2TransitionCorrelation? correlation,
        out TransitionCorrelation mapped)
    {
        mapped = null!;
        if (correlation is null || !Guid.TryParse(correlation.EffectId, out var effectId) || effectId == Guid.Empty ||
            !Guid.TryParse(correlation.TransitionId, out var transitionId) || transitionId == Guid.Empty ||
            correlation.PresenceEpoch != connection.ConnectionEpoch || correlation.Session is null ||
            correlation.Session.TenantId != connection.Client.TenantId ||
            !Guid.TryParse(correlation.Session.AgentId, out var agentId) || agentId != connection.Client.AgentId ||
            !Guid.TryParse(correlation.Session.RemoteSupportSessionId, out var supportSessionId) || supportSessionId == Guid.Empty)
        {
            return false;
        }

        mapped = new TransitionCorrelation(
            effectId,
            transitionId,
            correlation.PresenceEpoch,
            new RemoteSupportSessionKey(connection.Client.TenantId, connection.Client.AgentId, supportSessionId));
        return true;
    }

    private GatewayRemoteSupportPreparationFrame CreateAccepted(ValidatedPreparationSession session) => new()
    {
        ProtocolVersion = NetRatelAkkaOptions.ProtocolVersion,
        TenantId = session.Client.TenantId,
        ClientId = session.Client.AgentId.ToString("D"),
        ConnectionEpoch = session.ConnectionEpoch,
        ConnectionId = session.ConnectionId.ToString("D"),
        Sequence = 0,
        Accepted = new RemoteSupportPreparationConnectAccepted { PreparationAuthority = "akka" }
    };

    private bool Matches(AgentRemoteSupportPreparationFrame frame, ValidatedPreparationSession session) =>
        string.Equals(frame.ProtocolVersion, NetRatelAkkaOptions.ProtocolVersion, StringComparison.Ordinal) && frame.TenantId == session.Client.TenantId &&
        string.Equals(frame.ClientId, session.Client.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
        frame.ConnectionEpoch == session.ConnectionEpoch && string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase);

    private async Task RequirePresenceAsync(ClientKey client, Guid connectionId, ulong epoch, CancellationToken cancellationToken)
    {
        var presence = await presenceRouter.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (presence.Status != ClientPresenceStatus.Online || presence.ConnectionId != connectionId || presence.ConnectionEpoch != checked((long)epoch))
        {
            throw new RpcException(new GrpcStatus(StatusCode.Aborted, "The Remote Support V2 preparation stream is fenced by the active presence connection."));
        }
    }

    private static async Task WriteOutboundAsync(
        System.Threading.Channels.ChannelReader<GatewayRemoteSupportPreparationFrame> reader,
        IServerStreamWriter<GatewayRemoteSupportPreparationFrame> responseStream,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await responseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed record ValidatedPreparationSession(ClientKey Client, Guid ConnectionId, ulong ConnectionEpoch);
    private sealed record TransitionCorrelation(Guid EffectId, Guid TransitionId, ulong PresenceEpoch, RemoteSupportSessionKey Session);
}
