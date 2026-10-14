using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Observability;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

/// <summary>
/// Authenticated, fenced job-step transport.  It accepts lifecycle changes
/// only from the presence session that owns the target agent and delegates
/// durable transition validation to the Akka job authority service.
/// </summary>
[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentJobGatewayService(
    IClientPresenceRouter presenceRouter,
    IAgentManagementService agentManagement,
    IAgentJobGatewaySessionRegistry sessions,
    IAkkaJobAuthorityService jobs,
    IHostEnvironment environment,
    ILogger<AgentJobGatewayService> logger,
    TimeProvider clock)
    : AgentJobGateway.AgentJobGatewayBase
{
    private const string Authority = "akka";
    private const string Feature = "jobs";

    public override async Task Connect(
        IAsyncStreamReader<AgentJobFrame> requestStream,
        IServerStreamWriter<GatewayJobFrame> responseStream,
        ServerCallContext context)
    {
        if (!AgentGatewayIdentityResolver.TryResolve(context.GetHttpContext().User, out var identity, out var error) || identity is null)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, error));
        }

        await EnsureAgentIsActiveAsync(identity, context.CancellationToken).ConfigureAwait(false);
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A job gateway hello frame is required."));
        }

        var session = await ValidateHelloAsync(requestStream.Current, identity, context.CancellationToken).ConfigureAwait(false);
        await using var authority = await AgentGatewayAuthenticationLifetime.AttachAsync(context, session.Client, session.ConnectionId, session.ConnectionEpoch).ConfigureAwait(false);
        AgentJobGatewayRegistration registration;
        try
        {
            registration = sessions.Register(session.Client, session.ConnectionId, session.ConnectionEpoch, provisional: true);
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
            await responseStream.WriteAsync(new GatewayJobFrame
            {
                ProtocolVersion = NetRatelAkkaOptions.ProtocolVersion,
                TenantId = session.Client.TenantId,
                ClientId = session.Client.AgentId.ToString("D"),
                ConnectionEpoch = session.ConnectionEpoch,
                ConnectionId = session.ConnectionId.ToString("D"),
                Sequence = 0,
                Accepted = new JobConnectAccepted { JobAuthority = Authority, Capabilities = { "lifecycle-receipts-v1" } }
            }, admissionCancellation.Token).ConfigureAwait(false);
            logger.LogInformation("Job gateway admitted. authority={Authority} tenantId={TenantId} agentId={AgentId}", Authority, session.Client.TenantId, session.Client.AgentId);

            await GatewayDuplexSession.RunAsync(async cancellationToken =>
            {
                ulong lastSequence = 0;
                while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireCurrent(registration);
                    lastSequence = await ProcessInboundAsync(requestStream.Current, session, lastSequence, registration, cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken => WriteOutboundAsync(registration, responseStream, cancellationToken),
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

    private async Task<ValidatedSession> ValidateHelloAsync(AgentJobFrame frame, AuthenticatedAgentIdentity identity, CancellationToken cancellationToken)
    {
        if (frame.PayloadCase != AgentJobFrame.PayloadOneofCase.Hello || frame.Sequence != 0 ||
            !string.Equals(frame.ProtocolVersion, NetRatelAkkaOptions.ProtocolVersion, StringComparison.Ordinal) || frame.TenantId != identity.TenantId ||
            !Guid.TryParse(frame.ClientId, out var agentId) || agentId != identity.AgentId ||
            !Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty || frame.ConnectionEpoch == 0 || frame.ConnectionEpoch > long.MaxValue)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The job gateway hello does not match the authenticated agent identity."));
        }

        var client = new ClientKey(identity.TenantId, identity.AgentId);
        await RequirePresenceAsync(client, connectionId, frame.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
        return new ValidatedSession(client, connectionId, frame.ConnectionEpoch, frame.Hello.Capabilities.Contains("lifecycle-receipts-v1"));
    }

    private async Task<ulong> ProcessInboundAsync(AgentJobFrame frame, ValidatedSession session, ulong lastSequence, AgentJobGatewayRegistration registration, CancellationToken cancellationToken)
    {
        var receivedAtUtc = clock.GetUtcNow();
        if (!MatchesSession(frame, session) || frame.Sequence == 0 || frame.Sequence <= lastSequence ||
            frame.PayloadCase != AgentJobFrame.PayloadOneofCase.Lifecycle)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The job gateway frame is invalid or stale."));
        }

        RequireCurrent(registration);
        await RequirePresenceAsync(session.Client, session.ConnectionId, session.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
        RequireCurrent(registration);
        var update = frame.Lifecycle;
        if (!TryMapStatus(update.Status, out var status) || update.JobRunId == 0 || update.JobStepId == 0 || update.JobStepRunId == 0 ||
            update.Ordinal < 0 || string.IsNullOrWhiteSpace(update.RequestId) || update.RequestId.Length > 256 ||
            string.IsNullOrWhiteSpace(update.CorrelationId) || update.CorrelationId.Length > 256 ||
            !double.IsFinite(update.ProgressPercent) || update.ProgressPercent < 0 || update.ProgressPercent > 100 ||
            update.Version == 0 || update.LifecycleSequence == 0 || update.RequestedAtUtc is null || update.StatusAtUtc is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The job lifecycle payload is invalid."));
        }
        if (System.Text.Encoding.UTF8.GetByteCount(update.ResultJson) > 64 * 1024)
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "The job lifecycle result exceeds its bounded transport limit."));

        DateTimeOffset requestedAt;
        DateTimeOffset statusAt;
        DateTimeOffset? startedAt;
        try
        {
            requestedAt = update.RequestedAtUtc.ToDateTimeOffset();
            statusAt = update.StatusAtUtc.ToDateTimeOffset();
            startedAt = update.StartedAtUtc?.ToDateTimeOffset();
        }
        catch (InvalidOperationException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The job lifecycle timestamps are invalid."));
        }

        try
        {
            RequireCurrent(registration);
            await jobs.RecordLifecycleAsync(session.Client, new JobLifecycleUpdateEnvelope(
                update.JobRunId,
                update.JobStepId,
                update.JobStepRunId,
                update.Ordinal,
                update.RequestId,
                update.CorrelationId,
                status,
                update.Version,
                update.LifecycleSequence,
                requestedAt,
                statusAt,
                update.ProgressPercent,
                string.IsNullOrWhiteSpace(update.ResultJson) ? null : update.ResultJson,
                update.HasExitCode ? update.ExitCode : null,
                AuthenticatedOwner: new OwnerKey(session.Client, session.ConnectionId,
                    checked((long)session.ConnectionEpoch)),
                ReceivedAtUtc: receivedAtUtc,
                StartedAtUtc: startedAt), cancellationToken).ConfigureAwait(false);
            RequireCurrent(registration);
            if (session.SupportsReceipts)
            {
                await registration.PublishReceiptAsync(new JobLifecycleReceipt
                {
                    JobRunId = update.JobRunId,
                    JobStepRunId = update.JobStepRunId,
                    RequestId = update.RequestId,
                    Version = update.Version,
                    LifecycleSequence = update.LifecycleSequence
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
        }

        return frame.Sequence;
    }

    private async Task RequirePresenceAsync(ClientKey client, Guid connectionId, ulong connectionEpoch, CancellationToken cancellationToken)
    {
        var presence = await presenceRouter.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (presence.Status != ClientPresenceStatus.Online || presence.ConnectionId != connectionId || presence.ConnectionEpoch != checked((long)connectionEpoch))
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The job gateway session is fenced by the active presence connection."));
        }
    }

    private async Task EnsureAgentIsActiveAsync(AuthenticatedAgentIdentity identity, CancellationToken cancellationToken)
    {
        try
        {
            var agent = await agentManagement.GetAsync(identity.TenantId, identity.AgentId, cancellationToken).ConfigureAwait(false);
            if (agent is null || !agent.IsEnabled || agent.RevokedAtUtc.HasValue)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "The agent is not active."));
            }
        }
        catch (RpcException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Job gateway admission lookup failed. tenantId={TenantId}, agentId={AgentId}", identity.TenantId, identity.AgentId);
            throw new RpcException(new Status(StatusCode.Unavailable, "Agent admission state is unavailable."));
        }
    }

    private bool MatchesSession(AgentJobFrame frame, ValidatedSession session) =>
        string.Equals(frame.ProtocolVersion, NetRatelAkkaOptions.ProtocolVersion, StringComparison.Ordinal) && frame.TenantId == session.Client.TenantId &&
        string.Equals(frame.ClientId, session.Client.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
        frame.ConnectionEpoch == session.ConnectionEpoch && string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase);

    private static bool TryMapStatus(JobLifecycleStatus status, out JobGatewayLifecycleStatus mapped)
    {
        mapped = status switch
        {
            JobLifecycleStatus.Accepted => JobGatewayLifecycleStatus.Accepted,
            JobLifecycleStatus.Started => JobGatewayLifecycleStatus.Started,
            JobLifecycleStatus.Progress => JobGatewayLifecycleStatus.Progress,
            JobLifecycleStatus.Completed => JobGatewayLifecycleStatus.Completed,
            JobLifecycleStatus.Failed => JobGatewayLifecycleStatus.Failed,
            JobLifecycleStatus.Cancelled => JobGatewayLifecycleStatus.Cancelled,
            _ => default
        };
        return status is JobLifecycleStatus.Accepted or JobLifecycleStatus.Started or JobLifecycleStatus.Progress or
            JobLifecycleStatus.Completed or JobLifecycleStatus.Failed or JobLifecycleStatus.Cancelled;
    }

    private async Task WriteOutboundAsync(AgentJobGatewayRegistration registration, IServerStreamWriter<GatewayJobFrame> responseStream, CancellationToken cancellationToken)
    {
        await foreach (var frame in registration.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            RequireCurrent(registration);
            // No DB transaction survives into this transport check/write.
            // The physical frame's original owner cannot adopt a successor.
            if (!Guid.TryParse(frame.ConnectionId, out var connectionId) || frame.ConnectionEpoch == 0 ||
                !Guid.TryParse(frame.ClientId, out var agentId))
                throw new RpcException(new Status(StatusCode.Aborted, "The outbound job owner is invalid."));
            await RequirePresenceAsync(new ClientKey(frame.TenantId, agentId), connectionId,
                frame.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
            RequireCurrent(registration);
            await responseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void RequireCurrent(AgentJobGatewayRegistration registration)
    {
        if (!registration.IsCurrent)
            throw new RpcException(new Status(StatusCode.Aborted, "The gateway registration is no longer current."));
    }

    private sealed record ValidatedSession(ClientKey Client, Guid ConnectionId, ulong ConnectionEpoch, bool SupportsReceipts);
}
