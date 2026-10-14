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
/// Executes fenced job steps. Execution and unacknowledged lifecycle evidence
/// belong to the presence session, so a transport reconnect cannot restart a
/// physical side effect or discard its terminal receipt.
/// </summary>
public sealed class AgentJobGatewayClient(GatewayClientOptions options, Action<string> log)
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    public async Task RunForPresenceSessionAsync(GatewayPresenceSession session, string accessToken, CancellationToken stoppingToken)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            log("Job gateway is disabled because Gateway:Endpoint is not an absolute HTTPS URL.");
            return;
        }

        using var writer = new JobGatewayWriter(session, options.ProtocolVersion, TimeProvider.System, log, stoppingToken);
        using var taskManager = new ClientTaskManager(writer.PublishTaskStatusAsync, message => log($"Job execution error: {message}"));
        taskManager.Start(session.TenantId, environment: 0);
        var retryDelay = InitialRetryDelay;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunStreamAsync(endpoint, session, accessToken, taskManager, writer, stoppingToken).ConfigureAwait(false);
                    retryDelay = InitialRetryDelay;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is RpcException or HttpRequestException or IOException)
                {
                    log($"Job gateway session failed: {exception.GetType().Name}: {exception.Message}. Retrying in {retryDelay.TotalSeconds:0}s.");
                    await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                    retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
                }
            }
        }
        finally
        {
            writer.Stop();
            taskManager.Stop();
        }
    }

    private async Task RunStreamAsync(Uri endpoint, GatewayPresenceSession session, string accessToken,
        ClientTaskManager taskManager, JobGatewayWriter writer, CancellationToken stoppingToken)
    {
        using var channel = GrpcChannel.ForAddress(endpoint);
        var client = new AgentJobGateway.AgentJobGatewayClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {session.GetAccessToken(accessToken)}" } };
        using var streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var call = client.Connect(headers, cancellationToken: streamLifetime.Token);
        try
        {
            await writer.WriteHelloAsync(call.RequestStream, stoppingToken).ConfigureAwait(false);
            if (!await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false) ||
                call.ResponseStream.Current.PayloadCase != GatewayJobFrame.PayloadOneofCase.Accepted)
                throw new RpcException(new Status(StatusCode.Unavailable, "Job gateway closed before accepting the session."));

            writer.ValidateAccepted(call.ResponseStream.Current);
            await writer.AttachAsync(call.RequestStream, streamLifetime.Token).ConfigureAwait(false);
            log($"Job gateway admitted. authority={call.ResponseStream.Current.Accepted.JobAuthority}.");
            ulong lastServerSequence = 0;
            while (await call.ResponseStream.MoveNext(stoppingToken).ConfigureAwait(false))
            {
                var frame = call.ResponseStream.Current;
                writer.ValidateInbound(frame);
                if (frame.Sequence == 0 || frame.Sequence <= lastServerSequence)
                    throw new RpcException(new Status(StatusCode.DataLoss, "Job gateway returned a stale or out-of-order frame."));
                lastServerSequence = frame.Sequence;
                switch (frame.PayloadCase)
                {
                    case GatewayJobFrame.PayloadOneofCase.Dispatch:
                        HandleDispatch(frame.Dispatch, taskManager, writer, stoppingToken);
                        break;
                    case GatewayJobFrame.PayloadOneofCase.Receipt:
                        writer.Acknowledge(frame.Receipt);
                        break;
                    case GatewayJobFrame.PayloadOneofCase.Cancel:
                        if (!writer.TryGetRequestId(frame.Cancel.JobRunId, out var requestId) || !taskManager.CancelGatewayCommand(requestId))
                            log($"Job gateway cancellation ignored because run {frame.Cancel.JobRunId} is not active.");
                        break;
                    default:
                        throw new RpcException(new Status(StatusCode.DataLoss, "Job gateway returned an unsupported frame."));
                }
            }
            if (!stoppingToken.IsCancellationRequested)
                throw new RpcException(new Status(StatusCode.Unavailable, "Job gateway closed before the presence session ended."));
        }
        finally
        {
            writer.Detach(call.RequestStream);
            streamLifetime.Cancel();
            try { await call.RequestStream.CompleteAsync().ConfigureAwait(false); }
            catch (RpcException) { log("Job gateway stream had already closed before request completion."); }
            catch (OperationCanceledException) when (streamLifetime.IsCancellationRequested) { }
        }
    }

    private void HandleDispatch(JobStepDispatch dispatch, ClientTaskManager taskManager, JobGatewayWriter writer, CancellationToken cancellationToken)
    {
        if (dispatch.JobRunId == 0 || dispatch.JobStepId == 0 || dispatch.JobStepRunId == 0 || dispatch.Ordinal < 0 ||
            dispatch.TenantId != writer.TenantId || string.IsNullOrWhiteSpace(dispatch.RequestId) || string.IsNullOrWhiteSpace(dispatch.CorrelationId) ||
            string.IsNullOrWhiteSpace(dispatch.TaskType) || dispatch.NextVersion == 0 || dispatch.NextSequence == 0 || dispatch.RequestedAtUtc is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Job gateway dispatch is invalid."));
        if (!writer.TryRegisterDispatch(dispatch))
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Job gateway dispatch was already observed; execution will not be repeated."));
        _ = AcceptAndEnqueueAsync();
        async Task AcceptAndEnqueueAsync()
        {
            try
            {
                // Keep the response reader free to consume the durable acknowledgement.
                await writer.PublishLifecycleAsync(dispatch.RequestId, JobLifecycleStatus.Accepted, null, null, 0).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!taskManager.EnqueueGatewayCommand(dispatch.RequestId, dispatch.TaskType, dispatch.PayloadJson, dispatch.TenantId, dispatch.Environment))
                    await writer.PublishLifecycleAsync(dispatch.RequestId, JobLifecycleStatus.Failed, "{\"error\":\"job_queue_unavailable\"}", null, 0).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) { log($"Job dispatch admission failed for {dispatch.RequestId}: {exception.Message}"); }
        }
    }

    private sealed record JobLifecycleCursor(ulong JobRunId, ulong JobStepId, ulong JobStepRunId, int Ordinal,
        string CorrelationId, DateTimeOffset RequestedAtUtc, ulong NextVersion, ulong NextSequence,
        DateTimeOffset? StartedAtUtc = null);

    internal sealed class JobGatewayWriter(GatewayPresenceSession session, string protocolVersion,
        TimeProvider clock, Action<string> log, CancellationToken stoppingToken) : IDisposable
    {
        private sealed record PendingLifecycle(JobLifecycleUpdate Update, TaskCompletionSource Completion);
        private readonly SemaphoreSlim _publishGate = new(1, 1);
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly object _stateGate = new();
        private readonly ConcurrentDictionary<string, JobLifecycleCursor> _cursors = new(StringComparer.Ordinal);
        // Session-local tombstones prevent a duplicate dispatch from repeating a side effect.
        private readonly ConcurrentDictionary<string, JobLifecycleReceipt> _receipts = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        private IClientStreamWriter<AgentJobFrame>? _stream;
        private CancellationToken _transportToken;
        private PendingLifecycle? _pending;
        private ulong _sequence;
        public int TenantId => session.TenantId;

        public Task WriteHelloAsync(IClientStreamWriter<AgentJobFrame> stream, CancellationToken cancellationToken) =>
            stream.WriteAsync(Stamp(new AgentJobFrame { Hello = new AgentJobHello
            {
                Capabilities = { "job-lifecycle-v1", "gateway-execution", "progress-v1", "lifecycle-receipts-v1" }
            } }, 0), cancellationToken);

        public bool TryRegisterDispatch(JobStepDispatch dispatch) => !_receipts.ContainsKey(dispatch.RequestId) &&
            _cursors.TryAdd(dispatch.RequestId, new JobLifecycleCursor(dispatch.JobRunId, dispatch.JobStepId, dispatch.JobStepRunId,
                dispatch.Ordinal, dispatch.CorrelationId, dispatch.RequestedAtUtc.ToDateTimeOffset(), dispatch.NextVersion, dispatch.NextSequence));

        public bool TryGetRequestId(ulong jobRunId, out string requestId)
        {
            requestId = _cursors.FirstOrDefault(pair => pair.Value.JobRunId == jobRunId).Key;
            return !string.IsNullOrWhiteSpace(requestId);
        }

        public Task PublishTaskStatusAsync(string requestId, string status, string? resultJson, int? exitCode)
        {
            var mapped = status.ToLowerInvariant() switch
            {
                "started" => JobLifecycleStatus.Started,
                "progress" => JobLifecycleStatus.Progress,
                "completed" => JobLifecycleStatus.Completed,
                "failed" => JobLifecycleStatus.Failed,
                "cancelled" => JobLifecycleStatus.Cancelled,
                _ => throw new InvalidOperationException($"Unsupported gateway job status '{status}'.")
            };
            return PublishLifecycleAsync(requestId, mapped, resultJson, exitCode, mapped == JobLifecycleStatus.Completed ? 100 : 0);
        }

        public async Task PublishLifecycleAsync(string requestId, JobLifecycleStatus status, string? resultJson, int? exitCode, double progressPercent)
        {
            // Capture the reporting event before waiting for a prior commit receipt.
            // A reconnect or clock correction must not rewrite its observed time.
            var eventAt = clock.GetUtcNow();
            await _publishGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (!_cursors.TryGetValue(requestId, out var cursor)) return;
                var update = new JobLifecycleUpdate
                {
                    JobRunId = cursor.JobRunId, JobStepId = cursor.JobStepId, JobStepRunId = cursor.JobStepRunId,
                    Ordinal = cursor.Ordinal, RequestId = requestId, CorrelationId = cursor.CorrelationId,
                    Status = status, Version = cursor.NextVersion, LifecycleSequence = cursor.NextSequence,
                    RequestedAtUtc = Timestamp.FromDateTimeOffset(cursor.RequestedAtUtc),
                    StatusAtUtc = Timestamp.FromDateTimeOffset(eventAt), ProgressPercent = progressPercent,
                    ResultJson = resultJson ?? string.Empty
                };
                if (exitCode.HasValue) update.ExitCode = exitCode.Value;
                var startedAt = status == JobLifecycleStatus.Started ? eventAt : cursor.StartedAtUtc;
                if (startedAt.HasValue) update.StartedAtUtc = Timestamp.FromDateTimeOffset(startedAt.Value);
                var pending = new PendingLifecycle(update, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                lock (_stateGate) _pending = pending;
                await SendPendingAsync(pending).ConfigureAwait(false);
                // Neither the cursor nor the terminal tombstone advances on WriteAsync.
                await pending.Completion.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            }
            finally { _publishGate.Release(); }
        }

        public async Task AttachAsync(IClientStreamWriter<AgentJobFrame> stream, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                PendingLifecycle? pending;
                lock (_stateGate)
                {
                    _stream = stream;
                    _transportToken = cancellationToken;
                    _sequence = 0;
                    pending = _pending;
                }
                if (pending is not null) await WritePendingLockedAsync(pending, stream, cancellationToken).ConfigureAwait(false);
            }
            catch { Detach(stream); throw; }
            finally { _writeGate.Release(); }
        }

        public void Detach(IClientStreamWriter<AgentJobFrame> stream)
        {
            lock (_stateGate) if (ReferenceEquals(_stream, stream)) _stream = null;
        }

        private async Task SendPendingAsync(PendingLifecycle pending)
        {
            await _writeGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            IClientStreamWriter<AgentJobFrame>? stream = null;
            try
            {
                CancellationToken cancellationToken;
                lock (_stateGate) { stream = _stream; cancellationToken = _transportToken; }
                if (stream is not null) await WritePendingLockedAsync(pending, stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is RpcException or IOException or OperationCanceledException)
            {
                if (stream is not null) Detach(stream);
                log($"Job lifecycle receipt retained for reconnect: {exception.GetType().Name}.");
            }
            finally { _writeGate.Release(); }
        }

        private Task WritePendingLockedAsync(PendingLifecycle pending, IClientStreamWriter<AgentJobFrame> stream, CancellationToken cancellationToken)
        {
            lock (_stateGate)
            {
                if (!ReferenceEquals(_pending, pending)) return Task.CompletedTask;
                return stream.WriteAsync(Stamp(new AgentJobFrame { Lifecycle = pending.Update.Clone() }, ++_sequence), cancellationToken);
            }
        }

        public void Acknowledge(JobLifecycleReceipt receipt)
        {
            lock (_stateGate)
            {
                if (_receipts.TryGetValue(receipt.RequestId, out var previous) && previous.Equals(receipt)) return;
                var pending = _pending;
                if (pending is null || receipt.JobRunId != pending.Update.JobRunId || receipt.JobStepRunId != pending.Update.JobStepRunId ||
                    !string.Equals(receipt.RequestId, pending.Update.RequestId, StringComparison.Ordinal) ||
                    receipt.Version != pending.Update.Version || receipt.LifecycleSequence != pending.Update.LifecycleSequence)
                    throw new RpcException(new Status(StatusCode.DataLoss, "Job gateway receipt does not match pending lifecycle evidence."));
                var cursor = _cursors[receipt.RequestId];
                _receipts[receipt.RequestId] = receipt.Clone();
                if (pending.Update.Status is JobLifecycleStatus.Completed or JobLifecycleStatus.Failed or JobLifecycleStatus.Cancelled)
                    _cursors.TryRemove(receipt.RequestId, out _);
                else
                    _cursors[receipt.RequestId] = cursor with
                    {
                        NextVersion = checked(cursor.NextVersion + 1), NextSequence = checked(cursor.NextSequence + 1),
                        StartedAtUtc = pending.Update.StartedAtUtc?.ToDateTimeOffset()
                    };
                _pending = null;
                pending.Completion.TrySetResult();
            }
        }

        public void ValidateAccepted(GatewayJobFrame frame)
        {
            if (!Matches(frame) || frame.Sequence != 0 || !GatewayWireProtocol.HasAkkaAuthority(frame.Accepted.JobAuthority) ||
                !frame.Accepted.Capabilities.Contains("lifecycle-receipts-v1"))
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Job gateway does not support durable lifecycle receipts."));
        }

        public void ValidateInbound(GatewayJobFrame frame)
        {
            if (!Matches(frame)) throw new RpcException(new Status(StatusCode.DataLoss, "Job gateway returned a frame for another presence session."));
        }

        private AgentJobFrame Stamp(AgentJobFrame frame, ulong sequence)
        {
            frame.ProtocolVersion = protocolVersion; frame.TenantId = session.TenantId;
            frame.ClientId = session.AgentId.ToString("D"); frame.ConnectionEpoch = session.ConnectionEpoch;
            frame.ConnectionId = session.ConnectionId.ToString("D"); frame.Sequence = sequence;
            return frame;
        }

        private bool Matches(GatewayJobFrame frame) => string.Equals(frame.ProtocolVersion, protocolVersion, StringComparison.Ordinal) &&
            frame.TenantId == session.TenantId && string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
            frame.ConnectionEpoch == session.ConnectionEpoch && string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase);

        public void Stop() => _lifetime.Cancel();
        public void Dispose() { Stop(); _lifetime.Dispose(); }
    }
}
