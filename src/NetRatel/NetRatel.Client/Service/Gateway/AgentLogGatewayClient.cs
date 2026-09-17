using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Logging;
using System.Threading.Channels;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Streams the already-bounded runtime logging observation buffer for one
/// admitted presence session. The log writer uses TryWrite only and therefore
/// cannot be stalled by this gateway or a browser consumer.
/// </summary>
public sealed class AgentLogGatewayClient(GatewayClientOptions options)
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private const int MaximumGatewayFrameBytes = 64 * 1024;
    private const int QueryResultFrameOverheadBytes = 2 * 1024;
    private const string TruncationSuffix = " … [truncated]";

    public async Task RunForPresenceSessionAsync(GatewayPresenceSession session, string accessToken, CancellationToken stoppingToken)
    {
        if (!options.LogGatewayEnabled) return;
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps) return;

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
                LogManager.WriteLog($"[Gateway] Log stream retrying after {DescribeRetry(exception)}.");
                await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
            }
        }
    }

    private async Task RunStreamAsync(Uri endpoint, GatewayPresenceSession session, string accessToken, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient(new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        })
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpClient = httpClient });
        var client = new AgentLogGateway.AgentLogGatewayClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {accessToken}" } };
        using var call = client.Connect(headers, cancellationToken: cancellationToken);
        var records = Channel.CreateBounded<ClientRuntimeLogRecord>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var provider = ClientLogSourceAdapterFactory.Create();
        var discoveredSources = await provider.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        using var writeGate = new SemaphoreSlim(1, 1);
        using var followCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeFollows = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var followTasks = new ConcurrentBag<Task>();
        long dropped = 0;
        long streamSequence = 0;
        void Capture(ClientRuntimeLogRecord record)
        {
            if (!records.Writer.TryWrite(record)) Interlocked.Increment(ref dropped);
        }

        ClientRuntimeLogBuffer.RecordCaptured += Capture;
        try
        {
            var operationId = Guid.NewGuid();
            await WriteFrameAsync(call.RequestStream, CreateHello(session, operationId, discoveredSources), writeGate, cancellationToken).ConfigureAwait(false);
            if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false) ||
                call.ResponseStream.Current.PayloadCase != GatewayLogFrame.PayloadOneofCase.Accepted)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Log gateway closed before accepting the session."));
            }

            ValidateAccepted(call.ResponseStream.Current, session, operationId);
            var maximumEncodedFrameBytes = checked((int)call.ResponseStream.Current.Accepted.MaximumEncodedBatchBytes);
            var responseDrain = DrainResponsesAsync(call.ResponseStream, session, async query =>
            {
                await HandleQueryAsync(query, provider, activeFollows, followTasks, followCancellation.Token, maximumEncodedFrameBytes,
                    record => WriteRecordAsync(call.RequestStream, session, checked((ulong)Interlocked.Increment(ref streamSequence)), record, 0, writeGate, maximumEncodedFrameBytes, cancellationToken),
                    result => WriteQueryResultAsync(call.RequestStream, session, checked((ulong)Interlocked.Increment(ref streamSequence)), result, writeGate, maximumEncodedFrameBytes, cancellationToken),
                    () => WriteResyncAsync(call.RequestStream, session, checked((ulong)Interlocked.Increment(ref streamSequence)), writeGate, cancellationToken)).ConfigureAwait(false);
            }, cancellationToken);
            var snapshot = ClientRuntimeLogBuffer.Snapshot();
            var lastSnapshotRecordSequence = snapshot.Records.LastOrDefault()?.Sequence ?? 0;
            foreach (var record in snapshot.Records)
            {
                await WriteRecordAsync(call.RequestStream, session, checked((ulong)Interlocked.Increment(ref streamSequence)), record, Interlocked.Read(ref dropped), writeGate, maximumEncodedFrameBytes, cancellationToken).ConfigureAwait(false);
            }

            await foreach (var record in records.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // The observer is attached before Snapshot so it cannot miss a
                // record. Ignore the overlap rather than treating the same
                // cursor as a duplicate log event.
                if (record.Sequence <= lastSnapshotRecordSequence) continue;
                await WriteRecordAsync(call.RequestStream, session, checked((ulong)Interlocked.Increment(ref streamSequence)), record, Interlocked.Exchange(ref dropped, 0), writeGate, maximumEncodedFrameBytes, cancellationToken).ConfigureAwait(false);
            }

            followCancellation.Cancel();
            await Task.WhenAll(followTasks.ToArray()).ConfigureAwait(false);
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            await responseDrain.ConfigureAwait(false);
        }
        finally
        {
            ClientRuntimeLogBuffer.RecordCaptured -= Capture;
            records.Writer.TryComplete();
            followCancellation.Cancel();
            foreach (var source in activeFollows.Values) source.Cancel();
        }
    }

    private AgentLogFrame CreateHello(GatewayPresenceSession session, Guid operationId, IReadOnlyList<ClientLogSourceDescriptor> discoveredSources)
    {
        var frame = new AgentLogFrame
        {
            ProtocolVersion = options.ProtocolVersion,
            TenantId = session.TenantId,
            ClientId = session.AgentId.ToString("D"),
            ConnectionEpoch = session.ConnectionEpoch,
            ConnectionId = session.ConnectionId.ToString("D"),
            OperationId = operationId.ToString("D"),
            Sequence = 0,
            Traceparent = Activity.Current?.Id ?? string.Empty,
            Tracestate = Activity.Current?.TraceStateString ?? string.Empty,
            Hello = new AgentLogHello
            {
                AgentVersion = typeof(AgentLogGatewayClient).Assembly.GetName().Version?.ToString() ?? "unknown",
                Capabilities = { "log-gateway" },
                Sources = { RuntimeSource() }
            }
        };
        foreach (var source in discoveredSources)
        {
            var descriptor = new LogSourceDescriptor
            {
                SourceId = source.SourceId,
                Kind = source.Kind,
                DisplayName = source.DisplayName,
                Platform = source.Platform,
                Available = source.Available,
                UnavailableReason = source.UnavailableReason ?? string.Empty,
                SupportsLive = source.SupportsLive,
                SupportsHistory = source.SupportsHistory,
                SupportsPaging = source.SupportsPaging
            };
            descriptor.FilterCapabilities.AddRange(source.FilterCapabilities);
            frame.Hello.Sources.Add(descriptor);
        }
        return frame;
    }

    private static LogSourceDescriptor RuntimeSource() => new()
    {
        SourceId = "netratel-runtime",
        Kind = "runtime",
        DisplayName = "NetRatel Client Logs",
        Platform = OperatingSystem.IsWindows() ? "windows" : "linux",
        Available = true,
        SupportsLive = true,
        SupportsHistory = true,
        SupportsPaging = true,
        FilterCapabilities = { "severity", "prefix", "category", "text" }
    };

    private async Task WriteRecordAsync(
        IClientStreamWriter<AgentLogFrame> stream,
        GatewayPresenceSession session,
        ulong streamSequence,
        ClientRuntimeLogRecord record,
        long droppedRecordCount,
        SemaphoreSlim writeGate,
        int maximumEncodedFrameBytes,
        CancellationToken cancellationToken)
    {
        var frame = new AgentLogFrame
        {
            ProtocolVersion = options.ProtocolVersion,
            TenantId = session.TenantId,
            ClientId = session.AgentId.ToString("D"),
            ConnectionEpoch = session.ConnectionEpoch,
            ConnectionId = session.ConnectionId.ToString("D"),
            OperationId = Guid.NewGuid().ToString("D"),
            Sequence = streamSequence,
            Traceparent = Activity.Current?.Id ?? string.Empty,
            Tracestate = Activity.Current?.TraceStateString ?? string.Empty,
            Batch = new AgentLogBatch
            {
                SessionId = session.ConnectionId.ToString("D"),
                NextCursor = record.Cursor,
                DroppedRecordCount = checked((ulong)Math.Max(0, droppedRecordCount)),
                ResyncRequired = droppedRecordCount > 0
            }
        };
        frame.Batch.Records.Add(new AgentLogRecord
        {
            Cursor = record.Cursor,
            RecordSequence = record.Sequence,
            TimestampUtc = Timestamp.FromDateTimeOffset(record.TimestampUtc),
            Severity = record.Severity,
            SourceId = record.SourceId,
            Category = record.Category,
            Prefix = record.Prefix ?? string.Empty,
            Provider = record.Provider,
            Message = record.Message,
            Truncated = record.Truncated,
            EventId = record.EventId ?? 0,
            RecordId = record.RecordId ?? 0,
            Machine = record.Machine ?? string.Empty
        });
        if (!TrimToFrameBudget(frame.Batch.Records[0], frame.CalculateSize, maximumEncodedFrameBytes))
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "The log record metadata exceeds the gateway frame budget."));
        }
        await WriteFrameAsync(stream, frame, writeGate, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteQueryResultAsync(
        IClientStreamWriter<AgentLogFrame> stream,
        GatewayPresenceSession session,
        ulong streamSequence,
        LogQueryResult result,
        SemaphoreSlim writeGate,
        int maximumEncodedFrameBytes,
        CancellationToken cancellationToken)
    {
        var frame = new AgentLogFrame
        {
            ProtocolVersion = options.ProtocolVersion,
            TenantId = session.TenantId,
            ClientId = session.AgentId.ToString("D"),
            ConnectionEpoch = session.ConnectionEpoch,
            ConnectionId = session.ConnectionId.ToString("D"),
            OperationId = Guid.NewGuid().ToString("D"),
            Sequence = streamSequence,
            Traceparent = Activity.Current?.Id ?? string.Empty,
            Tracestate = Activity.Current?.TraceStateString ?? string.Empty,
            QueryResult = result
        };
        if (frame.CalculateSize() > Math.Clamp(maximumEncodedFrameBytes, 1, MaximumGatewayFrameBytes))
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "The log query result exceeds the gateway frame budget."));
        }
        await WriteFrameAsync(stream, frame, writeGate, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteFrameAsync(IClientStreamWriter<AgentLogFrame> stream, AgentLogFrame frame, SemaphoreSlim writeGate, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await stream.WriteAsync(frame).ConfigureAwait(false); }
        finally { writeGate.Release(); }
    }

    private async Task HandleQueryAsync(
        LogQueryRequest request,
        IClientLogSourceAdapter provider,
        ConcurrentDictionary<string, CancellationTokenSource> activeFollows,
        ConcurrentBag<Task> followTasks,
        CancellationToken stoppingToken,
        int maximumEncodedFrameBytes,
        Func<ClientRuntimeLogRecord, Task> writeRecord,
        Func<LogQueryResult, Task> writeResult,
        Func<Task> writeResync)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.SourceId) || request.PageSize > 100 ||
            request.Text.Length > 512 || request.Severities.Count > 8 || request.Prefixes.Count > 32 || request.Categories.Count > 32 || request.Providers.Count > 32 || request.EventIds.Count > 32 ||
            request.Categories.Any(category => category.Length > 256) || request.Providers.Any(provider => provider.Length > 256)) return;
        var query = new ClientLogQuery(request.SourceId, request.Cursor, Math.Clamp((int)request.PageSize, 1, 100),
            request.FromUtc?.ToDateTimeOffset(), request.ToUtc?.ToDateTimeOffset(), request.Severities.ToArray(), request.Prefixes.ToArray(), request.Text,
            request.Providers.ToArray(), request.EventIds.ToArray(), request.Categories.ToArray());
        switch (request.Operation)
        {
            case LogQueryOperation.History:
                await SendPageAsync(request.RequestId, await provider.ReadHistoryAsync(query, stoppingToken).ConfigureAwait(false), writeResult, QueryResultBudget(maximumEncodedFrameBytes)).ConfigureAwait(false);
                break;
            case LogQueryOperation.StartLive:
                {
                    if (activeFollows.TryRemove(request.SourceId, out var old)) { old.Cancel(); old.Dispose(); }
                    var page = await provider.ReadHistoryAsync(query, stoppingToken).ConfigureAwait(false);
                    await SendPageAsync(request.RequestId, page, writeResult, QueryResultBudget(maximumEncodedFrameBytes)).ConfigureAwait(false);
                    // Live delivery is shared per agent/source. Keep that stream
                    // broad so one operator's typed filters never hide records
                    // from another; each explorer applies its own filter locally.
                    var followQuery = query with { Cursor = null, Severities = [], Prefixes = [], Text = null, Providers = [], EventIds = [], Categories = [] };
                    var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    activeFollows[request.SourceId] = sourceCancellation;
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var item in provider.FollowAsync(followQuery, sourceCancellation.Token).ConfigureAwait(false))
                            {
                                if (item.Record is { } record)
                                {
                                    await writeRecord(record).ConfigureAwait(false);
                                }
                                else if (item.ResyncRequired)
                                {
                                    await writeResync().ConfigureAwait(false);
                                }
                            }
                        }
                        catch (OperationCanceledException) when (sourceCancellation.IsCancellationRequested)
                        {
                            System.Diagnostics.Trace.WriteLine("The selected log source was stopped.");
                        }
                        finally
                        {
                            if (activeFollows.TryGetValue(request.SourceId, out var current) && ReferenceEquals(current, sourceCancellation))
                                activeFollows.TryRemove(request.SourceId, out _);
                            sourceCancellation.Dispose();
                        }
                    }, CancellationToken.None);
                    followTasks.Add(task);
                    break;
                }
            case LogQueryOperation.StopLive:
                if (activeFollows.TryRemove(request.SourceId, out var stoppedSource)) { stoppedSource.Cancel(); stoppedSource.Dispose(); }
                await writeResult(new LogQueryResult { RequestId = request.RequestId }).ConfigureAwait(false);
                break;
            default:
                await writeResult(new LogQueryResult { RequestId = request.RequestId, ErrorCode = "invalid_operation" }).ConfigureAwait(false);
                break;
        }
    }

    private static async Task SendPageAsync(string requestId, ClientLogPage page, Func<LogQueryResult, Task> writeResult, int maximumEncodedFrameBytes)
    {
        var result = new LogQueryResult
        {
            RequestId = requestId,
            NextCursor = page.NextCursor ?? string.Empty,
            PreviousCursor = page.PreviousCursor ?? string.Empty,
            HasMore = page.HasMore,
            DroppedRecordCount = page.DroppedRecordCount,
            ResyncRequired = page.ResyncRequired,
            ErrorCode = page.ErrorCode ?? string.Empty
        };
        var candidates = page.Records.TakeLast(100).ToArray();
        foreach (var record in candidates.Reverse())
        {
            var agentRecord = ToAgentRecord(record);
            result.Records.Insert(0, agentRecord);
            if (TrimToFrameBudget(agentRecord, result.CalculateSize, maximumEncodedFrameBytes)) continue;

            result.Records.RemoveAt(0);
            break;
        }

        if (result.Records.Count != candidates.Length)
        {
            result.HasMore = true;
            result.PreviousCursor = result.Records.Count > 0
                ? BeforeCursor(result.Records[0].Cursor)
                : string.Empty;
            if (result.Records.Count == 0)
            {
                result.ResyncRequired = true;
                result.ErrorCode = "response_budget_exceeded";
            }
        }
        await writeResult(result).ConfigureAwait(false);
    }

    private static bool TrimToFrameBudget(AgentLogRecord record, Func<int> frameSize, int maximumEncodedFrameBytes)
    {
        var budget = Math.Clamp(maximumEncodedFrameBytes, 1, MaximumGatewayFrameBytes);
        if (frameSize() <= budget) return true;

        var originalMessage = record.Message;
        var low = 0;
        var high = originalMessage.Length;
        var best = -1;
        while (low <= high)
        {
            var length = low + ((high - low) / 2);
            record.Message = string.Concat(originalMessage.AsSpan(0, length), TruncationSuffix.AsSpan());
            record.Truncated = true;
            if (frameSize() <= budget)
            {
                best = length;
                low = length + 1;
            }
            else
            {
                high = length - 1;
            }
        }

        if (best < 0) return false;
        record.Message = string.Concat(originalMessage.AsSpan(0, best), TruncationSuffix.AsSpan());
        return true;
    }

    private static int QueryResultBudget(int maximumEncodedFrameBytes) => Math.Max(1, Math.Clamp(maximumEncodedFrameBytes, 1, MaximumGatewayFrameBytes) - QueryResultFrameOverheadBytes);

    private static string BeforeCursor(string cursor) => cursor.StartsWith("before:", StringComparison.Ordinal) ? cursor : $"before:{cursor}";

    private static AgentLogRecord ToAgentRecord(ClientRuntimeLogRecord record) => new()
    {
        Cursor = record.Cursor,
        RecordSequence = record.Sequence,
        TimestampUtc = Timestamp.FromDateTimeOffset(record.TimestampUtc),
        Severity = record.Severity,
        SourceId = record.SourceId,
        Category = record.Category,
        Prefix = record.Prefix ?? string.Empty,
        Provider = record.Provider,
        Message = record.Message,
        Truncated = record.Truncated,
        EventId = record.EventId ?? 0,
        RecordId = record.RecordId ?? 0,
        Machine = record.Machine ?? string.Empty
    };

    private async Task WriteResyncAsync(
        IClientStreamWriter<AgentLogFrame> stream,
        GatewayPresenceSession session,
        ulong streamSequence,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        var frame = new AgentLogFrame
        {
            ProtocolVersion = options.ProtocolVersion,
            TenantId = session.TenantId,
            ClientId = session.AgentId.ToString("D"),
            ConnectionEpoch = session.ConnectionEpoch,
            ConnectionId = session.ConnectionId.ToString("D"),
            OperationId = Guid.NewGuid().ToString("D"),
            Sequence = streamSequence,
            Traceparent = Activity.Current?.Id ?? string.Empty,
            Tracestate = Activity.Current?.TraceStateString ?? string.Empty,
            Batch = new AgentLogBatch
            {
                SessionId = session.ConnectionId.ToString("D"),
                ResyncRequired = true
            }
        };
        await WriteFrameAsync(stream, frame, writeGate, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DrainResponsesAsync(IAsyncStreamReader<GatewayLogFrame> stream, GatewayPresenceSession session, Func<LogQueryRequest, Task> onQuery, CancellationToken cancellationToken)
    {
        while (await stream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            var frame = stream.Current;
            if (frame.TenantId != session.TenantId || !string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                frame.ConnectionEpoch != session.ConnectionEpoch || !string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                throw new RpcException(new Status(StatusCode.DataLoss, "Log gateway returned a frame for another presence session."));
            }
            if (frame.PayloadCase == GatewayLogFrame.PayloadOneofCase.Query) await onQuery(frame.Query).ConfigureAwait(false);
        }
    }

    private static string DescribeRetry(Exception exception) => exception switch
    {
        RpcException { StatusCode: var statusCode } => $"gRPC status {statusCode}",
        HttpRequestException { StatusCode: { } statusCode } => $"HTTP status {(int)statusCode}",
        HttpRequestException => "HTTP transport error",
        IOException => "I/O transport error",
        _ => "transport error"
    };

    private void ValidateAccepted(GatewayLogFrame frame, GatewayPresenceSession session, Guid operationId)
    {
        if (frame.Sequence != 0 || !string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            !string.Equals(frame.OperationId, operationId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            !GatewayAuthority.MatchesRequired(frame.Accepted.LogAuthority, options.RequiredPresenceAuthority) ||
            frame.Accepted.MaximumRecordsPerBatch is 0 or > 100 || frame.Accepted.MaximumEncodedBatchBytes is 0 or > MaximumGatewayFrameBytes)
        {
            throw new RpcException(new Status(StatusCode.DataLoss, "Log gateway returned an invalid acknowledgement."));
        }

        if (frame.TenantId != session.TenantId || !string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != session.ConnectionEpoch || !string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(StatusCode.DataLoss, "Log gateway accepted another presence session."));
        }
    }
}
