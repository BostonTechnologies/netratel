using System.Threading.Channels;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Services;

/// <summary>
/// Shared, bounded V2 gateway implementation for Development compatibility
/// and policy-admitted MCP operator observability routes. Route adapters own
/// identity, authorization, auditing, and response shaping; this service owns
/// only source validation, gateway queries, bounded live windows, and their
/// safe operational outcomes.
/// </summary>
public sealed class McpOperatorClientObservabilityService(
    NetRatelAkkaMigrationOptions options,
    IServiceProvider services)
{
    private const int DefaultLogPageSize = 100;
    private const int DefaultTailWindowSeconds = 5;
    private const int DefaultTailRecordLimit = 100;
    private const int DefaultTelemetryWindowSeconds = 5;
    private const int DefaultTelemetrySampleLimit = 5;

    private readonly NetRatelAkkaMigrationOptions _options = options;
    private readonly IServiceProvider _services = services;

    public bool IsLogCapabilityAvailable => _options.IsLogAuthorityActive &&
        _services.GetService<IAgentLogGatewaySessionRegistry>() is not null &&
        _services.GetService<IAgentLogGatewayQueryDispatcher>() is not null;

    public bool IsTelemetryCapabilityAvailable => _options.IsTelemetryAuthorityActive &&
        _services.GetService<IClientTelemetryRouter>() is not null &&
        _services.GetService<IGatewayTelemetryLiveRegistry>() is not null;

    public static bool IsValidHistoryRequest(
        string sourceId,
        string? cursor,
        int? pageSize,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string[]? severity,
        string[]? prefix,
        string[]? category,
        string[]? provider,
        long[]? eventId,
        string? text) =>
        TryCreateLogRequest(sourceId, cursor, pageSize, fromUtc, toUtc, severity, prefix, category, provider, eventId, text, out _);

    public static bool IsValidTailRequest(string? sourceId, int? windowSeconds, int? maxRecords)
    {
        var boundedWindow = windowSeconds ?? DefaultTailWindowSeconds;
        var boundedLimit = maxRecords ?? DefaultTailRecordLimit;
        return !string.IsNullOrWhiteSpace(sourceId) && sourceId.Length <= 128 &&
            boundedWindow is >= 1 and <= 15 && boundedLimit is >= 1 and <= 100;
    }

    public static bool IsValidSourceId(string? sourceId) => !string.IsNullOrWhiteSpace(sourceId) && sourceId.Length <= 128;

    public static bool IsValidTelemetryWindow(int? windowSeconds, int? maxSamples)
    {
        var boundedWindow = windowSeconds ?? DefaultTelemetryWindowSeconds;
        var boundedLimit = maxSamples ?? DefaultTelemetrySampleLimit;
        return boundedWindow is >= 1 and <= 15 && boundedLimit is >= 1 and <= 20;
    }

    public McpOperatorObservabilityResult<IReadOnlyList<GatewayLogSourceDescriptorDto>> GetSources(ClientKey client)
    {
        if (!TryGetLogSessions(out var sessions))
            return Failure<IReadOnlyList<GatewayLogSourceDescriptorDto>>("observability_gateway_unavailable");

        var sources = sessions.GetSources(client);
        return sources.Count == 0
            ? Failure<IReadOnlyList<GatewayLogSourceDescriptorDto>>("log_sources_unavailable")
            : Success<IReadOnlyList<GatewayLogSourceDescriptorDto>>(sources);
    }

    public async Task<McpOperatorObservabilityResult<McpOperatorLogHistoryResponse>> GetHistoryAsync(
        ClientKey client,
        string sourceId,
        string? cursor,
        int? pageSize,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string[]? severity,
        string[]? prefix,
        string[]? category,
        string[]? provider,
        long[]? eventId,
        string? text,
        CancellationToken cancellationToken)
    {
        if (!TryGetLogServices(out var sessions, out var dispatcher))
            return Failure<McpOperatorLogHistoryResponse>("observability_gateway_unavailable");
        if (!TryCreateLogRequest(sourceId, cursor, pageSize, fromUtc, toUtc, severity, prefix, category, provider, eventId, text, out var request))
            return Failure<McpOperatorLogHistoryResponse>("invalid_log_query");
        if (ValidateLogSource(sessions, client, sourceId!) is { } sourceFailure)
            return Failure<McpOperatorLogHistoryResponse>(sourceFailure);

        var page = await dispatcher.QueryAsync(client, request, LogQueryOperation.History, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(page.ErrorCode))
            return Failure<McpOperatorLogHistoryResponse>(page.ErrorCode);

        var retained = sessions.Query(client, request);
        var resyncRequired = page.ResyncRequired || retained.ResyncRequired;
        return Success(new McpOperatorLogHistoryResponse(
            request.SourceId,
            page,
            Math.Max(retained.DroppedRecordCount, page.DroppedRecordCount),
            resyncRequired,
            resyncRequired
                ? "Run the confirmed resync operation to fetch a bounded current history window before tailing again."
                : null));
    }

    public async Task<McpOperatorObservabilityResult<McpOperatorLogTailResponse>> GetTailAsync(
        ClientKey client,
        string sourceId,
        int? windowSeconds,
        int? maxRecords,
        Func<CancellationToken, Task<string?>>? currentAdmission,
        CancellationToken cancellationToken)
    {
        if (!TryGetLogServices(out var sessions, out var dispatcher))
            return Failure<McpOperatorLogTailResponse>("observability_gateway_unavailable");

        if (!IsValidTailRequest(sourceId, windowSeconds, maxRecords))
            return Failure<McpOperatorLogTailResponse>("invalid_log_query");
        var boundedWindow = windowSeconds ?? DefaultTailWindowSeconds;
        var boundedLimit = maxRecords ?? DefaultTailRecordLimit;
        if (ValidateLogSource(sessions, client, sourceId!) is { } sourceFailure)
            return Failure<McpOperatorLogTailResponse>(sourceFailure);
        if (await CurrentAdmissionFailureAsync(currentAdmission, cancellationToken).ConfigureAwait(false) is { } admissionFailure)
            return AdmissionFailure<McpOperatorLogTailResponse>(admissionFailure);

        using var subscription = new LogWindowSubscription(sessions, client, sourceId);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(TimeSpan.FromSeconds(boundedWindow));
        var records = new List<GatewayLogRecordDto>(boundedLimit);
        ulong dropped = 0;
        var resyncRequired = false;
        try
        {
            try
            {
                var started = await dispatcher.QueryAsync(
                    client,
                    new GatewayLogPageRequest(sourceId, null, boundedLimit, null, null, null, null, null),
                    LogQueryOperation.StartLive,
                    window.Token).ConfigureAwait(false);
                Add(records, started.Records.Where(record => string.Equals(record.SourceId, sourceId, StringComparison.Ordinal)), boundedLimit);
                dropped = checked(dropped + started.DroppedRecordCount);
                resyncRequired |= started.ResyncRequired;
                if (await CurrentAdmissionFailureAsync(currentAdmission, cancellationToken).ConfigureAwait(false) is { } failure)
                    return AdmissionFailure<McpOperatorLogTailResponse>(failure);
            }
            catch (OperationCanceledException) when (window.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                resyncRequired = true;
            }

            while (records.Count < boundedLimit && !window.IsCancellationRequested)
            {
                try
                {
                    var batch = await subscription.Reader.ReadAsync(window.Token).ConfigureAwait(false);
                    Add(records, batch.Records.Where(record => string.Equals(record.SourceId, sourceId, StringComparison.Ordinal)), boundedLimit);
                    dropped = checked(dropped + batch.DroppedRecordCount);
                    resyncRequired |= batch.ResyncRequired;
                    if (await CurrentAdmissionFailureAsync(currentAdmission, cancellationToken).ConfigureAwait(false) is { } failure)
                        return AdmissionFailure<McpOperatorLogTailResponse>(failure);
                }
                catch (OperationCanceledException) when (window.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    resyncRequired = true;
                    break;
                }
            }
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await dispatcher.QueryAsync(
                    client,
                    new GatewayLogPageRequest(sourceId, null, 1, null, null, null, null, null),
                    LogQueryOperation.StopLive,
                    stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                resyncRequired = true;
            }
        }

        if (await CurrentAdmissionFailureAsync(currentAdmission, cancellationToken).ConfigureAwait(false) is { } finalAdmissionFailure)
            return AdmissionFailure<McpOperatorLogTailResponse>(finalAdmissionFailure);

        var gapDetected = resyncRequired || subscription.Dropped;
        return Success(new McpOperatorLogTailResponse(
            sourceId,
            records,
            boundedWindow,
            boundedLimit,
            dropped,
            gapDetected,
            gapDetected
                ? "Run the confirmed resync operation to fetch a bounded current history window before tailing again."
                : null));
    }

    public async Task<McpOperatorObservabilityResult<McpOperatorLogResyncResponse>> ResyncAsync(
        ClientKey client,
        string? sourceId,
        Func<CancellationToken, Task<string?>>? currentAdmission,
        CancellationToken cancellationToken)
    {
        if (!TryGetLogServices(out var sessions, out var dispatcher))
            return Failure<McpOperatorLogResyncResponse>("observability_gateway_unavailable");
        if (!IsValidSourceId(sourceId))
            return Failure<McpOperatorLogResyncResponse>("invalid_log_query");
        if (ValidateLogSource(sessions, client, sourceId!) is { } sourceFailure)
            return Failure<McpOperatorLogResyncResponse>(sourceFailure);
        if (await CurrentAdmissionFailureAsync(currentAdmission, cancellationToken).ConfigureAwait(false) is { } admissionFailure)
            return AdmissionFailure<McpOperatorLogResyncResponse>(admissionFailure);

        var prior = sessions.Query(client, new GatewayLogPageRequest(sourceId!, null, 1, null, null, null, null, null));
        var page = await dispatcher.QueryAsync(
            client,
            new GatewayLogPageRequest(sourceId!, null, DefaultLogPageSize, null, null, null, null, null),
            LogQueryOperation.History,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(page.ErrorCode))
            return Failure<McpOperatorLogResyncResponse>(page.ErrorCode);
        if (await CurrentAdmissionFailureAsync(currentAdmission, cancellationToken).ConfigureAwait(false) is { } postDispatchAdmissionFailure)
            return AdmissionFailure<McpOperatorLogResyncResponse>(postDispatchAdmissionFailure);

        // A bounded history response establishes the new baseline even when it
        // reports the gap that prompted this confirmed recovery action.
        var resyncObserved = page.ResyncRequired || prior.ResyncRequired;
        var completed = sessions.TryCompleteResync(client, sourceId!);
        var resyncRequired = !completed && resyncObserved;
        return Success(new McpOperatorLogResyncResponse(
            sourceId!,
            page,
            Math.Max(prior.DroppedRecordCount, page.DroppedRecordCount),
            resyncObserved,
            resyncRequired,
            completed,
            resyncRequired
                ? "The bounded history refresh did not complete; retry after the client gateway reconnects."
                : "Bounded history refresh completed; it is safe to start a new tail window."));
    }

    public async Task<McpOperatorObservabilityResult<AgentTelemetrySnapshotResponse>> GetSnapshotAsync(
        ClientKey client,
        CancellationToken cancellationToken)
    {
        if (!TryGetTelemetry(out var telemetry))
            return Failure<AgentTelemetrySnapshotResponse>("observability_gateway_unavailable");

        var state = await telemetry.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        return state.Latest is null
            ? Failure<AgentTelemetrySnapshotResponse>("telemetry_unavailable")
            : Success(MapTelemetry(state.Latest));
    }

    public async Task<McpOperatorObservabilityResult<McpOperatorTelemetryWindowResponse>> GetWindowAsync(
        ClientKey client,
        int? windowSeconds,
        int? maxSamples,
        Func<CancellationToken, Task<string?>>? currentAdmission,
        CancellationToken cancellationToken)
    {
        if (!TryGetTelemetry(out var telemetry) || _services.GetService<IGatewayTelemetryLiveRegistry>() is not { } live)
            return Failure<McpOperatorTelemetryWindowResponse>("observability_gateway_unavailable");

        if (!IsValidTelemetryWindow(windowSeconds, maxSamples))
            return Failure<McpOperatorTelemetryWindowResponse>("invalid_telemetry_window");
        if (await CurrentAdmissionFailureAsync(currentAdmission, cancellationToken).ConfigureAwait(false) is { } admissionFailure)
            return AdmissionFailure<McpOperatorTelemetryWindowResponse>(admissionFailure);
        var boundedWindow = windowSeconds ?? DefaultTelemetryWindowSeconds;
        var boundedLimit = maxSamples ?? DefaultTelemetrySampleLimit;

        var samples = new List<AgentTelemetrySnapshotResponse>(boundedLimit);
        var lastEpoch = -1L;
        ulong lastSequence = 0;
        await using var subscription = live.Subscribe(client);
        var state = await telemetry.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (state.Latest is not null)
        {
            samples.Add(MapTelemetry(state.Latest));
            lastEpoch = state.Latest.ConnectionEpoch;
            lastSequence = state.Latest.Sequence;
            if (await CurrentAdmissionFailureAsync(currentAdmission, cancellationToken).ConfigureAwait(false) is { } failure)
                return AdmissionFailure<McpOperatorTelemetryWindowResponse>(failure);
        }

        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(TimeSpan.FromSeconds(boundedWindow));
        while (samples.Count < boundedLimit && !window.IsCancellationRequested)
        {
            try
            {
                var update = await subscription.Reader.ReadAsync(window.Token).ConfigureAwait(false);
                if (update.Snapshot?.Snapshot is not { } snapshot ||
                    snapshot.ConnectionEpoch < lastEpoch ||
                    snapshot.ConnectionEpoch == lastEpoch && snapshot.Sequence <= lastSequence)
                {
                    continue;
                }

                samples.Add(MapTelemetry(snapshot));
                lastEpoch = snapshot.ConnectionEpoch;
                lastSequence = snapshot.Sequence;
                if (await CurrentAdmissionFailureAsync(currentAdmission, cancellationToken).ConfigureAwait(false) is { } failure)
                    return AdmissionFailure<McpOperatorTelemetryWindowResponse>(failure);
            }
            catch (OperationCanceledException) when (window.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
        }

        if (await CurrentAdmissionFailureAsync(currentAdmission, cancellationToken).ConfigureAwait(false) is { } finalAdmissionFailure)
            return AdmissionFailure<McpOperatorTelemetryWindowResponse>(finalAdmissionFailure);

        return Success(new McpOperatorTelemetryWindowResponse(samples, boundedWindow, boundedLimit));
    }

    private bool TryGetLogSessions(out IAgentLogGatewaySessionRegistry sessions)
    {
        sessions = default!;
        if (!_options.IsLogAuthorityActive || _services.GetService<IAgentLogGatewaySessionRegistry>() is not { } registered)
            return false;

        sessions = registered;
        return true;
    }

    private bool TryGetLogServices(out IAgentLogGatewaySessionRegistry sessions, out IAgentLogGatewayQueryDispatcher dispatcher)
    {
        sessions = default!;
        dispatcher = default!;
        return TryGetLogSessions(out sessions) && _services.GetService<IAgentLogGatewayQueryDispatcher>() is { } registered && (dispatcher = registered) is not null;
    }

    private bool TryGetTelemetry(out IClientTelemetryRouter telemetry)
    {
        telemetry = default!;
        if (!_options.IsTelemetryAuthorityActive || _services.GetService<IClientTelemetryRouter>() is not { } registered)
            return false;

        telemetry = registered;
        return true;
    }

    private static bool TryCreateLogRequest(
        string sourceId,
        string? cursor,
        int? pageSize,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string[]? severity,
        string[]? prefix,
        string[]? category,
        string[]? provider,
        long[]? eventId,
        string? text,
        out GatewayLogPageRequest request)
    {
        request = default!;
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 128 || cursor?.Length > 256 || text?.Length > 512 ||
            pageSize is <= 0 or > 100 || severity?.Length > 8 || prefix?.Length > 32 || category?.Length > 32 || provider?.Length > 32 || eventId?.Length > 32 ||
            severity?.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 64) == true ||
            prefix?.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256) == true ||
            category?.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256) == true ||
            provider?.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256) == true ||
            eventId?.Any(value => value < 0) == true || fromUtc > toUtc ||
            (fromUtc.HasValue && toUtc.HasValue && toUtc.Value - fromUtc.Value > TimeSpan.FromDays(31)))
        {
            return false;
        }

        request = new GatewayLogPageRequest(sourceId, cursor, pageSize ?? DefaultLogPageSize, fromUtc, toUtc, severity, prefix, text, provider, eventId, category);
        return true;
    }

    private static string? ValidateLogSource(IAgentLogGatewaySessionRegistry sessions, ClientKey client, string sourceId)
    {
        var source = sessions.GetSources(client).FirstOrDefault(candidate => string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal));
        return source switch
        {
            null => "invalid_source",
            { Available: false } => "source_unavailable",
            _ => null
        };
    }

    private static AgentTelemetrySnapshotResponse MapTelemetry(TelemetrySnapshot snapshot) => new(
        snapshot.Client.TenantId,
        snapshot.Client.AgentId,
        snapshot.ObservedAtUtc,
        snapshot.ReceivedAtUtc,
        snapshot.Cpu,
        snapshot.Memory,
        snapshot.Disks,
        snapshot.Networks,
        snapshot.TransportHealth,
        snapshot.Source,
        snapshot.IsAuthoritative);

    private static Task<string?> CurrentAdmissionFailureAsync(
        Func<CancellationToken, Task<string?>>? currentAdmission,
        CancellationToken cancellationToken) =>
        currentAdmission is null
            ? Task.FromResult<string?>(null)
            : currentAdmission(cancellationToken);

    private static void Add(List<GatewayLogRecordDto> target, IEnumerable<GatewayLogRecordDto> records, int maximum)
    {
        foreach (var record in records)
        {
            if (target.Count == maximum)
                break;
            target.Add(record);
        }
    }

    private static McpOperatorObservabilityResult<T> Success<T>(T value) => new(value, null);

    private static McpOperatorObservabilityResult<T> Failure<T>(string failureCode) => new(default, failureCode);

    private static McpOperatorObservabilityResult<T> AdmissionFailure<T>(string failureCode) => new(default, failureCode, true);

    private sealed class LogWindowSubscription : IDisposable
    {
        private readonly IAgentLogGatewaySessionRegistry _sessions;
        private readonly ClientKey _client;
        private readonly string _sourceId;
        private readonly Action<GatewayLogBatchEvent> _onBatch;
        private int _disposed;
        private int _dropped;

        public LogWindowSubscription(IAgentLogGatewaySessionRegistry sessions, ClientKey client, string sourceId)
        {
            _sessions = sessions;
            _client = client;
            _sourceId = sourceId;
            var channel = Channel.CreateBounded<GatewayLogBatchEvent>(new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            Reader = channel.Reader;
            _onBatch = batch =>
            {
                if (batch.Client == _client && batch.Records.Any(record => string.Equals(record.SourceId, _sourceId, StringComparison.Ordinal)) &&
                    !channel.Writer.TryWrite(batch))
                {
                    Interlocked.Exchange(ref _dropped, 1);
                }
            };
            _sessions.LogBatchAccepted += _onBatch;
        }

        public ChannelReader<GatewayLogBatchEvent> Reader { get; }

        public bool Dropped => Volatile.Read(ref _dropped) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _sessions.LogBatchAccepted -= _onBatch;
        }
    }
}

public sealed record McpOperatorObservabilityResult<T>(T? Value, string? FailureCode, bool IsAdmissionFailure = false)
{
    public bool IsSuccess => FailureCode is null;
}

public sealed record McpOperatorLogHistoryResponse(
    string SourceId,
    GatewayLogPageDto Page,
    ulong DroppedRecordCount,
    bool ResyncRequired,
    string? ResyncGuidance);

public sealed record McpOperatorLogTailResponse(
    string SourceId,
    IReadOnlyList<GatewayLogRecordDto> Records,
    int WindowSeconds,
    int RequestedMaxRecords,
    ulong DroppedRecordCount,
    bool ResyncRequired,
    string? ResyncGuidance);

public sealed record McpOperatorLogResyncResponse(
    string SourceId,
    GatewayLogPageDto Page,
    ulong DroppedRecordCount,
    bool ResyncObserved,
    bool ResyncRequired,
    bool ResyncCompleted,
    string ResyncGuidance);

public sealed record McpOperatorTelemetryWindowResponse(
    IReadOnlyList<AgentTelemetrySnapshotResponse> Samples,
    int WindowSeconds,
    int RequestedMaxSamples);
