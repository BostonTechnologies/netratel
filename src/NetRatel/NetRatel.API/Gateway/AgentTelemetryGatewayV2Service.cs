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
using NetRatel.Application.Services;
using NetRatel.API.Services;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Application.Monitoring;

namespace NetRatel.API.Gateway;

/// <summary>Fenced, acknowledged telemetry stream for gateway-native agents.</summary>
[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentTelemetryGatewayV2Service(
    IClientTelemetryRouter telemetryRouter,
    IClientPresenceRouter presenceRouter,
    IAgentManagementService agentManagement,
    IAgentTelemetryCompatibilityRegistry compatibilityRegistry,
    IGatewayTelemetryLiveRegistry liveRegistry,
    IAgentTelemetryGatewaySessionRegistry sessions,
    NetRatelAkkaOptions options,
    TimeProvider timeProvider,
    IHostEnvironment environment,
    ILogger<AgentTelemetryGatewayV2Service> logger,
    IClientServicesRouter? servicesRouter = null,
    IClientServiceWatchPolicySource? watchPolicySource = null,
    IMonitoringRuntime? monitoring = null)
    : global::NetRatel.AgentGateway.Contracts.V1.AgentTelemetryGatewayV2.AgentTelemetryGatewayV2Base
{
    public override async Task Connect(
        IAsyncStreamReader<AgentTelemetryFrame> requestStream,
        IServerStreamWriter<GatewayTelemetryFrame> responseStream,
        ServerCallContext context)
    {
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
        var supportsServices = servicesRouter is not null && watchPolicySource is not null &&
            hello.Hello.Capabilities.Contains(ClientServicesLimits.Capability, StringComparer.Ordinal);
        AgentTelemetryGatewaySessionRegistration registration;
        try
        {
            registration = sessions.Register(client, connectionId, hello.ConnectionEpoch, supportsDynamicSampling, hello.Hello.AgentVersion, provisional: true, supportsServices);
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
            var evidenceFence = new MonitoringEvidenceFence(client, connectionId, checked((long)hello.ConnectionEpoch), registration.RegistrationId);
            var monitoringStarted = false;
            try
            {
                if (monitoring is not null)
                {
                    RequireMonitoringAccepted(await monitoring.BeginEvidenceStreamAsync(evidenceFence, admissionCancellation.Token).ConfigureAwait(false));
                    monitoringStarted = true;
                    if (!registration.IsCurrent) throw new RpcException(new Status(StatusCode.Aborted, "Telemetry registration has been replaced."));
                }
                try
                {
                    var accepted = new TelemetryConnectAccepted { TelemetryAuthority = "akka", MaximumInFlightFrames = 1 };
                    if (registration.SupportsServices) accepted.AcceptedCapabilities.Add(ClientServicesLimits.Capability);
                    await registration.EnqueueReliableAsync(new GatewayTelemetryFrame
                    {
                        ProtocolVersion = NetRatelAkkaOptions.ProtocolVersion,
                        TenantId = client.TenantId,
                        ClientId = client.AgentId.ToString("D"),
                        ConnectionEpoch = hello.ConnectionEpoch,
                        ConnectionId = hello.ConnectionId,
                        Sequence = 0,
                        Accepted = accepted
                    }, admissionCancellation.Token).ConfigureAwait(false);
                    if (registration.SupportsServices)
                    {
                        var policy = await watchPolicySource!.GetPolicyAsync(client, admissionCancellation.Token).ConfigureAwait(false);
                        if (policy.Client != client || !ClientServicesCoordinator.IsValidPolicy(policy.Policy, timeProvider.GetUtcNow()))
                            throw new RpcException(new Status(StatusCode.Unavailable, "The services watch policy is unavailable."));
                        await servicesRouter!.UpdateWatchPolicyAsync(policy, admissionCancellation.Token).ConfigureAwait(false);
                        var queued = false;
                        if (!registration.TryPublish(() => queued = sessions.TryPublishServicesPolicy(client, policy.Policy)))
                            throw new RpcException(new Status(StatusCode.Aborted, "Telemetry registration has been replaced."));
                        if (!queued) throw new RpcException(new Status(StatusCode.Unavailable, "The services policy could not be admitted."));
                    }
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
                        var isServices = envelope.PayloadCase == AgentTelemetryFrame.PayloadOneofCase.ServicesChunk;
                        if ((!isServices && envelope.PayloadCase != AgentTelemetryFrame.PayloadOneofCase.Snapshot) ||
                            !MatchesSession(envelope, client) || envelope.ConnectionEpoch != hello.ConnectionEpoch ||
                            !string.Equals(envelope.ConnectionId, hello.ConnectionId, StringComparison.OrdinalIgnoreCase) || envelope.Sequence == 0 || envelope.Sequence <= lastSequence)
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "The telemetry envelope is invalid or stale."));
                        if (isServices && !registration.SupportsServices)
                            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Services capability was not negotiated."));
                        if (!isServices && (
                            envelope.Snapshot.Sequence != envelope.Sequence || envelope.Snapshot.ConnectionEpoch != envelope.ConnectionEpoch ||
                            !string.Equals(envelope.Snapshot.ConnectionId, envelope.ConnectionId, StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "The telemetry snapshot envelope is invalid or stale."));
                        }

                        if (!isServices) AgentTelemetryGatewayMapper.ThrowIfInvalid(AgentTelemetryProtocolValidator.Validate(
                            envelope.Snapshot, identity, NetRatelAkkaOptions.ProtocolVersion, options.MaxTelemetryScopesPerFrame));
                        await RequireActivePresenceAsync(client, envelope.ConnectionId, envelope.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
                        if (!registration.IsCurrent)
                        {
                            throw new RpcException(new Status(StatusCode.Aborted, "Telemetry session has been replaced."));
                        }
                        ulong acceptedSequence;
                        if (isServices)
                        {
                            var chunk = ClientServicesGatewayMapper.Map(envelope, client, timeProvider.GetUtcNow());
                            var servicesResult = await servicesRouter!.RecordAsync(new(chunk), cancellationToken).ConfigureAwait(false);
                            if (servicesResult.Disposition != ClientServicesMessageDisposition.Accepted)
                                throw new RpcException(new Status(servicesResult.Disposition switch
                                {
                                    ClientServicesMessageDisposition.PersistenceUnavailable => StatusCode.Unavailable,
                                    ClientServicesMessageDisposition.CapacityExceeded => StatusCode.ResourceExhausted,
                                    ClientServicesMessageDisposition.Invalid => StatusCode.InvalidArgument,
                                    _ => StatusCode.Aborted
                                }, "The services chunk was rejected by its projection fence."));
                            if (monitoring is not null)
                            {
                                if (servicesResult.State is not { } acceptedState || acceptedState.Client != client ||
                                    acceptedState.ConnectionEpoch != evidenceFence.ConnectionEpoch || acceptedState.LastAcceptedSequence != envelope.Sequence)
                                    throw new RpcException(new Status(StatusCode.Unavailable, "The accepted services evidence is unavailable."));
                                RequireMonitoringAccepted(await monitoring.RecordServicesAsync(new(evidenceFence, acceptedState), cancellationToken).ConfigureAwait(false));
                            }
                            if (!registration.TryPublish(() => { }))
                                throw new RpcException(new Status(StatusCode.Aborted, "Telemetry registration has been replaced."));
                            acceptedSequence = servicesResult.LastAcceptedSequence;
                        }
                        else
                        {
                            var snapshot = AgentTelemetryGatewayMapper.MapSnapshot(
                                envelope.Snapshot,
                                client,
                                checked((long)envelope.ConnectionEpoch),
                                timeProvider.GetUtcNow());
                            var result = await telemetryRouter.RecordAsync(new RecordTelemetrySnapshot(snapshot), cancellationToken).ConfigureAwait(false);
                            if (result.Disposition == TelemetryMessageDisposition.Accepted && monitoring is not null)
                                RequireMonitoringAccepted(await monitoring.RecordTelemetryAsync(new(evidenceFence, snapshot), cancellationToken).ConfigureAwait(false));
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
                                "telemetry", "akka", "snapshot", environment.EnvironmentName);
                            NetRatelAkkaTelemetry.RecordAuthorityRequest("telemetry", "akka", environment.EnvironmentName);
                            NetRatelAkkaTelemetry.RecordAuthorityEvent("telemetry", "akka", environment.EnvironmentName);
                            acceptedSequence = result.LastAcceptedSequence;
                        }
                        lastSequence = envelope.Sequence;
                        await registration.EnqueueReliableAsync(new GatewayTelemetryFrame
                        {
                            ProtocolVersion = NetRatelAkkaOptions.ProtocolVersion,
                            TenantId = client.TenantId,
                            ClientId = client.AgentId.ToString("D"),
                            ConnectionEpoch = envelope.ConnectionEpoch,
                            ConnectionId = envelope.ConnectionId,
                            Sequence = envelope.Sequence,
                            SnapshotAccepted = new TelemetrySnapshotAccepted { AcceptedSequence = acceptedSequence, AvailableCredits = 1 }
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
            finally
            {
                if (monitoringStarted)
                {
                    using var closeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await monitoring!.EndEvidenceStreamAsync(evidenceFence, closeCancellation.Token).ConfigureAwait(false); }
                    catch (Exception exception) { logger.LogWarning(exception, "Monitoring evidence stream closure could not be persisted."); }
                }
            }
        }
    }

    private static void RequireMonitoringAccepted(MonitoringInputResult result)
    {
        if (result.Disposition == MonitoringInputDisposition.Accepted) return;
        throw new RpcException(new Status(result.Disposition switch
        {
            MonitoringInputDisposition.PersistenceUnavailable => StatusCode.Unavailable,
            MonitoringInputDisposition.CapacityExceeded => StatusCode.ResourceExhausted,
            _ => StatusCode.Aborted
        }, "The monitoring input was rejected by its durable evidence fence."));
    }

    private bool MatchesSession(AgentTelemetryFrame frame, ClientKey client) =>
        string.Equals(frame.ProtocolVersion, NetRatelAkkaOptions.ProtocolVersion, StringComparison.Ordinal) &&
        frame.TenantId == client.TenantId &&
        string.Equals(frame.ClientId, client.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParse(frame.ConnectionId, out var connectionId) && connectionId != Guid.Empty && frame.ConnectionEpoch is > 0 and <= long.MaxValue;

    private async Task RequireActivePresenceAsync(ClientKey client, string connectionId, ulong connectionEpoch, CancellationToken cancellationToken)
    {
        var presence = await presenceRouter.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (presence.Status != ClientPresenceStatus.Online || !Guid.TryParse(connectionId, out var id) ||
            presence.ConnectionId != id || presence.ConnectionEpoch != checked((long)connectionEpoch))
        {
            throw new RpcException(new Status(StatusCode.Aborted, "Telemetry session is fenced by the active presence connection."));
        }
    }
}
