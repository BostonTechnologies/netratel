using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Observability;
using NetRatel.Application.Agents;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.API.Services.Jobs;
using NetRatel.Shared.Security;

namespace NetRatel.API.Gateway;

[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentCommandGatewayService(
    IClientCommandRouter commandRouter,
    IClientPresenceRouter presenceRouter,
    IAgentManagementService agentManagement,
    IAgentCommandGatewaySessionRegistry commandSessions,
    IJobRunService jobRuns,
    IMcpOperatorCommandStore operatorCommands,
    IMcpOperatorTaskStore operatorTasks,
    NetRatelAkkaMigrationOptions options,
    IHostEnvironment environment,
    ILogger<AgentCommandGatewayService> logger,
    TimeProvider timeProvider)
    : AgentCommandGateway.AgentCommandGatewayBase
{
    private string Authority => options.PresenceAuthority;
    private const string Feature = "commands";
    private const int MaximumResultJsonBytes = 64 * 1024;

    public override async Task Connect(
        IAsyncStreamReader<AgentCommandFrame> requestStream,
        IServerStreamWriter<GatewayCommandFrame> responseStream,
        ServerCallContext context)
    {
        if (!options.IsCommandAuthorityActive)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The command authority canary is disabled."));
        }

        if (!AgentGatewayIdentityResolver.TryResolve(context.GetHttpContext().User, out var identity, out var error) || identity is null)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, error));
        }

        await EnsureAgentIsActiveAsync(identity, context.CancellationToken).ConfigureAwait(false);
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A command gateway hello frame is required."));
        }

        var session = await ValidateHelloAsync(requestStream.Current, identity, context.CancellationToken).ConfigureAwait(false);
        AgentCommandGatewayRegistration registration;
        try
        {
            registration = commandSessions.Register(session.Client, session.ConnectionId, session.ConnectionEpoch, provisional: true);
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
            context.CancellationToken, registration.CompletionToken);
        try
        {
            await RequirePresenceAsync(session.Client, session.ConnectionId, session.ConnectionEpoch, admissionCancellation.Token).ConfigureAwait(false);
            if (!registration.Activate())
                throw new RpcException(new Status(StatusCode.Aborted, "The gateway registration was replaced during admission."));
            RequireCurrent(registration);
            await responseStream.WriteAsync(new GatewayCommandFrame
            {
                ProtocolVersion = options.ProtocolVersion,
                TenantId = session.Client.TenantId,
                ClientId = session.Client.AgentId.ToString("D"),
                ConnectionEpoch = session.ConnectionEpoch,
                ConnectionId = session.ConnectionId.ToString("D"),
                Sequence = 0,
                Accepted = new CommandConnectAccepted { CommandAuthority = Authority }
            }, admissionCancellation.Token).ConfigureAwait(false);
            logger.LogInformation("Command gateway admitted. authority={Authority} tenantId={TenantId} agentId={AgentId}", Authority, session.Client.TenantId, session.Client.AgentId);

            await GatewayDuplexSession.RunAsync(async cancellationToken =>
            {
                ulong lastSequence = 0;
                while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    RequireCurrent(registration);
                    lastSequence = await ProcessInboundAsync(requestStream.Current, session, lastSequence, registration, cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken => WriteOutboundAsync(registration, responseStream, cancellationToken),
                context.CancellationToken, registration.CompletionToken, logger).ConfigureAwait(false);
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

    private async Task<ValidatedSession> ValidateHelloAsync(AgentCommandFrame frame, AuthenticatedAgentIdentity identity, CancellationToken cancellationToken)
    {
        if (frame.PayloadCase != AgentCommandFrame.PayloadOneofCase.Hello || frame.Sequence != 0 ||
            !string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) || frame.TenantId != identity.TenantId ||
            !Guid.TryParse(frame.ClientId, out var agentId) || agentId != identity.AgentId ||
            !Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty || frame.ConnectionEpoch == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The command gateway hello does not match the authenticated agent identity."));
        }

        var client = new ClientKey(identity.TenantId, identity.AgentId);
        await RequirePresenceAsync(client, connectionId, frame.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
        return new ValidatedSession(client, connectionId, frame.ConnectionEpoch);
    }

    private async Task<ulong> ProcessInboundAsync(AgentCommandFrame frame, ValidatedSession session, ulong lastSequence, AgentCommandGatewayRegistration registration, CancellationToken cancellationToken)
    {
        var receivedAt = timeProvider.GetUtcNow();
        if (!MatchesSession(frame, session) || frame.Sequence == 0 || frame.Sequence <= lastSequence ||
            frame.PayloadCase != AgentCommandFrame.PayloadOneofCase.Lifecycle)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The command gateway frame is invalid or stale."));
        }

        RequireCurrent(registration);
        await RequirePresenceAsync(session.Client, session.ConnectionId, session.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
        RequireCurrent(registration);
        var lifecycle = frame.Lifecycle;
        if (!TryMapStatus(lifecycle.Status, out var status) || string.IsNullOrWhiteSpace(lifecycle.CommandId) ||
            string.IsNullOrWhiteSpace(lifecycle.CorrelationId) || lifecycle.Version == 0 || lifecycle.Sequence == 0 || lifecycle.RequestTimestamp is null || lifecycle.StatusTimestamp is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The command lifecycle payload is invalid."));
        }
        if (lifecycle.ResultJson.Length > MaximumResultJsonBytes || System.Text.Encoding.UTF8.GetByteCount(lifecycle.ResultJson) > MaximumResultJsonBytes)
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "The command lifecycle result exceeds its bounded transport limit."));

        if (!AgentCommandProtocolValidator.TryReadTimestamp(lifecycle.RequestTimestamp, out var requestTimestamp) ||
            !AgentCommandProtocolValidator.TryReadTimestamp(lifecycle.StatusTimestamp, out _))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The command lifecycle timestamps are invalid."));
        }

        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(Feature, Authority, status.ToString(), fallbackUsed: false, environment.EnvironmentName);
        RequireCurrent(registration);
        var result = await commandRouter.RecordAsync(new RecordCommandLifecycleEvent(new CommandLifecycleEvent(
            session.Client,
            lifecycle.CommandId,
            lifecycle.CorrelationId,
            requestTimestamp,
            receivedAt,
            lifecycle.Version,
            lifecycle.Sequence,
            status,
            Source: Authority,
            IsAuthoritative: true)), cancellationToken).ConfigureAwait(false);
        RequireCurrent(registration);
        if (result.Disposition is CommandMessageDisposition.Accepted or CommandMessageDisposition.Duplicate)
        {
            // Duplicate repair must reuse the persisted event time, rather
            // than stamping a second completion with this delivery's receipt.
            var statusTimestamp = result.CurrentStatusTimestamp
                ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "The committed command lifecycle timestamp is unavailable."));
            var safeResult = BoundResult(lifecycle.ResultJson);
            RequireCurrent(registration);
            await operatorCommands.RecordLifecycleAsync(
                lifecycle.CommandId,
                session.Client.TenantId,
                session.Client.AgentId,
                MapOperatorCommandState(status),
                statusTimestamp,
                status == CommandLifecycleStatus.Failed ? "agent_command_failed" : null,
                cancellationToken,
                lifecycle.ResultJson,
                status is CommandLifecycleStatus.Completed or CommandLifecycleStatus.Failed ? lifecycle.ExitCode : null).ConfigureAwait(false);
            RequireCurrent(registration);
            await operatorTasks.RecordLifecycleAsync(
                lifecycle.CommandId,
                session.Client.TenantId,
                session.Client.AgentId,
                MapTaskActivityStatus(status),
                safeResult,
                statusTimestamp,
                cancellationToken).ConfigureAwait(false);
            RequireCurrent(registration);
            if (result.Disposition == CommandMessageDisposition.Accepted)
            {
                var taskStatus = MapTaskActivityStatus(status);
                RequireCurrent(registration);
                await jobRuns.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(
                    lifecycle.CommandId,
                    taskStatus,
                    status is CommandLifecycleStatus.Failed ? TaskResultSummary.Failure(safeResult) : null,
                    status is CommandLifecycleStatus.Completed or CommandLifecycleStatus.Failed or CommandLifecycleStatus.Cancelled
                        ? statusTimestamp
                        : null,
                    IsTerminal(taskStatus) ? safeResult : null), cancellationToken).ConfigureAwait(false);
                RequireCurrent(registration);
            }
            NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
        }
        else
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Command lifecycle transition was rejected: {result.Disposition}."));
        }

        return frame.Sequence;
    }

    private async Task RequirePresenceAsync(ClientKey client, Guid connectionId, ulong connectionEpoch, CancellationToken cancellationToken)
    {
        var presence = await presenceRouter.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (presence.Status != ShadowPresenceStatus.Online || presence.ConnectionId != connectionId || presence.ConnectionEpoch != checked((long)connectionEpoch))
        {
            throw new RpcException(new Status(StatusCode.Aborted, "The command gateway session is fenced by the active presence connection."));
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
            logger.LogWarning(exception, "Command gateway admission lookup failed. tenantId={TenantId}, agentId={AgentId}", identity.TenantId, identity.AgentId);
            throw new RpcException(new Status(StatusCode.Unavailable, "Agent admission state is unavailable."));
        }
    }

    private bool MatchesSession(AgentCommandFrame frame, ValidatedSession session) =>
        string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) && frame.TenantId == session.Client.TenantId &&
        string.Equals(frame.ClientId, session.Client.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
        frame.ConnectionEpoch == session.ConnectionEpoch && string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase);

    private static bool TryMapStatus(CommandShadowStatus status, out CommandLifecycleStatus mapped)
    {
        mapped = status switch
        {
            CommandShadowStatus.Accepted => CommandLifecycleStatus.Accepted,
            CommandShadowStatus.Started => CommandLifecycleStatus.Started,
            CommandShadowStatus.Completed => CommandLifecycleStatus.Completed,
            CommandShadowStatus.Failed => CommandLifecycleStatus.Failed,
            CommandShadowStatus.Cancelled => CommandLifecycleStatus.Cancelled,
            _ => default
        };
        return status is CommandShadowStatus.Accepted or CommandShadowStatus.Started or CommandShadowStatus.Completed or CommandShadowStatus.Failed or CommandShadowStatus.Cancelled;
    }

    private static string MapTaskActivityStatus(CommandLifecycleStatus status) =>
        status is CommandLifecycleStatus.Accepted or CommandLifecycleStatus.Started
            ? "Processing"
            : status switch
            {
                CommandLifecycleStatus.Completed => "Completed",
                CommandLifecycleStatus.Failed => "Failed",
                CommandLifecycleStatus.Cancelled => "Cancelled",
                _ => "Pending"
            };

    private static McpOperatorCommandState MapOperatorCommandState(CommandLifecycleStatus status) => status switch
    {
        CommandLifecycleStatus.Accepted => McpOperatorCommandState.Accepted,
        CommandLifecycleStatus.Started => McpOperatorCommandState.Started,
        CommandLifecycleStatus.Completed => McpOperatorCommandState.Completed,
        CommandLifecycleStatus.Failed => McpOperatorCommandState.Failed,
        CommandLifecycleStatus.Cancelled => McpOperatorCommandState.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "The lifecycle status cannot be persisted for an operator command.")
    };

    private static string? BoundResult(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var redacted = OperatorOutputRedactor.Redact(value);
        while (System.Text.Encoding.UTF8.GetByteCount(redacted) > MaximumResultJsonBytes)
            redacted = redacted[..Math.Max(1, redacted.Length / 2)];
        return redacted;
    }

    private static bool IsTerminal(string status)
        => status is "Completed" or "Failed" or "Cancelled";

    private static async Task WriteOutboundAsync(AgentCommandGatewayRegistration registration, IServerStreamWriter<GatewayCommandFrame> responseStream, CancellationToken cancellationToken)
    {
        await foreach (var frame in registration.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            RequireCurrent(registration);
            await responseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RequireCurrent(AgentCommandGatewayRegistration registration)
    {
        if (!registration.IsCurrent)
            throw new RpcException(new Status(StatusCode.Aborted, "The gateway registration is no longer current."));
    }

    private sealed record ValidatedSession(ClientKey Client, Guid ConnectionId, ulong ConnectionEpoch);
}
