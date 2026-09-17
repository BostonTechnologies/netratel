using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using System.Diagnostics;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Observability;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

/// <summary>
/// Authenticated, per-frame presence-fenced ingress for bounded structured
/// log batches. It deliberately has no actor or durable-store payload path.
/// </summary>
[Authorize(Policy = "AgentGatewayAccess")]
public sealed class AgentLogGatewayService(
    IClientPresenceRouter presenceRouter,
    IAgentManagementService agentManagement,
    IAgentLogGatewaySessionRegistry logSessions,
    NetRatelAkkaMigrationOptions options,
    IAgentLogGatewayQueryDispatcher? logQueries = null,
    ILogger<AgentLogGatewayService>? logger = null)
    : AgentLogGateway.AgentLogGatewayBase
{
    public override async Task Connect(
        IAsyncStreamReader<AgentLogFrame> requestStream,
        IServerStreamWriter<GatewayLogFrame> responseStream,
        ServerCallContext context)
    {
        if (!options.IsLogAuthorityActive)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The log gateway authority is disabled."));
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
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A log gateway hello frame is required."));
        }

        var session = await ValidateHelloAsync(requestStream.Current, identity, context.CancellationToken).ConfigureAwait(false);
        try
        {
            using var registration = logSessions.Register(session.Client, session.ConnectionId, session.ConnectionEpoch, requestStream.Current.Hello, provisional: true);
            using var queryRegistration = logQueries?.Register(registration, provisional: true);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, registration.CompletionToken,
                queryRegistration?.CompletionToken ?? CancellationToken.None);
            try
            {
                await RequirePresenceAsync(session.Client, session.ConnectionId, session.ConnectionEpoch, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested && lifetime.IsCancellationRequested)
            {
                throw new AgentGatewayRegistrationFencedException();
            }
            // Publish state last: query dispatch also requires this exact state to be active.
            if (!registration.IsCurrent || queryRegistration?.TryActivate() == false || !registration.TryActivate())
                throw new AgentGatewayRegistrationFencedException();

            using var activity = StartStreamActivity(requestStream.Current);
            NetRatelAkkaTelemetry.LogSessionOpened();
            try
            {
                if (!registration.IsCurrent || queryRegistration?.IsCurrent == false) throw new AgentGatewayRegistrationFencedException();
                await responseStream.WriteAsync(CreateAccepted(session, requestStream.Current), lifetime.Token).ConfigureAwait(false);
                await GatewayDuplexSession.RunAsync(ReadInboundAsync,
                    queryRegistration is null
                        ? token => Task.Delay(Timeout.InfiniteTimeSpan, token)
                        : token => WriteOutboundAsync(queryRegistration, responseStream, token),
                    lifetime.Token, registration.CompletionToken,
                    logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentLogGatewayService>.Instance).ConfigureAwait(false);

                async Task ReadInboundAsync(CancellationToken cancellationToken)
                {
                    var lastSequence = 0UL;
                    while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
                    {
                        if (!registration.IsCurrent || queryRegistration?.IsCurrent == false) return;
                        var frame = requestStream.Current;
                        if (!MatchesSession(frame, session) || frame.Sequence == 0 || frame.Sequence <= lastSequence ||
                            !Guid.TryParse(frame.OperationId, out var operationId) || operationId == Guid.Empty ||
                            frame.PayloadCase is not (AgentLogFrame.PayloadOneofCase.Batch or AgentLogFrame.PayloadOneofCase.QueryResult) || !HasValidTraceContext(frame))
                        {
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "The log gateway frame is invalid or stale."));
                        }

                        var encodedSize = frame.CalculateSize();
                        if (encodedSize > Math.Min(64 * 1024, options.MaxInboundMessageBytes))
                        {
                            throw new RpcException(new Status(StatusCode.ResourceExhausted, "The log gateway frame exceeds the configured message budget."));
                        }

                        await RequirePresenceAsync(session.Client, session.ConnectionId, session.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
                        var accepted = frame.PayloadCase switch
                        {
                            AgentLogFrame.PayloadOneofCase.Batch => registration.TryAppend(frame.Batch),
                            AgentLogFrame.PayloadOneofCase.QueryResult => queryRegistration?.TryComplete(frame.QueryResult) == true,
                            _ => false
                        };
                        if (!accepted) throw new RpcException(new Status(StatusCode.Aborted, "The log frame was rejected by the active session fence."));

                        if (frame.PayloadCase == AgentLogFrame.PayloadOneofCase.Batch)
                        {
                            NetRatelAkkaTelemetry.LogBatchAccepted(frame.Batch.Records.Count, encodedSize, frame.Batch.DroppedRecordCount, frame.Batch.ResyncRequired);
                            if (queryRegistration is null && registration.IsCurrent)
                            {
                                await responseStream.WriteAsync(CreateFlowControl(session, frame), cancellationToken).ConfigureAwait(false);
                            }
                        }
                        lastSequence = frame.Sequence;
                    }
                }
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested && lifetime.IsCancellationRequested)
            {
                throw new AgentGatewayRegistrationFencedException();
            }
            finally
            {
                NetRatelAkkaTelemetry.LogSessionClosed();
            }
        }
        catch (AgentGatewayRegistrationFencedException exception)
        {
            throw new RpcException(new Status(StatusCode.Aborted, exception.Message));
        }
    }

    private async Task<ValidatedLogSession> ValidateHelloAsync(
        AgentLogFrame frame,
        AuthenticatedAgentIdentity identity,
        CancellationToken cancellationToken)
    {
        if (frame.PayloadCase != AgentLogFrame.PayloadOneofCase.Hello || frame.Sequence != 0 ||
            !string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != identity.TenantId || !Guid.TryParse(frame.ClientId, out var agentId) || agentId != identity.AgentId ||
            !Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty ||
            !Guid.TryParse(frame.OperationId, out var operationId) || operationId == Guid.Empty || frame.ConnectionEpoch == 0 ||
            !frame.Hello.Capabilities.Contains("log-gateway", StringComparer.OrdinalIgnoreCase) ||
            !HasValidTraceContext(frame))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The log gateway hello does not match the authenticated agent identity."));
        }

        if (frame.Hello.Sources.Count == 0 || frame.Hello.Sources.Any(source =>
                string.IsNullOrWhiteSpace(source.SourceId) || source.SourceId.Length > 128 ||
                string.IsNullOrWhiteSpace(source.Kind) || source.Kind.Length > 128 ||
                source.DisplayName.Length > 256 || source.Platform.Length > 128 || source.UnavailableReason.Length > 256 ||
                source.FilterCapabilities.Count > 32 || source.FilterCapabilities.Any(capability => capability.Length > 64)))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The log gateway source descriptors are invalid."));
        }

        var client = new ClientKey(identity.TenantId, identity.AgentId);
        await RequirePresenceAsync(client, connectionId, frame.ConnectionEpoch, cancellationToken).ConfigureAwait(false);
        return new ValidatedLogSession(client, connectionId, frame.ConnectionEpoch);
    }

    private GatewayLogFrame CreateAccepted(ValidatedLogSession session, AgentLogFrame request) => new()
    {
        ProtocolVersion = options.ProtocolVersion,
        TenantId = session.Client.TenantId,
        ClientId = session.Client.AgentId.ToString("D"),
        ConnectionEpoch = session.ConnectionEpoch,
        ConnectionId = session.ConnectionId.ToString("D"),
        OperationId = request.OperationId,
        Sequence = 0,
        Traceparent = request.Traceparent,
        Tracestate = request.Tracestate,
        Accepted = new LogConnectAccepted
        {
            LogAuthority = options.PresenceAuthority,
            MaximumRecordsPerBatch = 100,
            MaximumEncodedBatchBytes = (uint)Math.Min(64 * 1024, options.MaxInboundMessageBytes)
        }
    };

    private GatewayLogFrame CreateFlowControl(ValidatedLogSession session, AgentLogFrame request) => new()
    {
        ProtocolVersion = options.ProtocolVersion,
        TenantId = session.Client.TenantId,
        ClientId = session.Client.AgentId.ToString("D"),
        ConnectionEpoch = session.ConnectionEpoch,
        ConnectionId = session.ConnectionId.ToString("D"),
        OperationId = request.OperationId,
        Sequence = request.Sequence,
        Traceparent = request.Traceparent,
        Tracestate = request.Tracestate,
        FlowControl = new LogFlowControl { AvailableCredits = 32 }
    };

    private static async Task WriteOutboundAsync(
        AgentLogQueryRegistration registration,
        IServerStreamWriter<GatewayLogFrame> responseStream,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in registration.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!registration.IsCurrent) return;
            await responseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool MatchesSession(AgentLogFrame frame, ValidatedLogSession session) =>
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
            throw new RpcException(new Status(StatusCode.Aborted, "The log gateway session is fenced by the active presence connection."));
        }
    }

    private static bool HasValidTraceContext(AgentLogFrame frame) =>
        frame.Traceparent.Length <= 256 && frame.Tracestate.Length <= 512 &&
        (string.IsNullOrWhiteSpace(frame.Traceparent) || ActivityContext.TryParse(frame.Traceparent, frame.Tracestate, out _));

    private static Activity? StartStreamActivity(AgentLogFrame frame) =>
        ActivityContext.TryParse(frame.Traceparent, frame.Tracestate, out var parentContext)
            ? NetRatelAkkaTelemetry.StartActivity("akka.log.connect", "logs", parentContext: parentContext)
            : NetRatelAkkaTelemetry.StartActivity("akka.log.connect", "logs");

    private sealed record ValidatedLogSession(ClientKey Client, Guid ConnectionId, ulong ConnectionEpoch);
}
