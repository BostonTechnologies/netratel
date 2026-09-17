using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Observability;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.API.Services;

namespace NetRatel.API.Gateway;

[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentGatewayService(
    IClientPresenceRouter presenceRouter,
    IAgentManagementService agentManagement,
    IClientUpdateCatalog updateCatalog,
    ClientUpdateAuthorityService updateAuthority,
    NetRatelAkkaMigrationOptions options,
    TimeProvider timeProvider,
    IHostEnvironment environment,
    ILogger<AgentGatewayService> logger)
    : global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayBase
{
    public override async Task Connect(
        IAsyncStreamReader<AgentFrame> requestStream,
        IServerStreamWriter<GatewayFrame> responseStream,
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
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A connect hello frame is required."));
        }

        var helloFrame = requestStream.Current;
        ThrowIfInvalid(AgentGatewayProtocolValidator.ValidateHello(
            helloFrame,
            authenticatedIdentity,
            options.ProtocolVersion));

        // The authenticated stream gets a server-issued connection identifier.
        // The hello value is correlation only and cannot be reused to take over
        // an already admitted stream.
        var connectionId = Guid.NewGuid();
        var operationId = Guid.Parse(helloFrame.OperationId);
        var client = new ClientKey(authenticatedIdentity.TenantId, authenticatedIdentity.AgentId);
        var responseAuthority = options.PresenceAuthority;
        var operationalAuthority = options.OperationalPresenceAuthority;
        var environmentName = environment.EnvironmentName;
        GatewayPresenceSessionStarted? session = null;
        Guid? activationAttemptId = null;
        Guid? activationReleaseId = null;
        var activationReadmitted = false;
        var disconnectReason = "stream_closed";

        try
        {
            var receivedAtUtc = timeProvider.GetUtcNow();
            session = await StartPresenceSessionAsync(
                new StartGatewayPresenceSession(
                    client,
                    connectionId,
                    operationId,
                    options.ProtocolVersion,
                    helloFrame.Hello.AgentVersion,
                    helloFrame.Hello.Capabilities.ToArray(),
                    NullIfWhiteSpace(helloFrame.Hello.LegacySpacetimeIdentity),
                    receivedAtUtc),
                operationalAuthority,
                environmentName,
                context.CancellationToken).ConfigureAwait(false);

            if (options.IsClientUpdateAuthorityActive &&
                helloFrame.Hello.UpdateActivation is { } activation &&
                Guid.TryParse(activation.AttemptId, out var parsedAttemptId) &&
                Guid.TryParse(activation.ReleaseId, out var parsedReleaseId))
            {
                try
                {
                    var readmission = await updateAuthority.MarkReadmittedAsync(
                        authenticatedIdentity,
                        parsedAttemptId,
                        parsedReleaseId,
                        activation.AdmissionNonce,
                        helloFrame.Hello.AgentVersion,
                        connectionId,
                        session.ConnectionEpoch,
                        context.CancellationToken).ConfigureAwait(false);
                    activationReadmitted = readmission.Accepted;
                    if (activationReadmitted)
                    {
                        activationAttemptId = parsedAttemptId;
                        activationReleaseId = parsedReleaseId;
                    }
                    else
                    {
                        logger.LogWarning("Update activation readmission rejected. reason={Reason} attemptId={AttemptId}",
                            readmission.Reason, parsedAttemptId);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Update activation readmission could not be recorded; presence remains admitted.");
                }
            }

            logger.LogInformation(
                "Agent gateway presence session admitted. authority={Authority}, fallbackUsed={FallbackUsed}, migrationPhase={MigrationPhase}",
                responseAuthority,
                false,
                "authority-spike");

            var connected = new ConnectAccepted
            {
                HeartbeatIntervalSeconds = checked((uint)options.HeartbeatIntervalSeconds),
                HeartbeatTimeoutSeconds = checked((uint)options.HeartbeatTimeoutSeconds),
                PresenceAuthority = responseAuthority
            };
            AddUpdateMetadata(connected, helloFrame.Hello, client);
            await responseStream.WriteAsync(new GatewayFrame
            {
                ProtocolVersion = options.ProtocolVersion,
                TenantId = client.TenantId,
                ClientId = client.AgentId.ToString("D"),
                ConnectionEpoch = checked((ulong)session.ConnectionEpoch),
                ConnectionId = connectionId.ToString("D"),
                OperationId = operationId.ToString("D"),
                Sequence = 0,
                Connected = connected
            }).ConfigureAwait(false);

            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var frame = requestStream.Current;
                ThrowIfInvalid(AgentGatewayProtocolValidator.ValidateHeartbeat(
                    frame,
                    authenticatedIdentity,
                    options.ProtocolVersion,
                    connectionId,
                    session.ConnectionEpoch));

                var heartbeatOperationId = Guid.Parse(frame.OperationId);
                var heartbeatReceivedAtUtc = timeProvider.GetUtcNow();
                var result = await RecordPresenceHeartbeatAsync(
                    new RecordGatewayHeartbeat(
                        client,
                        connectionId,
                        session.ConnectionEpoch,
                        heartbeatOperationId,
                        frame.Sequence,
                        heartbeatReceivedAtUtc),
                    operationalAuthority,
                    environmentName,
                    context.CancellationToken).ConfigureAwait(false);

                if (result.Disposition is not PresenceMessageDisposition.Accepted and
                    not PresenceMessageDisposition.Duplicate)
                {
                    disconnectReason = "presence_fenced";
                    throw new RpcException(new Status(
                        StatusCode.Aborted,
                        $"Presence frame was rejected: {result.Disposition}."));
                }

                var heartbeatAccepted = new HeartbeatAccepted
                {
                    Duplicate = result.Disposition == PresenceMessageDisposition.Duplicate,
                    ReceivedAtUtc = Timestamp.FromDateTimeOffset(heartbeatReceivedAtUtc),
                    PresenceAuthority = responseAuthority
                };
                AddUpdateMetadata(heartbeatAccepted, helloFrame.Hello, client);
                if (activationReadmitted && activationAttemptId.HasValue && activationReleaseId.HasValue)
                {
                    try
                    {
                        var confirmation = await updateAuthority.ConfirmAsync(
                            authenticatedIdentity,
                            activationAttemptId.Value,
                            connectionId,
                            session.ConnectionEpoch,
                            context.CancellationToken).ConfigureAwait(false);
                        if (confirmation.Accepted && confirmation.ConfirmationId.HasValue)
                        {
                            heartbeatAccepted.UpdateConfirmation = new UpdateActivationConfirmation
                            {
                                AttemptId = activationAttemptId.Value.ToString("D"),
                                ReleaseId = activationReleaseId.Value.ToString("D"),
                                ConfirmationId = confirmation.ConfirmationId.Value.ToString("D")
                            };
                            activationReadmitted = false;
                        }
                        else
                        {
                            logger.LogWarning("Update activation confirmation rejected. reason={Reason} attemptId={AttemptId}",
                                confirmation.Reason, activationAttemptId.Value);
                        }
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Update activation heartbeat confirmation failed; presence remains healthy.");
                    }
                }

                await responseStream.WriteAsync(new GatewayFrame
                {
                    ProtocolVersion = options.ProtocolVersion,
                    TenantId = client.TenantId,
                    ClientId = client.AgentId.ToString("D"),
                    ConnectionEpoch = checked((ulong)session.ConnectionEpoch),
                    ConnectionId = connectionId.ToString("D"),
                    OperationId = heartbeatOperationId.ToString("D"),
                    Sequence = frame.Sequence,
                    HeartbeatAccepted = heartbeatAccepted
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            disconnectReason = "stream_cancelled";
        }
        catch
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(
                "presence", operationalAuthority, fallbackUsed: false, environmentName);
            throw;
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await EndPresenceSessionAsync(
                        new EndGatewayPresenceSession(
                            client,
                            connectionId,
                            session.ConnectionEpoch,
                            disconnectReason,
                            timeProvider.GetUtcNow()),
                        operationalAuthority,
                        environmentName).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    NetRatelAkkaTelemetry.RecordAuthorityFailure(
                        "presence", operationalAuthority, fallbackUsed: false, environmentName);
                    logger.LogWarning(
                        exception,
                        "Failed to close gateway presence session without fallback. tenantId={TenantId}, agentId={AgentId}, epoch={Epoch}, authority={Authority}",
                        client.TenantId,
                        client.AgentId,
                        session.ConnectionEpoch,
                        responseAuthority);
                    context.Status = new Status(
                        StatusCode.Unavailable,
                        "The gateway presence session could not be closed cleanly; no fallback was used.");
                }
            }
        }
    }

    private async Task<GatewayPresenceSessionStarted> StartPresenceSessionAsync(
        StartGatewayPresenceSession message,
        string authority,
        string environmentName,
        CancellationToken cancellationToken)
    {
        NetRatelAkkaTelemetry.RecordAuthorityRequest(
            "presence", authority, fallbackUsed: false, environmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
            "presence", authority, "connect", fallbackUsed: false, environmentName);
        try
        {
            var session = await presenceRouter.StartSessionAsync(message, cancellationToken).ConfigureAwait(false);
            NetRatelAkkaTelemetry.RecordAuthorityEvent(
                "presence", authority, fallbackUsed: false, environmentName);
            return session;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.GetType().Name);
            throw;
        }
    }

    private async Task<PresenceMessageResult> RecordPresenceHeartbeatAsync(
        RecordGatewayHeartbeat message,
        string authority,
        string environmentName,
        CancellationToken cancellationToken)
    {
        NetRatelAkkaTelemetry.RecordAuthorityRequest(
            "presence", authority, fallbackUsed: false, environmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
            "presence", authority, "heartbeat", fallbackUsed: false, environmentName);
        try
        {
            var result = await presenceRouter.RecordHeartbeatAsync(message, cancellationToken).ConfigureAwait(false);
            if (result.Disposition is PresenceMessageDisposition.Accepted or PresenceMessageDisposition.Duplicate)
            {
                NetRatelAkkaTelemetry.RecordAuthorityEvent(
                    "presence", authority, fallbackUsed: false, environmentName);
            }
            else
            {
                activity?.SetStatus(
                    System.Diagnostics.ActivityStatusCode.Error,
                    result.Disposition.ToString());
            }

            return result;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.GetType().Name);
            throw;
        }
    }

    private async Task EndPresenceSessionAsync(
        EndGatewayPresenceSession message,
        string authority,
        string environmentName)
    {
        NetRatelAkkaTelemetry.RecordAuthorityRequest(
            "presence", authority, fallbackUsed: false, environmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
            "presence", authority, "disconnect", fallbackUsed: false, environmentName);
        try
        {
            await presenceRouter.EndSessionAsync(message, CancellationToken.None).ConfigureAwait(false);
            NetRatelAkkaTelemetry.RecordAuthorityEvent(
                "presence", authority, fallbackUsed: false, environmentName);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.GetType().Name);
            throw;
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
                "Agent gateway admission lookup failed. tenantId={TenantId}, agentId={AgentId}",
                identity.TenantId,
                identity.AgentId);
            throw new RpcException(new Status(StatusCode.Unavailable, "Agent admission state is unavailable."));
        }

        if (agent is null || !agent.IsEnabled || agent.RevokedAtUtc.HasValue)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The agent is not active."));
        }
    }

    private static void ThrowIfInvalid(AgentFrameValidationResult validation)
    {
        if (!validation.IsValid)
        {
            throw new RpcException(new Status(validation.StatusCode, validation.Error));
        }
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private void AddUpdateMetadata(ConnectAccepted response, ConnectHello hello, ClientKey client)
    {
        var metadata = ResolveUpdateMetadata(hello, client);
        if (metadata.Offer is not null) response.UpdateOffer = metadata.Offer;
        if (metadata.Policy is not null) response.UpdatePolicy = metadata.Policy;
    }

    private void AddUpdateMetadata(HeartbeatAccepted response, ConnectHello hello, ClientKey client)
    {
        var metadata = ResolveUpdateMetadata(hello, client);
        if (metadata.Offer is not null) response.UpdateOffer = metadata.Offer;
        if (metadata.Policy is not null) response.UpdatePolicy = metadata.Policy;
    }

    private (ClientUpdateOffer? Offer, ClientUpdatePolicy? Policy) ResolveUpdateMetadata(ConnectHello hello, ClientKey client)
    {
        if (!options.IsClientUpdateAuthorityActive ||
            !hello.Capabilities.Contains("client-auto-update-v2") ||
            string.IsNullOrWhiteSpace(hello.RuntimeId))
        {
            return (null, null);
        }

        var offer = updateCatalog.GetOffer(client.TenantId, client.AgentId, hello.RuntimeId, hello.AgentVersion, hello.UpdateChannel);
        var policy = updateCatalog.GetPolicy(client.TenantId, client.AgentId, hello.UpdatePolicyRevision);
        if (offer is not null)
            ClientUpdateTelemetry.OfferObserved(offer.RuntimeId, hello.UpdateChannel);
        return (
            offer is null ? null : new ClientUpdateOffer
            {
                ReleaseId = offer.ReleaseId.ToString("D"),
                CatalogRevision = offer.CatalogRevision,
                RuntimeId = offer.RuntimeId,
                Version = offer.Version,
                Sha256 = offer.Sha256,
                SizeBytes = offer.SizeBytes,
                DownloadPath = offer.DownloadPath
            },
            new ClientUpdatePolicy
            {
                Revision = policy.Revision,
                AutoUpdateEnabled = policy.AutoUpdateEnabled,
                Suspended = policy.Suspended,
                ResumeRequested = policy.ResumeRequested,
                SuppressedReleaseId = policy.SuppressedReleaseId?.ToString("D") ?? string.Empty
            });
    }
}
