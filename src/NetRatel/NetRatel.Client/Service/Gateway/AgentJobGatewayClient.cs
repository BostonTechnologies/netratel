using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
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
/// Executes fenced job steps without subscribing to or publishing the legacy
/// SpacetimeDB job/task transport.  Each lifecycle write is serialized so the
/// server can durably validate ordered progress and terminal transitions.
/// </summary>
public sealed class AgentJobGatewayClient(
    GatewayClientOptions options,
    bool useInProcPowerShell,
    Action<string> log)
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    public async Task RunForPresenceSessionAsync(GatewayPresenceSession session, string accessToken, CancellationToken stoppingToken)
    {
        if (!options.JobAuthorityEnabled)
        {
            return;
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            log("Job gateway is disabled because Gateway:Endpoint is not an absolute HTTPS URL.");
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
                log($"Job gateway session failed: {exception.GetType().Name}: {exception.Message}. Retrying in {retryDelay.TotalSeconds:0}s.");
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
            }
        }
    }

    private async Task RunStreamAsync(Uri endpoint, GatewayPresenceSession session, string accessToken, CancellationToken stoppingToken)
    {
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new AgentJobGateway.AgentJobGatewayClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {accessToken}" } };
        using var call = client.Connect(headers, cancellationToken: stoppingToken);
        using var writer = new JobGatewayWriter(call.RequestStream, session, options.ProtocolVersion);
        using var taskManager = new ClientTaskManager(useInProcPowerShell, writer.PublishTaskStatusAsync, message => log($"Job execution error: {message}"));
        taskManager.Start(session.TenantId, environment: 0);
        try
        {
            await writer.WriteHelloAsync(stoppingToken).ConfigureAwait(false);
            if (!await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false) ||
                call.ResponseStream.Current.PayloadCase != GatewayJobFrame.PayloadOneofCase.Accepted)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Job gateway closed before accepting the session."));
            }

            writer.ValidateAccepted(call.ResponseStream.Current);
            log($"Job gateway admitted. authority={call.ResponseStream.Current.Accepted.JobAuthority}.");
            ulong lastServerSequence = 0;
            while (await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false))
            {
                var frame = call.ResponseStream.Current;
                writer.ValidateInbound(frame);
                if (frame.Sequence == 0 || frame.Sequence <= lastServerSequence)
                {
                    throw new RpcException(new Status(StatusCode.DataLoss, "Job gateway returned a stale or out-of-order frame."));
                }

                lastServerSequence = frame.Sequence;
                switch (frame.PayloadCase)
                {
                    case GatewayJobFrame.PayloadOneofCase.Dispatch:
                        await HandleDispatchAsync(frame.Dispatch, taskManager, writer, stoppingToken).ConfigureAwait(false);
                        break;
                    case GatewayJobFrame.PayloadOneofCase.Cancel:
                        if (!writer.TryGetRequestId(frame.Cancel.JobRunId, out var requestId) || !taskManager.CancelGatewayCommand(requestId))
                        {
                            log($"Job gateway cancellation ignored because run {frame.Cancel.JobRunId} is not active.");
                        }
                        break;
                    default:
                        throw new RpcException(new Status(StatusCode.DataLoss, "Job gateway returned an unsupported frame."));
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

    private static async Task HandleDispatchAsync(JobStepDispatch dispatch, ClientTaskManager taskManager, JobGatewayWriter writer, CancellationToken cancellationToken)
    {
        if (dispatch.JobRunId == 0 || dispatch.JobStepId == 0 || dispatch.JobStepRunId == 0 || dispatch.Ordinal < 0 ||
            string.IsNullOrWhiteSpace(dispatch.RequestId) || string.IsNullOrWhiteSpace(dispatch.CorrelationId) ||
            string.IsNullOrWhiteSpace(dispatch.TaskType) || dispatch.NextVersion == 0 || dispatch.NextSequence == 0 ||
            dispatch.RequestedAtUtc is null || !writer.TryRegisterDispatch(dispatch))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Job gateway dispatch is invalid or duplicated."));
        }

        await writer.PublishLifecycleAsync(dispatch.RequestId, JobLifecycleStatus.Accepted, null, null, progressPercent: 0).ConfigureAwait(false);
        if (!taskManager.EnqueueGatewayCommand(dispatch.RequestId, dispatch.TaskType, dispatch.PayloadJson, dispatch.TenantId, dispatch.Environment))
        {
            await writer.PublishLifecycleAsync(dispatch.RequestId, JobLifecycleStatus.Failed, "{\"error\":\"job_queue_unavailable\"}", 1, progressPercent: 0).ConfigureAwait(false);
        }
    }

    private sealed record JobLifecycleCursor(
        ulong JobRunId,
        ulong JobStepId,
        ulong JobStepRunId,
        int Ordinal,
        string CorrelationId,
        DateTimeOffset RequestedAtUtc,
        ulong NextVersion,
        ulong NextSequence);

    private sealed class JobGatewayWriter(IClientStreamWriter<AgentJobFrame> stream, GatewayPresenceSession session, string protocolVersion) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<string, JobLifecycleCursor> _cursors = new(StringComparer.Ordinal);
        private ulong _sequence;

        public Task WriteHelloAsync(CancellationToken cancellationToken) =>
            WriteAsync(new AgentJobFrame { Hello = new AgentJobHello { Capabilities = { "job-lifecycle-v1", "gateway-execution", "progress-v1" } } }, cancellationToken);

        public bool TryRegisterDispatch(JobStepDispatch dispatch) => _cursors.TryAdd(dispatch.RequestId, new JobLifecycleCursor(
            dispatch.JobRunId,
            dispatch.JobStepId,
            dispatch.JobStepRunId,
            dispatch.Ordinal,
            dispatch.CorrelationId,
            dispatch.RequestedAtUtc.ToDateTimeOffset(),
            dispatch.NextVersion,
            dispatch.NextSequence));

        public bool TryGetRequestId(ulong jobRunId, out string requestId)
        {
            requestId = _cursors.FirstOrDefault(pair => pair.Value.JobRunId == jobRunId).Key;
            return !string.IsNullOrWhiteSpace(requestId);
        }

        public async Task PublishTaskStatusAsync(string requestId, string status, string? resultJson, int? exitCode)
        {
            if (string.Equals(status, "started", StringComparison.OrdinalIgnoreCase))
            {
                await PublishLifecycleAsync(requestId, JobLifecycleStatus.Started, resultJson, exitCode, progressPercent: 0).ConfigureAwait(false);
                await PublishLifecycleAsync(requestId, JobLifecycleStatus.Progress, resultJson, exitCode, progressPercent: 1).ConfigureAwait(false);
                return;
            }

            var mapped = status switch
            {
                "completed" => JobLifecycleStatus.Completed,
                "failed" => JobLifecycleStatus.Failed,
                "cancelled" => JobLifecycleStatus.Cancelled,
                _ => throw new InvalidOperationException($"Unsupported gateway job status '{status}'.")
            };
            await PublishLifecycleAsync(requestId, mapped, resultJson, exitCode, progressPercent: mapped == JobLifecycleStatus.Completed ? 100 : 0).ConfigureAwait(false);
        }

        public async Task PublishLifecycleAsync(string requestId, JobLifecycleStatus status, string? resultJson, int? exitCode, double progressPercent)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_cursors.TryGetValue(requestId, out var cursor))
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow;
                await WriteLockedAsync(new AgentJobFrame
                {
                    Lifecycle = new JobLifecycleUpdate
                    {
                        JobRunId = cursor.JobRunId,
                        JobStepId = cursor.JobStepId,
                        JobStepRunId = cursor.JobStepRunId,
                        Ordinal = cursor.Ordinal,
                        RequestId = requestId,
                        CorrelationId = cursor.CorrelationId,
                        Status = status,
                        Version = cursor.NextVersion,
                        LifecycleSequence = cursor.NextSequence,
                        RequestedAtUtc = Timestamp.FromDateTimeOffset(cursor.RequestedAtUtc),
                        StatusAtUtc = Timestamp.FromDateTimeOffset(now),
                        ProgressPercent = progressPercent,
                        ResultJson = resultJson ?? string.Empty,
                        ExitCode = exitCode ?? 0
                    }
                }).ConfigureAwait(false);
                _cursors[requestId] = cursor with { NextVersion = cursor.NextVersion + 1, NextSequence = cursor.NextSequence + 1 };
                if (status is JobLifecycleStatus.Completed or JobLifecycleStatus.Failed or JobLifecycleStatus.Cancelled)
                {
                    _cursors.TryRemove(requestId, out _);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public void ValidateAccepted(GatewayJobFrame frame)
        {
            if (!Matches(frame) || frame.Sequence != 0 || !GatewayAuthority.IsAkka(frame.Accepted.JobAuthority))
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Job gateway did not admit the required Akka authority."));
            }
        }

        public void ValidateInbound(GatewayJobFrame frame)
        {
            if (!Matches(frame))
            {
                throw new RpcException(new Status(StatusCode.DataLoss, "Job gateway returned a frame for another presence session."));
            }
        }

        private async Task WriteAsync(AgentJobFrame frame, CancellationToken cancellationToken)
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

        private Task WriteLockedAsync(AgentJobFrame frame)
        {
            frame.ProtocolVersion = protocolVersion;
            frame.TenantId = session.TenantId;
            frame.ClientId = session.AgentId.ToString("D");
            frame.ConnectionEpoch = session.ConnectionEpoch;
            frame.ConnectionId = session.ConnectionId.ToString("D");
            frame.Sequence = frame.PayloadCase == AgentJobFrame.PayloadOneofCase.Hello ? 0 : ++_sequence;
            return stream.WriteAsync(frame);
        }

        private bool Matches(GatewayJobFrame frame) =>
            string.Equals(frame.ProtocolVersion, protocolVersion, StringComparison.Ordinal) && frame.TenantId == session.TenantId &&
            string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
            frame.ConnectionEpoch == session.ConnectionEpoch && string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase);

        public void Dispose() => _gate.Dispose();
    }
}
