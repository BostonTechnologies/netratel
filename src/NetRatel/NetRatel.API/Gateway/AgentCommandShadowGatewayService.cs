using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Agents;
using NetRatel.Application.Commands;
using NetRatel.Application.Presence;
using ApplicationCommandLifecycleStatus = NetRatel.Application.Commands.CommandLifecycleStatus;

namespace NetRatel.API.Gateway;

[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentCommandShadowGatewayService(
    IClientCommandRouter commandRouter,
    IClientPresenceRouter presenceRouter,
    IAgentManagementService agentManagement,
    NetRatelAkkaMigrationOptions options,
    ILogger<AgentCommandShadowGatewayService> logger)
    : global::NetRatel.AgentGateway.Contracts.V1.AgentCommandShadowGateway.AgentCommandShadowGatewayBase
{
    public override async Task<CommandShadowPublishSummary> PublishCommandEvents(
        IAsyncStreamReader<CommandShadowFrame> requestStream,
        ServerCallContext context)
    {
        if (!options.Enabled || !options.GatewayEnabled || !options.CommandShadowEnabled)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Command shadow mode is disabled."));
        }

        if (!AgentGatewayIdentityResolver.TryResolve(
                context.GetHttpContext().User,
                out var authenticatedIdentity,
                out var identityError) ||
            authenticatedIdentity is null)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, identityError));
        }

        await EnsureAgentIsActiveAsync(authenticatedIdentity, context.CancellationToken).ConfigureAwait(false);

        var client = new ClientKey(authenticatedIdentity.TenantId, authenticatedIdentity.AgentId);
        ulong acceptedCount = 0;
        ulong rejectedCount = 0;
        ulong lastAcceptedSequence = 0;

        while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            var frame = requestStream.Current;
            ThrowIfInvalid(AgentCommandProtocolValidator.Validate(
                frame,
                authenticatedIdentity,
                options.ProtocolVersion));

            await EnsureCurrentPresenceSessionAsync(client, frame, context.CancellationToken)
                .ConfigureAwait(false);

            var result = await commandRouter.RecordAsync(
                new RecordCommandLifecycleEvent(new CommandLifecycleEvent(
                    client,
                    frame.CommandId,
                    frame.CorrelationId,
                    frame.RequestTimestamp.ToDateTimeOffset(),
                    frame.StatusTimestamp.ToDateTimeOffset(),
                    frame.Version,
                    frame.Sequence,
                    MapStatus(frame.Status))),
                context.CancellationToken).ConfigureAwait(false);

            if (result.Disposition == CommandMessageDisposition.Accepted)
            {
                acceptedCount = IncrementSaturating(acceptedCount);
                lastAcceptedSequence = result.LastAcceptedSequence;
            }
            else
            {
                rejectedCount = IncrementSaturating(rejectedCount);
            }
        }

        return new CommandShadowPublishSummary
        {
            AcceptedCount = acceptedCount,
            RejectedCount = rejectedCount,
            LastAcceptedSequence = lastAcceptedSequence,
            CommandAuthority = "unavailable"
        };
    }

    private async Task EnsureCurrentPresenceSessionAsync(
        ClientKey client,
        CommandShadowFrame frame,
        CancellationToken cancellationToken)
    {
        var presence = await presenceRouter.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        var connectionId = Guid.Parse(frame.ConnectionId);
        var connectionEpoch = checked((long)frame.ConnectionEpoch);
        if (presence.Status != ShadowPresenceStatus.Online ||
            presence.ConnectionId != connectionId ||
            presence.ConnectionEpoch != connectionEpoch)
        {
            throw new RpcException(new Status(
                StatusCode.Aborted,
                "Command shadow frame does not match the active authenticated presence session."));
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
                "Command shadow gateway admission lookup failed. tenantId={TenantId}, agentId={AgentId}",
                identity.TenantId,
                identity.AgentId);
            throw new RpcException(new Status(StatusCode.Unavailable, "Agent admission state is unavailable."));
        }

        if (agent is null || !agent.IsEnabled || agent.RevokedAtUtc.HasValue)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The agent is not active."));
        }
    }

    private static ApplicationCommandLifecycleStatus MapStatus(CommandShadowStatus status) =>
        status switch
        {
            CommandShadowStatus.Created => ApplicationCommandLifecycleStatus.Created,
            CommandShadowStatus.Dispatched => ApplicationCommandLifecycleStatus.Dispatched,
            CommandShadowStatus.Accepted => ApplicationCommandLifecycleStatus.Accepted,
            CommandShadowStatus.Started => ApplicationCommandLifecycleStatus.Started,
            CommandShadowStatus.Completed => ApplicationCommandLifecycleStatus.Completed,
            CommandShadowStatus.Failed => ApplicationCommandLifecycleStatus.Failed,
            CommandShadowStatus.Cancelled => ApplicationCommandLifecycleStatus.Cancelled,
            _ => throw new InvalidOperationException($"Unsupported command shadow status '{status}'.")
        };

    private static void ThrowIfInvalid(AgentFrameValidationResult validation)
    {
        if (!validation.IsValid)
        {
            throw new RpcException(new Status(validation.StatusCode, validation.Error));
        }
    }

    private static ulong IncrementSaturating(ulong value) =>
        value == ulong.MaxValue ? value : value + 1;
}
