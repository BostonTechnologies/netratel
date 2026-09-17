using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Realtime;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Observability;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;

namespace NetRatel.API.Gateway;

/// <summary>Fenced, acknowledged telemetry authority stream for gateway-native agents.</summary>
[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentTelemetryGatewayV2Service(
    IClientTelemetryRouter telemetryRouter,
    IClientPresenceRouter presenceRouter,
    IAgentManagementService agentManagement,
    IAgentTelemetryCompatibilityRegistry compatibilityRegistry,
    IGatewayTelemetryLiveRegistry liveRegistry,
    IAgentTelemetryGatewaySessionRegistry sessions,
    NetRatelAkkaMigrationOptions options,
    TimeProvider timeProvider,
    IHostEnvironment environment,
    ILogger<AgentTelemetryGatewayV2Service> logger)
    : global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Base
{
    public override async Task Connect(
        IAsyncStreamReader<AgentTelemetryFrame> requestStream,
        IServerStreamWriter<GatewayTelemetryFrame> responseStream,
        ServerCallContext context)
    {
        if (!options.IsTelemetryAuthorityActive)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The telemetry authority canary is disabled."));
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
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A telemetry hello frame is required."));
        }

        var hello = requestStream.Current;
        var client = new ClientKey(identity.TenantId, identity.AgentId);
        if (hello.PayloadCase != AgentTelemetryFrame.PayloadOneofCase.Hello ||
            !MatchesSession(hello, client) || hello.Sequence != 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The telemetry hello does not match the authenticated presence session."));
        }

        await RequireActivePresenceAsync(client, hello.ConnectionId, hello.ConnectionEpoch, context.CancellationToken).ConfigureAwait(false);
        var connectionId = Guid.Parse(hello.ConnectionId);
        var supportsDynamicSampling = hello.Hello.Capabilities.Contains("telemetry-rate-control-v1", StringComparer.Ordinal);
        AgentTelemetryGatewaySessionRegistration registration;
        try
        {
            registration = sessions.Register(client, connectionId, hello.ConnectionEpoch, supportsDynamicSampling, hello.Hello.AgentVersion, provisional: true);
        }
        catch (AgentGatewayRegistrationFencedException exception)
        {
            throw new RpcException(new Status(StatusCode.Aborted, exception.Message));
        }

        await using (registration)
        {
            using var admissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, registration.CompletionToken);
            try
            {
                await RequireActivePresenceAsync(client, hello.ConnectionId, hello.ConnectionEpoch, admissionCancellation.Token).WaitAsync(admissionCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (registration.CompletionToken.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
            {
                throw new RpcException(new Status(StatusCode.Aborted, "The gateway registration has been replaced."));
            }
            if (!registration.TryActivate())
            {
                throw new RpcException(new Status(StatusCode.Aborted, "Telemetry registration has been replaced."));
            }

            try
            {
                await registration.EnqueueReliableAsync(new GatewayTelemetryFrame
                {
                    ProtocolVersion = options.ProtocolVersion,
                    TenantId = client.TenantId,
                    ClientId = client.AgentId.ToString("D"),
                    ConnectionEpoch = hello.ConnectionEpoch,
                    ConnectionId = hello.ConnectionId,
                    Sequence = 0,
                    Accepted = new TelemetryConnectAccepted { TelemetryAuthority = options.PresenceAuthority, MaximumInFlightFrames = 1 }
                }, admissionCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (registration.CompletionToken.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
            {
                throw new RpcException(new Status(StatusCode.Aborted, "The gateway registration has been replaced."));
            }

            await GatewayDuplexSession.RunAsync(async cancellationToken =>
            {
                ulong lastSequence = 0;
                while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    var envelope = requestStream.Current;
                    if (envelope.PayloadCase != AgentTelemetryFrame.PayloadOneofCase.Snapshot ||
                        !MatchesSession(envelope, client) || envelope.ConnectionEpoch != hello.ConnectionEpoch ||
                        !string.Equals(envelope.ConnectionId, hello.ConnectionId, StringComparison.OrdinalIgnoreCase) || envelope.Sequence == 0 || envelope.Sequence <= lastSequence ||
                        envelope.Snapshot.Sequence != envelope.Sequence || envelope.Snapshot.ConnectionEpoch != envelope.ConnectionEpoch ||
                        !string.Equals(envelope.Snapshot.ConnectionId, envelope.ConnectionId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "The telemetry snapshot envelope is invalid or stale."));
                    }

                    AgentTelemetryGatewayService.ThrowIfInvalid(AgentTelemetryProtocolValidator.Validate(
                        envelope.Snapshot, identity, options.ProtocolVersion, options.MaxTelemetryScopesPerFrame));
                    await RequireActivePresenceAsync(client, envelope.ConnectionId, envelope.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
                    if (!registration.IsCurrent)
                    {
                        throw new RpcException(new Status(StatusCode.Aborted, "Telemetry session has been replaced."));
                    }
                    var snapshot = AgentTelemetryGatewayService.MapSnapshot(
                        envelope.Snapshot,
                        client,
                        checked((long)envelope.ConnectionEpoch),
                        timeProvider.GetUtcNow(),
                        true);
                    var result = await telemetryRouter.RecordAsync(new RecordTelemetrySnapshot(snapshot), cancellationToken).ConfigureAwait(false);
                    if (!registration.TryPublish(() =>
                    {
                        if (result.Disposition == TelemetryMessageDisposition.Accepted)
                        {
                            compatibilityRegistry.Upsert(snapshot);
                            liveRegistry.PublishAccepted(snapshot);
                        }
                    }))
                    {
                        throw new RpcException(new Status(StatusCode.Aborted, "Telemetry registration has been replaced."));
                    }
                    using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
                        "telemetry", options.PresenceAuthority, "snapshot", fallbackUsed: false, environment.EnvironmentName);
                    NetRatelAkkaTelemetry.RecordAuthorityRequest("telemetry", options.PresenceAuthority, fallbackUsed: false, environment.EnvironmentName);
                    NetRatelAkkaTelemetry.RecordAuthorityEvent("telemetry", options.PresenceAuthority, fallbackUsed: false, environment.EnvironmentName);
                    lastSequence = envelope.Sequence;
                    await registration.EnqueueReliableAsync(new GatewayTelemetryFrame
                    {
                        ProtocolVersion = options.ProtocolVersion,
                        TenantId = client.TenantId,
                        ClientId = client.AgentId.ToString("D"),
                        ConnectionEpoch = envelope.ConnectionEpoch,
                        ConnectionId = envelope.ConnectionId,
                        Sequence = envelope.Sequence,
                        SnapshotAccepted = new TelemetrySnapshotAccepted { AcceptedSequence = result.LastAcceptedSequence, AvailableCredits = 1 }
                    }, cancellationToken).ConfigureAwait(false);
                }
            },
            async cancellationToken =>
            {
                if (!registration.IsCurrent) return;

                await foreach (var frame in registration.ReadOutboundAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!registration.IsCurrent) return;
                    await responseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                }
            }, context.CancellationToken, registration.CompletionToken, logger).ConfigureAwait(false);
        }
    }

    private bool MatchesSession(AgentTelemetryFrame frame, ClientKey client) =>
        string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) &&
        frame.TenantId == client.TenantId &&
        string.Equals(frame.ClientId, client.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParse(frame.ConnectionId, out var connectionId) && connectionId != Guid.Empty && frame.ConnectionEpoch > 0;

    private async Task RequireActivePresenceAsync(ClientKey client, string connectionId, ulong connectionEpoch, CancellationToken cancellationToken)
    {
        var presence = await presenceRouter.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (presence.Status != ShadowPresenceStatus.Online || !Guid.TryParse(connectionId, out var id) ||
            presence.ConnectionId != id || presence.ConnectionEpoch != checked((long)connectionEpoch))
        {
            throw new RpcException(new Status(StatusCode.Aborted, "Telemetry session is fenced by the active presence connection."));
        }
    }
}
