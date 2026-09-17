using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts;
using System.Collections.Concurrent;
using System.Text;

namespace NetRatel.API.Gateway;

/// <summary>
/// Process-local, bounded transient cache for an admitted agent log stream.
/// It holds neither actor state nor durable data.  Exact registration ownership fences
/// stale gateway frames before they can become browser-visible history.
/// </summary>
public interface IAgentLogGatewaySessionRegistry
{
    event Action<GatewayLogBatchEvent>? LogBatchAccepted;
    AgentLogRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, AgentLogHello hello, bool provisional = false);
    bool TryCompleteResync(ClientKey client, string sourceId);
    IReadOnlyList<GatewayLogSourceDescriptorDto> GetSources(ClientKey client);
    GatewayLogPageDto Query(ClientKey client, GatewayLogPageRequest request);
}

public sealed class AgentLogGatewaySessionRegistry : IAgentLogGatewaySessionRegistry
{
    private const int MaximumRecords = 2_000;
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumStructuredProperties = 32;
    private const int MaximumPropertyKeyLength = 128;
    private const int MaximumPropertyValueLength = 1_024;
    private readonly ConcurrentDictionary<ClientKey, AgentLogState> _states = new();

    public event Action<GatewayLogBatchEvent>? LogBatchAccepted;

    public AgentLogRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, AgentLogHello hello, bool provisional = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(client.TenantId);
        ArgumentOutOfRangeException.ThrowIfEqual(connectionId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfZero(connectionEpoch);

        var state = new AgentLogState(connectionId, connectionEpoch, hello.Sources.Select(MapSource).ToArray()) { Active = !provisional };
        var registration = new AgentLogRegistration(client, connectionId, connectionEpoch, state.CompletionToken,
            () => IsCurrent(client, state),
            () => UseCurrent(client, state, () => state.Active = true),
            () => Remove(client, state),
            batch => TryAppend(client, state, batch),
            () => GetSources(client, state),
            action => UseCurrent(client, state, action));
        while (true)
        {
            if (!_states.TryGetValue(client, out var previous))
            {
                if (_states.TryAdd(client, state))
                {
                    return registration;
                }

                continue;
            }

            lock (previous.Gate)
            {
                if (!IsCurrent(client, previous)) continue;
                if (!GatewaySessionRegistrationFence.CanReplace(connectionId, connectionEpoch, previous.ConnectionId, previous.ConnectionEpoch))
                {
                    state.Complete();
                    throw new AgentGatewayRegistrationFencedException();
                }

                if (_states.TryUpdate(client, state, previous))
                {
                    previous.Complete();
                    return registration;
                }
            }
        }
    }

    private bool IsCurrent(ClientKey client, AgentLogState state) =>
        !state.CompletionToken.IsCancellationRequested && _states.TryGetValue(client, out var current) && ReferenceEquals(current, state);

    private bool UseCurrent(ClientKey client, AgentLogState state, Func<bool> action)
    {
        lock (state.Gate) return IsCurrent(client, state) && action();
    }

    private void Remove(ClientKey client, AgentLogState state)
    {
        lock (state.Gate)
        {
            _states.TryRemove(new KeyValuePair<ClientKey, AgentLogState>(client, state));
            state.Complete();
        }
    }

    private bool TryAppend(ClientKey client, AgentLogState state, AgentLogBatch batch)
    {
        GatewayLogBatchEvent? accepted = null;

        lock (state.Gate)
        {
            if (!IsCurrent(client, state) || !state.Active) return false;
            if (batch.Records.Count > 100) return false;

            state.DroppedRecordCount = checked(state.DroppedRecordCount + batch.DroppedRecordCount);
            state.ResyncRequired |= batch.ResyncRequired;
            var acceptedRecords = new List<GatewayLogRecordDto>(batch.Records.Count);
            foreach (var record in batch.Records)
            {
                if (!state.Sources.Any(source => source.Available && string.Equals(source.SourceId, record.SourceId, StringComparison.Ordinal)))
                {
                    state.ResyncRequired = true;
                    continue;
                }

                if (!IsBoundedRecord(record))
                {
                    state.DroppedRecordCount++;
                    state.ResyncRequired = true;
                    continue;
                }

                var mapped = MapRecord(record);
                var bytes = EstimateBytes(mapped);
                if (bytes > 8 * 1024)
                {
                    state.DroppedRecordCount++;
                    state.ResyncRequired = true;
                    continue;
                }

                while (state.Records.Count > 0 && (state.Records.Count >= MaximumRecords || state.Bytes + bytes > MaximumBytes))
                {
                    state.Bytes -= EstimateBytes(state.Records.Dequeue());
                    state.DroppedRecordCount++;
                    state.ResyncRequired = true;
                }

                // Sequence/cursor identify events; equal message text is valid.
                if (state.LastRecordSequence >= mapped.Sequence)
                {
                    state.ResyncRequired = true;
                    continue;
                }

                state.Records.Enqueue(mapped);
                state.Bytes += bytes;
                state.LastRecordSequence = mapped.Sequence;
                acceptedRecords.Add(mapped);
            }

            if (acceptedRecords.Count > 0 || batch.DroppedRecordCount > 0 || batch.ResyncRequired)
            {
                accepted = new GatewayLogBatchEvent(
                    client,
                    batch.SessionId,
                    acceptedRecords,
                    batch.NextCursor,
                    state.DroppedRecordCount,
                    state.ResyncRequired);
            }
            Notify(accepted);
            return true;
        }
    }

    public bool TryCompleteResync(ClientKey client, string sourceId)
    {
        if (!_states.TryGetValue(client, out var state)) return false;

        lock (state.Gate)
        {
            if (!IsCurrent(client, state) || !state.Active || !state.ResyncRequired || !state.Sources.Any(source => source.Available && string.Equals(source.SourceId, sourceId, StringComparison.Ordinal))) return false;
            state.ResyncRequired = false;
            return true;
        }
    }

    public IReadOnlyList<GatewayLogSourceDescriptorDto> GetSources(ClientKey client)
    {
        if (!_states.TryGetValue(client, out var state)) return [];
        return GetSources(client, state);
    }

    private IReadOnlyList<GatewayLogSourceDescriptorDto> GetSources(ClientKey client, AgentLogState state)
    {
        lock (state.Gate) return IsCurrent(client, state) && state.Active ? state.Sources.ToArray() : [];
    }

    public GatewayLogPageDto Query(ClientKey client, GatewayLogPageRequest request)
    {
        if (!_states.TryGetValue(client, out var state)) return new([], null, null, false, 0, false);

        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        lock (state.Gate)
        {
            if (!IsCurrent(client, state) || !state.Active) return new([], null, null, false, 0, false);
            if (!state.Sources.Any(source => source.Available && string.Equals(source.SourceId, request.SourceId, StringComparison.Ordinal)))
            {
                return new([], null, null, false, state.DroppedRecordCount, state.ResyncRequired);
            }

            var matching = state.Records
                .Where(record => string.Equals(record.SourceId, request.SourceId, StringComparison.Ordinal))
                .Where(record => request.FromUtc is null || record.TimestampUtc >= request.FromUtc)
                .Where(record => request.ToUtc is null || record.TimestampUtc <= request.ToUtc)
                .Where(record => request.Severities is not { Count: > 0 } || request.Severities.Contains(record.Severity, StringComparer.OrdinalIgnoreCase))
                .Where(record => request.Prefixes is not { Count: > 0 } || request.Prefixes.Contains(record.Prefix ?? "Other", StringComparer.OrdinalIgnoreCase))
                .Where(record => request.Categories is not { Count: > 0 } || request.Categories.Contains(record.Category ?? "Other", StringComparer.OrdinalIgnoreCase))
                .Where(record => string.IsNullOrWhiteSpace(request.Text) || record.Message.Contains(request.Text, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (string.IsNullOrWhiteSpace(request.Cursor))
            {
                // Opening a log explorer means "show me the recent buffer",
                // while records retain chronological display order.
                var records = matching.TakeLast(pageSize).ToArray();
                var hasEarlier = matching.Length > records.Length;
                return new(records, null, hasEarlier ? BeforeCursor(records.FirstOrDefault()?.Sequence) : null, hasEarlier, state.DroppedRecordCount, state.ResyncRequired);
            }

            var cursor = ParseCursor(request.Cursor);
            if (!cursor.Valid) return new([], null, null, false, state.DroppedRecordCount, state.ResyncRequired, "invalid_cursor");

            if (cursor.Before)
            {
                var earlier = matching.Where(record => record.Sequence < cursor.Sequence).ToArray();
                var page = earlier.TakeLast(pageSize).ToArray();
                var hasEarlier = earlier.Length > page.Length;
                return new(page, null, hasEarlier ? BeforeCursor(page.FirstOrDefault()?.Sequence) : null, hasEarlier, state.DroppedRecordCount, state.ResyncRequired);
            }

            var following = matching.Where(record => record.Sequence > cursor.Sequence).Take(pageSize + 1).ToArray();
            var hasMore = following.Length > pageSize;
            var pageAfter = following.Take(pageSize).ToArray();
            return new(pageAfter, hasMore ? AfterCursor(pageAfter.LastOrDefault()?.Sequence) : null, null, hasMore, state.DroppedRecordCount, state.ResyncRequired);
        }
    }

    private static string? BeforeCursor(ulong? sequence) => sequence is > 0 ? $"before:{EncodeCursor(sequence.Value)}" : null;

    private static string? AfterCursor(ulong? sequence) => sequence is > 0 ? $"after:{EncodeCursor(sequence.Value)}" : null;

    private static string EncodeCursor(ulong sequence) => Convert.ToBase64String(BitConverter.GetBytes(sequence))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static (bool Valid, bool Before, ulong Sequence) ParseCursor(string cursor)
    {
        var separator = cursor.IndexOf(':');
        if (separator <= 0 || separator == cursor.Length - 1) return default;
        var direction = cursor[..separator];
        if (direction is not ("before" or "after")) return default;

        try
        {
            var token = cursor[(separator + 1)..].Replace('-', '+').Replace('_', '/');
            token = token.PadRight((token.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(token);
            return bytes.Length == sizeof(ulong)
                ? (true, direction == "before", BitConverter.ToUInt64(bytes))
                : default;
        }
        catch (FormatException)
        {
            return default;
        }
    }

    private static GatewayLogSourceDescriptorDto MapSource(LogSourceDescriptor source) => new(
        source.SourceId, source.Kind, source.DisplayName, source.Platform, source.Available,
        string.IsNullOrWhiteSpace(source.UnavailableReason) ? null : source.UnavailableReason,
        source.SupportsLive, source.SupportsHistory, source.SupportsPaging, source.FilterCapabilities.ToArray());

    private static GatewayLogRecordDto MapRecord(AgentLogRecord record) => GatewayLogRedactor.Redact(new(
        EncodeCursor(record.RecordSequence),
        record.RecordSequence,
        record.TimestampUtc?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow,
        record.Severity,
        record.SourceId,
        string.IsNullOrWhiteSpace(record.Category) ? null : record.Category,
        string.IsNullOrWhiteSpace(record.Prefix) ? null : record.Prefix,
        string.IsNullOrWhiteSpace(record.Provider) ? null : record.Provider,
        record.EventId == 0 ? null : record.EventId,
        record.RecordId == 0 ? null : record.RecordId,
        string.IsNullOrWhiteSpace(record.Machine) ? null : record.Machine,
        record.Message,
        record.StructuredProperties.Count == 0 ? null : new Dictionary<string, string>(record.StructuredProperties),
        record.Truncated));

    private static bool IsBoundedRecord(AgentLogRecord record) =>
        !string.IsNullOrWhiteSpace(record.Cursor) && record.Cursor.Length <= 128 &&
        record.RecordSequence > 0 &&
        !string.IsNullOrWhiteSpace(record.Severity) && record.Severity.Length <= 64 &&
        !string.IsNullOrWhiteSpace(record.SourceId) && record.SourceId.Length <= 128 &&
        record.Category.Length <= 256 && record.Prefix.Length <= 256 && record.Provider.Length <= 256 && record.Machine.Length <= 256 &&
        record.StructuredProperties.Count <= MaximumStructuredProperties &&
        record.StructuredProperties.All(property => property.Key.Length <= MaximumPropertyKeyLength && property.Value.Length <= MaximumPropertyValueLength);

    private static int EstimateBytes(GatewayLogRecordDto record)
    {
        var bytes = 256L + StringBytes(record.Cursor) + StringBytes(record.Severity) + StringBytes(record.SourceId) +
            StringBytes(record.Category) + StringBytes(record.Prefix) + StringBytes(record.Provider) + StringBytes(record.Machine) +
            StringBytes(record.Message);
        if (record.StructuredProperties is not null)
        {
            foreach (var property in record.StructuredProperties)
            {
                bytes += StringBytes(property.Key) + StringBytes(property.Value);
            }
        }

        return bytes > int.MaxValue ? int.MaxValue : (int)bytes;
    }

    private static int StringBytes(string? value) => string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);

    private void Notify(GatewayLogBatchEvent? accepted)
    {
        if (accepted is null) return;

        var subscribers = LogBatchAccepted;
        if (subscribers is null) return;

        foreach (Action<GatewayLogBatchEvent> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(accepted);
            }
            catch (Exception exception)
            {
                // Fan-out must not be allowed to interrupt the agent stream.
                System.Diagnostics.Trace.TraceWarning("A gateway log fan-out observer failed: {0}", exception.GetType().Name);
            }
        }
    }

    private sealed class AgentLogState
    {
        private readonly CancellationTokenSource _completion = new();
        public AgentLogState(Guid connectionId, ulong connectionEpoch, IReadOnlyList<GatewayLogSourceDescriptorDto> sources)
        {
            ConnectionId = connectionId;
            ConnectionEpoch = connectionEpoch;
            Sources = sources;
            CompletionToken = _completion.Token;
        }
        public CancellationToken CompletionToken { get; }
        public bool Active { get; set; }
        public void Complete()
        {
            if (CompletionToken.IsCancellationRequested) return;
            _completion.Cancel();
            _completion.Dispose();
            Records.Clear();
            Bytes = 0;
        }
        public object Gate { get; } = new();
        public Guid ConnectionId { get; }
        public ulong ConnectionEpoch { get; }
        public IReadOnlyList<GatewayLogSourceDescriptorDto> Sources { get; }
        public Queue<GatewayLogRecordDto> Records { get; } = new();
        public int Bytes { get; set; }
        public ulong DroppedRecordCount { get; set; }
        public bool ResyncRequired { get; set; }
        public ulong LastRecordSequence { get; set; }
    }
}

/// <summary>Transient data passed from one admitted log batch to live subscribers.</summary>
public sealed record GatewayLogBatchEvent(
    ClientKey Client,
    string SessionId,
    IReadOnlyList<GatewayLogRecordDto> Records,
    string? NextCursor,
    ulong DroppedRecordCount,
    bool ResyncRequired);

/// <summary>Exact owner of one log stream, including its transient state and ingress.</summary>
public sealed class AgentLogRegistration(
    ClientKey client,
    Guid connectionId,
    ulong connectionEpoch,
    CancellationToken completionToken,
    Func<bool> isCurrent,
    Func<bool> activate,
    Action dispose,
    Func<AgentLogBatch, bool> append,
    Func<IReadOnlyList<GatewayLogSourceDescriptorDto>> getSources,
    Func<Func<bool>, bool> useCurrent) : IDisposable
{
    private int _disposed;
    public Guid RegistrationId { get; } = Guid.NewGuid();
    public ClientKey Client { get; } = client;
    public Guid ConnectionId { get; } = connectionId;
    public ulong ConnectionEpoch { get; } = connectionEpoch;
    public CancellationToken CompletionToken { get; } = completionToken;
    public bool IsCurrent => Volatile.Read(ref _disposed) == 0 && isCurrent();
    public bool TryActivate() => IsCurrent && activate();
    public bool TryAppend(AgentLogBatch batch) => IsCurrent && append(batch);
    public IReadOnlyList<GatewayLogSourceDescriptorDto> GetSources() => getSources();
    internal bool TryUse(Func<bool> action) => IsCurrent && useCurrent(action);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
    }
}
