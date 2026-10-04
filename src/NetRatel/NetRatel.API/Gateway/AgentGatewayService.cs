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
    IClientUpdateActivationAuthority updateAuthority,
    NetRatelAkkaOptions options,
    TimeProvider timeProvider,
    IHostEnvironment environment,
    ILogger<AgentGatewayService> logger,
    AgentGatewayRenewalAuthenticator? renewalAuthenticator = null,
    AgentGatewayAuthenticationLeaseRegistry? authenticationLeases = null)
    : global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayBase
{
    private const string Authority = "akka";

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

        var admissionExpiresAtUtc = timeProvider.GetUtcNow().AddSeconds(options.GatewayAdmissionTimeoutSeconds);
        using var policyCancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(options.GatewayAdmissionTimeoutSeconds), timeProvider);
        AgentGatewayAuthenticationLease? authenticationLease = null;
        CancellationTokenRegistration authorityRetirement = default;
        // The authenticated stream gets a server-issued connection identifier.
        // The hello value is correlation only and cannot be reused to take over
        // an already admitted stream.
        var connectionId = Guid.NewGuid();
        var operationId = Guid.Empty;
        var client = new ClientKey(authenticatedIdentity.TenantId, authenticatedIdentity.AgentId);
        var responseAuthority = Authority;
        var operationalAuthority = Authority;
        var environmentName = environment.EnvironmentName;
        GatewayPresenceSessionStarted? session = null;
        var startRequested = false;
        Guid? activationAttemptId = null;
        Guid? activationReleaseId = null;
        var activationReadmitted = false;
        var disconnectReason = "stream_closed";

        async Task RunPresenceAsync(CancellationToken cancellationToken)
        {
            await EnsureAgentIsActiveAsync(authenticatedIdentity, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "A connect hello frame is required."));
            cancellationToken.ThrowIfCancellationRequested();
            var helloFrame = requestStream.Current;
            ThrowIfInvalid(AgentGatewayProtocolValidator.ValidateHello(
                helloFrame, authenticatedIdentity, NetRatelAkkaOptions.ProtocolVersion));
            operationId = Guid.Parse(helloFrame.OperationId);
            DateTimeOffset? authenticationExpiresAtUtc = null;
            if (renewalAuthenticator is { CanValidate: true })
            {
                var authorization = context.GetHttpContext().Request.Headers.Authorization.ToString();
                if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    throw new RpcException(new Status(StatusCode.Unauthenticated, "The authenticated gateway token is required."));
                authenticationExpiresAtUtc = await renewalAuthenticator.ValidateAsync(
                    authorization[7..], authenticatedIdentity, null, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            var receivedAtUtc = timeProvider.GetUtcNow();
            startRequested = true;
            session = await StartPresenceSessionAsync(
                new StartGatewayPresenceSession(
                    client,
                    connectionId,
                    operationId,
                    NetRatelAkkaOptions.ProtocolVersion,
                    helloFrame.Hello.AgentVersion,
                    helloFrame.Hello.Capabilities.ToArray(),
                    NullIfWhiteSpace(helloFrame.Hello.LegacySpacetimeIdentity),
                    receivedAtUtc, AuthenticationExpiresAtUtc: authenticationExpiresAtUtc,
                    AdmissionExpiresAtUtc: admissionExpiresAtUtc, ProvisionalAdmission: true),
                operationalAuthority,
                environmentName,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (session.Disposition is not (PresenceMessageDisposition.Accepted or PresenceMessageDisposition.Duplicate))
                throw new RpcException(new Status(StatusCode.Aborted, "The presence admission is no longer authorized."));
            // Negotiated first-heartbeat liveness cannot extend the absolute
            // bootstrap deadline for this still-provisional admission.
            var admissionRemaining = admissionExpiresAtUtc - timeProvider.GetUtcNow();
            if (admissionRemaining <= TimeSpan.Zero)
            {
                policyCancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            policyCancellation.CancelAfter(admissionRemaining < options.HeartbeatTimeout
                ? admissionRemaining : options.HeartbeatTimeout);
            if (authenticationExpiresAtUtc is { } expiresAtUtc && authenticationLeases is not null)
            {
                authenticationLease = authenticationLeases.Register(client, connectionId,
                    checked((ulong)session.ConnectionEpoch), expiresAtUtc);
                authorityRetirement = authenticationLease.CompletionToken.Register(policyCancellation.Cancel);
            }
            var supportsRenewal = authenticationExpiresAtUtc.HasValue &&
                helloFrame.Hello.Capabilities.Contains("presence-auth-renewal-v1");

            if (helloFrame.Hello.UpdateActivation is { } activation &&
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
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
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
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Update activation readmission could not be recorded; presence remains admitted.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            logger.LogInformation(
                "Agent gateway presence session admitted. authority={Authority}, tenantId={TenantId}, agentId={AgentId}, connectionId={ConnectionId}, authScheme={AuthScheme}, correlationId={CorrelationId}",
                responseAuthority,
                client.TenantId,
                client.AgentId,
                connectionId,
                context.GetHttpContext().User.Identity?.AuthenticationType ?? "unknown",
                context.GetHttpContext().TraceIdentifier);

            var connected = new ConnectAccepted
            {
                HeartbeatIntervalSeconds = checked((uint)options.HeartbeatIntervalSeconds),
                HeartbeatTimeoutSeconds = checked((uint)options.HeartbeatTimeoutSeconds),
                PresenceAuthority = responseAuthority,
                SupportsAuthenticatedRenewal = supportsRenewal
            };
            AddUpdateMetadata(connected, helloFrame.Hello, client);
            await responseStream.WriteAsync(new GatewayFrame
            {
                ProtocolVersion = NetRatelAkkaOptions.ProtocolVersion,
                TenantId = client.TenantId,
                ClientId = client.AgentId.ToString("D"),
                ConnectionEpoch = checked((ulong)session.ConnectionEpoch),
                ConnectionId = connectionId.ToString("D"),
                OperationId = operationId.ToString("D"),
                Sequence = 0,
                Connected = connected
            }, cancellationToken).ConfigureAwait(false);

            ulong lastAcceptedSequence = 0;
            ulong lastAcknowledgedSequence = 0;
            DateTimeOffset? lastAcknowledgedAtUtc = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await requestStream.MoveNext(cancellationToken).ConfigureAwait(false)) break;
                cancellationToken.ThrowIfCancellationRequested();
                var frame = requestStream.Current;
                if (frame.PayloadCase == AgentFrame.PayloadOneofCase.Renew)
                {
                    if (!supportsRenewal || lastAcknowledgedSequence == 0)
                        throw new RpcException(new Status(StatusCode.FailedPrecondition, "Authenticated renewal was not negotiated after a validated heartbeat."));
                    ThrowIfInvalid(AgentGatewayProtocolValidator.ValidateRenewal(frame, authenticatedIdentity,
                        NetRatelAkkaOptions.ProtocolVersion, connectionId, session.ConnectionEpoch, lastAcceptedSequence));
                    var renewalOperationId = Guid.Parse(frame.OperationId);
                    var renewedExpiry = await renewalAuthenticator!.ValidateAsync(frame.Renew.AccessToken,
                        authenticatedIdentity, authenticationExpiresAtUtc, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var renewedPresence = await RecordPresenceHeartbeatAsync(new RecordGatewayHeartbeat(
                        client, connectionId, session.ConnectionEpoch, renewalOperationId, frame.Sequence,
                        timeProvider.GetUtcNow(), RenewedAuthenticationExpiresAtUtc: renewedExpiry),
                        operationalAuthority, environmentName, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (renewedPresence.Disposition != PresenceMessageDisposition.Accepted ||
                        (authenticationLease is not null && !authenticationLease.TryRenew(renewalOperationId, frame.Sequence, renewedExpiry)))
                        throw new RpcException(new Status(StatusCode.Aborted, "The authenticated renewal is stale or fenced."));
                    lastAcceptedSequence = frame.Sequence;
                    authenticationExpiresAtUtc = renewedExpiry;
                    policyCancellation.CancelAfter(options.HeartbeatTimeout);
                    await responseStream.WriteAsync(new GatewayFrame
                    {
                        ProtocolVersion = NetRatelAkkaOptions.ProtocolVersion, TenantId = client.TenantId,
                        ClientId = client.AgentId.ToString("D"), ConnectionId = connectionId.ToString("D"),
                        ConnectionEpoch = checked((ulong)session.ConnectionEpoch),
                        OperationId = renewalOperationId.ToString("D"), Sequence = frame.Sequence,
                        Renewed = new PresenceAuthRenewed
                        {
                            ExpiresAtUtc = Timestamp.FromDateTimeOffset(renewedExpiry), PresenceAuthority = responseAuthority
                        }
                    }, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }
                ThrowIfInvalid(AgentGatewayProtocolValidator.ValidateHeartbeat(
                    frame,
                    authenticatedIdentity,
                    NetRatelAkkaOptions.ProtocolVersion,
                    connectionId,
                    session.ConnectionEpoch));

                var heartbeatOperationId = Guid.Parse(frame.OperationId);
                var heartbeatReceivedAtUtc = timeProvider.GetUtcNow();
                // Accept only a bounded measurement of an ACK this stream has
                // actually sent. Agent wall-clock timestamps never measure RTT.
                var reportedLatency = frame.Heartbeat.HasAcknowledgedHeartbeatRoundTripMs
                    ? frame.Heartbeat.AcknowledgedHeartbeatRoundTripMs
                    : (double?)null;
                var latencyIsValid = lastAcknowledgedSequence > 0 &&
                    frame.Sequence > lastAcknowledgedSequence &&
                    frame.Heartbeat.AcknowledgedHeartbeatSequence == lastAcknowledgedSequence &&
                    reportedLatency is { } latency && double.IsFinite(latency) &&
                    latency >= 0 && latency <= options.HeartbeatTimeout.TotalMilliseconds &&
                    lastAcknowledgedAtUtc is { } measuredAt &&
                    measuredAt <= heartbeatReceivedAtUtc &&
                    heartbeatReceivedAtUtc - measuredAt <= options.HeartbeatTimeout;
                var result = await RecordPresenceHeartbeatAsync(
                    new RecordGatewayHeartbeat(
                        client,
                        connectionId,
                        session.ConnectionEpoch,
                        heartbeatOperationId,
                        frame.Sequence,
                        heartbeatReceivedAtUtc,
                        HeartbeatRoundTripMilliseconds: latencyIsValid ? reportedLatency : null,
                        LatencyMeasuredAtUtc: latencyIsValid ? lastAcknowledgedAtUtc : null),
                    operationalAuthority,
                    environmentName,
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (result.Disposition == PresenceMessageDisposition.Accepted)
                {
                    lastAcceptedSequence = frame.Sequence;
                    policyCancellation.CancelAfter(options.HeartbeatTimeout);
                }
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
                            cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
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
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Update activation heartbeat confirmation failed; presence remains healthy.");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                await responseStream.WriteAsync(new GatewayFrame
                {
                    ProtocolVersion = NetRatelAkkaOptions.ProtocolVersion,
                    TenantId = client.TenantId,
                    ClientId = client.AgentId.ToString("D"),
                    ConnectionEpoch = checked((ulong)session.ConnectionEpoch),
                    ConnectionId = connectionId.ToString("D"),
                    OperationId = heartbeatOperationId.ToString("D"),
                    Sequence = frame.Sequence,
                    HeartbeatAccepted = heartbeatAccepted
                }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Disposition == PresenceMessageDisposition.Accepted)
                {
                    lastAcknowledgedSequence = frame.Sequence;
                    lastAcknowledgedAtUtc = timeProvider.GetUtcNow();
                }
            }
        }

        try
        {
            await GatewayDuplexSession.RunAsync(RunPresenceAsync, context.CancellationToken,
                policyCancellation.Token, logger, context.GetHttpContext().Abort).ConfigureAwait(false);
            if (policyCancellation.IsCancellationRequested)
            {
                var authorityExpired = authenticationLease?.CompletionToken.IsCancellationRequested == true;
                disconnectReason = session is null ? "admission_timeout" : authorityExpired ? "authority_expired" : "heartbeat_expired";
                context.Status = new Status(authorityExpired ? StatusCode.Unauthenticated : StatusCode.DeadlineExceeded,
                    session is null ? "Gateway admission exceeded its deadline." : "The presence owner lifetime has expired.");
            }
            else if (context.CancellationToken.IsCancellationRequested)
                disconnectReason = "stream_cancelled";
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            disconnectReason = "stream_cancelled";
        }
        catch
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(
                "presence", operationalAuthority, environmentName);
            throw;
        }
        finally
        {
            authorityRetirement.Dispose();
            authenticationLease?.Dispose();
            if (startRequested)
            {
                try
                {
                    await EndPresenceSessionAsync(
                        new EndGatewayPresenceSession(
                            client,
                            connectionId,
                            session?.ConnectionEpoch ?? 0,
                            disconnectReason,
                            timeProvider.GetUtcNow(), CancelPendingAdmission: true),
                        operationalAuthority,
                        environmentName).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    NetRatelAkkaTelemetry.RecordAuthorityFailure(
                        "presence", operationalAuthority, environmentName);
                    logger.LogWarning(
                        exception,
                        "Failed to close gateway presence session without fallback. tenantId={TenantId}, agentId={AgentId}, epoch={Epoch}, authority={Authority}",
                        client.TenantId,
                        client.AgentId,
                        session?.ConnectionEpoch ?? 0,
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
            "presence", authority, environmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
            "presence", authority, "connect", environmentName);
        try
        {
            var session = await presenceRouter.StartSessionAsync(message, cancellationToken).ConfigureAwait(false);
            NetRatelAkkaTelemetry.RecordAuthorityEvent(
                "presence", authority, environmentName);
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
            "presence", authority, environmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
            "presence", authority, "heartbeat", environmentName);
        try
        {
            var result = await presenceRouter.RecordHeartbeatAsync(message, cancellationToken).ConfigureAwait(false);
            if (result.Disposition is PresenceMessageDisposition.Accepted or PresenceMessageDisposition.Duplicate)
            {
                NetRatelAkkaTelemetry.RecordAuthorityEvent(
                    "presence", authority, environmentName);
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
            "presence", authority, environmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(
            "presence", authority, "disconnect", environmentName);
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
            var ending = presenceRouter.EndSessionAsync(message, cancellation.Token);
            try
            {
                await ending.WaitAsync(TimeSpan.FromSeconds(5), timeProvider).ConfigureAwait(false);
            }
            catch
            {
                _ = ending.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }
            NetRatelAkkaTelemetry.RecordAuthorityEvent(
                "presence", authority, environmentName);
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
        if (!hello.Capabilities.Contains("client-auto-update-v2") ||
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
