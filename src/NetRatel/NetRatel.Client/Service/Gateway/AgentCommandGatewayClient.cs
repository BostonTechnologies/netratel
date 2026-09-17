using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Tasks;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Executes commands received from the fenced Akka command gateway. When the
/// authority flag is on this class is the only command transport: it neither
/// subscribes to nor publishes SpacetimeDB command events.
/// </summary>
public sealed class AgentCommandGatewayClient(
    GatewayClientOptions options,
    bool useInProcPowerShell,
    Action<string> log)
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    public async Task RunForPresenceSessionAsync(GatewayPresenceSession session, string accessToken, CancellationToken stoppingToken)
    {
        if (!options.CommandAuthorityEnabled)
        {
            return;
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            log("Command gateway is disabled because Gateway:Endpoint is not an absolute HTTPS URL.");
            return;
        }

        var retryDelay = InitialRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunStreamAsync(endpoint, session, accessToken, stoppingToken).ConfigureAwait(false);
                retryDelay = InitialRetryDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is RpcException or HttpRequestException or IOException)
            {
                log($"Command gateway session failed: {exception.GetType().Name}: {exception.Message}. Retrying in {retryDelay.TotalSeconds:0}s.");
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
            }
        }
    }

    private async Task RunStreamAsync(Uri endpoint, GatewayPresenceSession session, string accessToken, CancellationToken stoppingToken)
    {
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new AgentCommandGateway.AgentCommandGatewayClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {accessToken}" } };
        using var call = client.Connect(headers, cancellationToken: stoppingToken);
        using var writer = new CommandGatewayWriter(call.RequestStream, session, options.ProtocolVersion);
        using var taskManager = new ClientTaskManager(useInProcPowerShell, writer.PublishStatusAsync, message => log($"Command execution error: {message}"));
        taskManager.Start(session.TenantId, environment: 0);
        try
        {
            await writer.WriteHelloAsync(stoppingToken).ConfigureAwait(false);
            if (!await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false) ||
                call.ResponseStream.Current.PayloadCase != GatewayCommandFrame.PayloadOneofCase.Accepted)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Command gateway closed before accepting the session."));
            }

            writer.ValidateAccepted(call.ResponseStream.Current);
            log($"Command gateway admitted. authority={call.ResponseStream.Current.Accepted.CommandAuthority}.");
            ulong lastServerSequence = 0;
            while (await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false))
            {
                var frame = call.ResponseStream.Current;
                writer.ValidateInbound(frame);
                if (frame.Sequence == 0 || frame.Sequence <= lastServerSequence)
                {
                    throw new RpcException(new Status(StatusCode.DataLoss, "Command gateway returned a stale or out-of-order frame."));
                }

                lastServerSequence = frame.Sequence;
                switch (frame.PayloadCase)
                {
                    case GatewayCommandFrame.PayloadOneofCase.Dispatch:
                        await HandleDispatchAsync(frame.Dispatch, taskManager, writer, stoppingToken).ConfigureAwait(false);
                        break;
                    case GatewayCommandFrame.PayloadOneofCase.Cancel:
                        if (!taskManager.CancelGatewayCommand(frame.Cancel.CommandId))
                        {
                            log($"Command gateway cancellation ignored because command {frame.Cancel.CommandId} is not active.");
                        }
                        break;
                    default:
                        throw new RpcException(new Status(StatusCode.DataLoss, "Command gateway returned an unsupported frame."));
                }
            }
        }
        finally
        {
            taskManager.Stop();
            try
            {
                await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            }
            catch (RpcException)
            {
                // The gateway already closed the stream.
            }
        }
    }

    private static async Task HandleDispatchAsync(
        CommandDispatch dispatch,
        ClientTaskManager taskManager,
        CommandGatewayWriter writer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dispatch.CommandId) || string.IsNullOrWhiteSpace(dispatch.CorrelationId) ||
            string.IsNullOrWhiteSpace(dispatch.TaskType) || dispatch.NextVersion == 0 || dispatch.NextSequence == 0 || dispatch.RequestTimestamp is null ||
            !writer.TryRegisterDispatch(dispatch))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Command gateway dispatch is invalid or duplicated."));
        }

        await writer.PublishStatusAsync(dispatch.CommandId, "accepted", null, null).ConfigureAwait(false);
        if (!taskManager.EnqueueGatewayCommand(dispatch.CommandId, dispatch.TaskType, dispatch.PayloadJson, dispatch.TenantId, dispatch.Environment))
        {
            await writer.PublishStatusAsync(dispatch.CommandId, "failed", "{\"error\":\"command_queue_unavailable\"}", 1).ConfigureAwait(false);
        }
    }

    private sealed record CommandLifecycleCursor(string CorrelationId, DateTimeOffset RequestTimestamp, ulong NextVersion, ulong NextSequence);

    private sealed class CommandGatewayWriter(IClientStreamWriter<AgentCommandFrame> stream, GatewayPresenceSession session, string protocolVersion) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<string, CommandLifecycleCursor> _cursors = new(StringComparer.Ordinal);
        private ulong _sequence;

        public async Task WriteHelloAsync(CancellationToken cancellationToken)
        {
            await WriteAsync(new AgentCommandFrame { Hello = new AgentCommandHello { Capabilities = { "command-lifecycle-v1", "gateway-execution" } } }, cancellationToken).ConfigureAwait(false);
        }

        public bool TryRegisterDispatch(CommandDispatch dispatch) =>
            _cursors.TryAdd(dispatch.CommandId, new CommandLifecycleCursor(
                dispatch.CorrelationId,
                dispatch.RequestTimestamp.ToDateTimeOffset(),
                dispatch.NextVersion,
                dispatch.NextSequence));

        public async Task PublishStatusAsync(string commandId, string status, string? resultJson, int? exitCode)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_cursors.TryGetValue(commandId, out var cursor))
                {
                    return;
                }

                var mapped = status switch
                {
                    "accepted" => CommandShadowStatus.Accepted,
                    "started" => CommandShadowStatus.Started,
                    "completed" => CommandShadowStatus.Completed,
                    "failed" => CommandShadowStatus.Failed,
                    "cancelled" => CommandShadowStatus.Cancelled,
                    _ => throw new InvalidOperationException($"Unsupported gateway command status '{status}'.")
                };
                var now = DateTimeOffset.UtcNow;
                await WriteLockedAsync(new AgentCommandFrame
                {
                    Lifecycle = new CommandLifecycleUpdate
                    {
                        CommandId = commandId,
                        CorrelationId = cursor.CorrelationId,
                        RequestTimestamp = Timestamp.FromDateTimeOffset(cursor.RequestTimestamp),
                        StatusTimestamp = Timestamp.FromDateTimeOffset(now),
                        Version = cursor.NextVersion,
                        Sequence = cursor.NextSequence,
                        Status = mapped,
                        ResultJson = resultJson ?? string.Empty,
                        ExitCode = exitCode ?? 0
                    }
                }).ConfigureAwait(false);
                _cursors[commandId] = cursor with { NextVersion = cursor.NextVersion + 1, NextSequence = cursor.NextSequence + 1 };
            }
            finally
            {
                _gate.Release();
            }
        }

        public void ValidateAccepted(GatewayCommandFrame frame)
        {
            if (!Matches(frame) || frame.Sequence != 0 || !GatewayAuthority.IsAkka(frame.Accepted.CommandAuthority))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Command gateway did not admit the required Akka authority."));
            }
        }

        public void ValidateInbound(GatewayCommandFrame frame)
        {
            if (!Matches(frame))
            {
                throw new RpcException(new Status(StatusCode.DataLoss, "Command gateway returned a frame for another presence session."));
            }
        }

        private async Task WriteAsync(AgentCommandFrame frame, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteLockedAsync(frame).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private Task WriteLockedAsync(AgentCommandFrame frame)
        {
            frame.ProtocolVersion = protocolVersion;
            frame.TenantId = session.TenantId;
            frame.ClientId = session.AgentId.ToString("D");
            frame.ConnectionEpoch = session.ConnectionEpoch;
            frame.ConnectionId = session.ConnectionId.ToString("D");
            frame.Sequence = frame.PayloadCase == AgentCommandFrame.PayloadOneofCase.Hello ? 0 : ++_sequence;
            return stream.WriteAsync(frame);
        }

        private bool Matches(GatewayCommandFrame frame) =>
            string.Equals(frame.ProtocolVersion, protocolVersion, StringComparison.Ordinal) && frame.TenantId == session.TenantId &&
            string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
            frame.ConnectionEpoch == session.ConnectionEpoch && string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase);

        public void Dispose() => _gate.Dispose();
    }
}
