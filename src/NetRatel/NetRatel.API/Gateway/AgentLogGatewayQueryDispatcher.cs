using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Gateway;

/// <summary>
/// Bounded server-to-agent request path for log providers. It deliberately
/// owns no raw-record cache; completed live batches continue through the
/// existing transient session registry and OperationsHub fan-out.
/// </summary>
public interface IAgentLogGatewayQueryDispatcher
{
    AgentLogQueryRegistration Register(AgentLogRegistration registration, bool provisional = false);
    Task<GatewayLogPageDto> QueryAsync(ClientKey client, GatewayLogPageRequest request, LogQueryOperation operation, CancellationToken cancellationToken);
}

public sealed class AgentLogGatewayQueryDispatcher(IAgentLogGatewaySessionRegistry sessions) : IAgentLogGatewayQueryDispatcher
{
    private readonly ConcurrentDictionary<ClientKey, Transport> _transports = new();

    public AgentLogQueryRegistration Register(AgentLogRegistration registration, bool provisional = false)
    {
        var client = registration.Client;
        var transport = new Transport(registration) { Active = !provisional };
        var handle = new AgentLogQueryRegistration(transport.Reader, transport.CompletionToken,
            () => IsCurrent(client, transport),
            () => registration.TryUse(() =>
            {
                lock (transport.Gate)
                {
                    if (!IsCurrent(client, transport)) return false;
                    transport.Active = true;
                    return true;
                }
            }),
            result => TryComplete(client, transport, result),
            () => Remove(client, transport));
        // The state owner serializes admission with state replacement. A stale
        // query candidate must never replace a newer same-presence query stream.
        var admitted = registration.TryUse(() =>
        {
            while (true)
            {
                if (!_transports.TryGetValue(client, out var previous))
                {
                    if (_transports.TryAdd(client, transport)) return true;
                    continue;
                }

                lock (previous.Gate)
                {
                    if (!_transports.TryGetValue(client, out var current) || !ReferenceEquals(current, previous)) continue;
                    if (!GatewaySessionRegistrationFence.CanReplace(registration.ConnectionId, registration.ConnectionEpoch, previous.ConnectionId, previous.ConnectionEpoch))
                        return false;

                    if (_transports.TryUpdate(client, transport, previous))
                    {
                        previous.Complete();
                        return true;
                    }
                }
            }
        });

        if (!admitted || !handle.IsCurrent)
        {
            handle.Dispose();
            throw new AgentGatewayRegistrationFencedException();
        }
        return handle;
    }

    private bool IsCurrent(ClientKey client, Transport transport) =>
        transport.Registration.IsCurrent && !transport.CompletionToken.IsCancellationRequested &&
        _transports.TryGetValue(client, out var current) && ReferenceEquals(current, transport);

    public async Task<GatewayLogPageDto> QueryAsync(ClientKey client, GatewayLogPageRequest request, LogQueryOperation operation, CancellationToken cancellationToken)
    {
        if (string.Equals(request.SourceId, "netratel-runtime", StringComparison.Ordinal)) return sessions.Query(client, request);
        if (!_transports.TryGetValue(client, out var transport) || !IsCurrent(client, transport) || !transport.Active || !transport.Registration.GetSources().Any(source => source.Available && string.Equals(source.SourceId, request.SourceId, StringComparison.Ordinal)))
            return new([], null, null, false, 0, false);

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<GatewayLogPageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!transport.Registration.TryUse(() =>
        {
            lock (transport.Gate) return IsCurrent(client, transport) && transport.Active && transport.TryAdd(requestId, completion);
        })) return new([], null, null, false, 0, true);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transport.CompletionToken);
            await transport.EnqueueAsync(transport.CreateRequest(requestId, request, operation), linked.Token).ConfigureAwait(false);
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), linked.Token).ConfigureAwait(false);
            if (!IsCurrent(client, transport))
                throw new OperationCanceledException("The log query registration has been replaced.", transport.CompletionToken);
            return result;
        }
        catch (TimeoutException)
        {
            return new([], null, null, false, 0, true);
        }
        finally
        {
            transport.Remove(requestId);
        }
    }

    private bool TryComplete(ClientKey client, Transport transport, LogQueryResult result) =>
        transport.Registration.TryUse(() =>
        {
            lock (transport.Gate)
            {
                if (!IsCurrent(client, transport) || !transport.Active || string.IsNullOrWhiteSpace(result.RequestId) ||
                    !transport.TryRemove(result.RequestId, out var completion) || completion is null) return false;

                if (result.Records.Count > 100 || result.Records.Any(record => !IsBoundedRecord(record)))
                {
                    completion.TrySetResult(new([], null, null, false, result.DroppedRecordCount, true, "invalid_log_response"));
                    return true;
                }

                completion.TrySetResult(new(result.Records.Select(MapRecord).ToArray(), EmptyToNull(result.NextCursor), EmptyToNull(result.PreviousCursor), result.HasMore,
                    result.DroppedRecordCount, result.ResyncRequired || !string.IsNullOrWhiteSpace(result.ErrorCode), EmptyToNull(result.ErrorCode)));
                return true;
            }
        });

    private void Remove(ClientKey client, Transport transport)
    {
        lock (transport.Gate)
        {
            ((ICollection<KeyValuePair<ClientKey, Transport>>)_transports)
                .Remove(new KeyValuePair<ClientKey, Transport>(client, transport));
            transport.Complete();
        }
    }

    private static GatewayLogRecordDto MapRecord(AgentLogRecord record) => GatewayLogRedactor.Redact(new(record.Cursor, record.RecordSequence,
        record.TimestampUtc?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow, record.Severity, record.SourceId,
        EmptyToNull(record.Category), EmptyToNull(record.Prefix), EmptyToNull(record.Provider), record.EventId == 0 ? null : record.EventId,
        record.RecordId == 0 ? null : record.RecordId, EmptyToNull(record.Machine), record.Message,
        record.StructuredProperties.Count == 0 ? null : new Dictionary<string, string>(record.StructuredProperties), record.Truncated));

    private static bool IsBoundedRecord(AgentLogRecord record) => !string.IsNullOrWhiteSpace(record.Cursor) && record.Cursor.Length <= 256 && record.RecordSequence > 0 &&
        !string.IsNullOrWhiteSpace(record.SourceId) && record.SourceId.Length <= 128 && !string.IsNullOrWhiteSpace(record.Severity) && record.Severity.Length <= 64 &&
        record.Category.Length <= 256 && record.Prefix.Length <= 256 && record.Provider.Length <= 256 && record.Machine.Length <= 256 && record.Message.Length <= 8 * 1024 &&
        record.StructuredProperties.Count <= 32 && record.StructuredProperties.All(property => property.Key.Length <= 128 && property.Value.Length <= 1_024);

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class Transport
    {
        private readonly Channel<GatewayLogFrame> _outbound = Channel.CreateBounded<GatewayLogFrame>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        private readonly ConcurrentDictionary<string, TaskCompletionSource<GatewayLogPageDto>> _pending = new(StringComparer.Ordinal);
        private long _nextSequence;
        private readonly CancellationTokenSource _completion;
        public Transport(AgentLogRegistration registration)
        {
            Registration = registration;
            _completion = CancellationTokenSource.CreateLinkedTokenSource(registration.CompletionToken);
            CompletionToken = _completion.Token;
        }
        public object Gate { get; } = new();
        public AgentLogRegistration Registration { get; }
        public CancellationToken CompletionToken { get; }
        public bool Active { get; set; }
        public Guid ConnectionId => Registration.ConnectionId;
        public ulong ConnectionEpoch => Registration.ConnectionEpoch;
        public ChannelReader<GatewayLogFrame> Reader => _outbound.Reader;
        public bool TryAdd(string requestId, TaskCompletionSource<GatewayLogPageDto> completion) => _pending.TryAdd(requestId, completion);
        public bool TryRemove(string requestId, out TaskCompletionSource<GatewayLogPageDto>? completion) => _pending.TryRemove(requestId, out completion);
        public void Remove(string requestId) => _pending.TryRemove(requestId, out _);
        public Task EnqueueAsync(GatewayLogFrame frame, CancellationToken cancellationToken) => _outbound.Writer.WriteAsync(frame, cancellationToken).AsTask();
        public void Complete()
        {
            lock (Gate)
            {
                if (!_outbound.Writer.TryComplete()) return;
                _completion.Cancel();
                foreach (var pending in _pending.Values) pending.TrySetCanceled(CompletionToken);
                _pending.Clear();
                _completion.Dispose();
            }
        }
        public GatewayLogFrame CreateRequest(string requestId, GatewayLogPageRequest request, LogQueryOperation operation)
        {
            var frame = new GatewayLogFrame
            {
                ProtocolVersion = "1.0",
                TenantId = Registration.Client.TenantId,
                ClientId = Registration.Client.AgentId.ToString("D"),
                ConnectionEpoch = ConnectionEpoch,
                ConnectionId = ConnectionId.ToString("D"),
                OperationId = Guid.NewGuid().ToString("D"),
                Sequence = checked((ulong)Interlocked.Increment(ref _nextSequence))
            };
            frame.Query = new LogQueryRequest { RequestId = requestId, SourceId = request.SourceId, Operation = operation, Cursor = request.Cursor ?? string.Empty, PageSize = (uint)Math.Clamp(request.PageSize, 1, 100), Text = request.Text ?? string.Empty };
            if (request.FromUtc is { } from) frame.Query.FromUtc = Timestamp.FromDateTimeOffset(from);
            if (request.ToUtc is { } to) frame.Query.ToUtc = Timestamp.FromDateTimeOffset(to);
            if (request.Severities is not null) frame.Query.Severities.AddRange(request.Severities);
            if (request.Prefixes is not null) frame.Query.Prefixes.AddRange(request.Prefixes);
            if (request.Providers is not null) frame.Query.Providers.AddRange(request.Providers);
            if (request.EventIds is not null) frame.Query.EventIds.AddRange(request.EventIds);
            if (request.Categories is not null) frame.Query.Categories.AddRange(request.Categories);
            return frame;
        }
    }
}

public sealed class AgentLogQueryRegistration(
    ChannelReader<GatewayLogFrame> reader,
    CancellationToken completionToken,
    Func<bool> isCurrent,
    Func<bool> activate,
    Func<LogQueryResult, bool> complete,
    Action dispose) : IDisposable
{
    public Guid RegistrationId { get; } = Guid.NewGuid();
    public ChannelReader<GatewayLogFrame> Reader { get; } = reader;
    public CancellationToken CompletionToken { get; } = completionToken;
    public bool IsCurrent => Volatile.Read(ref _disposed) == 0 && isCurrent();
    public bool TryActivate() => IsCurrent && activate();
    public bool TryComplete(LogQueryResult result) => IsCurrent && complete(result);
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
    }
}
