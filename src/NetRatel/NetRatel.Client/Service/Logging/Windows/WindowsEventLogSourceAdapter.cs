using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NetRatel.Client.Service.Logging;

namespace NetRatel.Client.Service.Logging.Windows;

/// <summary>
/// Windows Event Log adapter. Source ids are established from local discovery;
/// callers can only select a discovered channel and typed filter values.
/// </summary>
internal sealed class WindowsEventLogSourceAdapter : IClientLogSourceAdapter
{
    private const int MaximumPageSize = 100;
    private const int MaximumScannedRecords = 10_000;
    private const int MaximumChannels = 96;
    private const int MaximumMessageBytes = 8 * 1024;
    private static readonly string[] CommonChannels = ["Application", "System", "Security", "Setup", "ForwardedEvents"];
    private static readonly string[] FilterCapabilities = ["time", "severity", "category", "provider", "event-id", "text"];
    private readonly IWindowsEventLogPlatform _platform;
    private readonly Func<bool> _isWindows;
    private readonly Dictionary<string, WindowsSource> _sources = new(StringComparer.Ordinal);
    private readonly object _sourceGate = new();

    public WindowsEventLogSourceAdapter(IWindowsEventLogPlatform? platform = null, Func<bool>? isWindows = null)
    {
        _platform = platform ?? CreateDefaultPlatform();
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
    }

    public async Task<IReadOnlyList<ClientLogSourceDescriptor>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!_isWindows()) return [];

        var discovered = await _platform.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var channels = new Dictionary<string, WindowsEventLogChannel>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in discovered) channels[channel.Name] = channel;
        foreach (var common in CommonChannels)
        {
            if (!channels.ContainsKey(common)) channels[common] = new(common, false, "channel_unavailable");
        }

        var ordered = channels.Values
            .OrderBy(channel => CommonChannelOrder(channel.Name))
            .ThenBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumChannels)
            .Select(channel => CreateSource(channel))
            .ToArray();

        lock (_sourceGate)
        {
            _sources.Clear();
            foreach (var source in ordered) _sources[source.Descriptor.SourceId] = source;
        }

        return ordered.Select(source => source.Descriptor).ToArray();
    }

    public async Task<ClientLogPage> ReadHistoryAsync(ClientLogQuery query, CancellationToken cancellationToken)
    {
        var source = GetSource(query.SourceId);
        if (source is null || !source.Descriptor.Available) return new([], null, null, false, ErrorCode: "source_unavailable");
        if (!TryParseCursor(query.Cursor, source.Descriptor.SourceId, out var cursor)) return new([], null, null, false, ErrorCode: "invalid_cursor");

        var pageSize = Math.Clamp(query.PageSize, 1, MaximumPageSize);
        var selected = new List<ClientRuntimeLogRecord>(pageSize + 1);
        var scanned = 0;
        try
        {
            await foreach (var entry in _platform.ReadNewestAsync(source.ChannelName, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > MaximumScannedRecords) break;
                if (entry.RecordId <= 0 || !MatchesCursor(entry, cursor) || !Matches(entry, query)) continue;

                selected.Add(Map(entry, source.Descriptor.SourceId));
                if (selected.Count > pageSize) break;
            }
        }
        catch (EventLogException)
        {
            return new([], null, null, false, ErrorCode: "event_log_read_failed");
        }
        catch (UnauthorizedAccessException)
        {
            return new([], null, null, false, ErrorCode: "event_log_access_denied");
        }

        var hasMore = selected.Count > pageSize || scanned > MaximumScannedRecords;
        var page = selected.Take(pageSize).Reverse().ToArray();
        var previous = hasMore && page.Length > 0 ? BeforeCursor(page[0].Cursor) : null;
        return new(page, null, previous, hasMore, ResyncRequired: scanned > MaximumScannedRecords,
            ErrorCode: scanned > MaximumScannedRecords && page.Length == 0 ? "query_scan_limit" : null);
    }

    public async IAsyncEnumerable<ClientLogFollowResult> FollowAsync(ClientLogQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var source = GetSource(query.SourceId);
        if (source is null || !source.Descriptor.Available) yield break;

        var requiresResync = false;
        var cancelled = false;
        await using var watcher = _platform.WatchAsync(source.ChannelName, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            WindowsEventLogWatchItem item;
            try
            {
                if (!await watcher.MoveNextAsync().ConfigureAwait(false)) break;
                item = watcher.Current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            catch (EventLogException) { requiresResync = true; break; }
            catch (UnauthorizedAccessException) { requiresResync = true; break; }

            if (item.RequiresResync)
            {
                yield return new(null, ResyncRequired: true);
                continue;
            }

            if (item.Entry is { } entry && entry.RecordId > 0 && Matches(entry, query)) yield return new(Map(entry, source.Descriptor.SourceId));
            else if (item.Entry is { RecordId: <= 0 }) yield return new(null, ResyncRequired: true);
        }

        if (cancelled) yield break;
        if (requiresResync) yield return new(null, ResyncRequired: true);
    }

    private static bool Matches(WindowsEventLogEntry entry, ClientLogQuery query) =>
        (query.FromUtc is null || entry.TimestampUtc >= query.FromUtc) &&
        (query.ToUtc is null || entry.TimestampUtc <= query.ToUtc) &&
        (query.Severities.Count == 0 || query.Severities.Contains(entry.Severity, StringComparer.OrdinalIgnoreCase)) &&
        (query.Categories is null || query.Categories.Count == 0 || query.Categories.Contains(entry.Channel, StringComparer.OrdinalIgnoreCase)) &&
        (query.Providers is null || query.Providers.Count == 0 || query.Providers.Contains(entry.Provider, StringComparer.OrdinalIgnoreCase)) &&
        (query.EventIds is null || query.EventIds.Count == 0 || query.EventIds.Contains(entry.EventId)) &&
        (string.IsNullOrWhiteSpace(query.Text) || entry.Message.Contains(query.Text, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesCursor(WindowsEventLogEntry entry, WindowsCursor? cursor)
    {
        if (cursor is not { } value) return true;
        return value.Before ? entry.RecordId < value.RecordId : entry.RecordId > value.RecordId;
    }

    private static ClientRuntimeLogRecord Map(WindowsEventLogEntry entry, string sourceId)
    {
        var (message, truncated) = Truncate(entry.Message);
        return new(EncodeCursor(sourceId, entry.RecordId), ClientRuntimeLogBuffer.NextGatewaySequence(), entry.TimestampUtc,
            entry.Severity, sourceId, entry.Channel, entry.Provider, entry.Provider, message, truncated,
            entry.EventId, entry.RecordId, entry.Machine);
    }

    private WindowsSource? GetSource(string sourceId)
    {
        lock (_sourceGate) return _sources.TryGetValue(sourceId, out var source) ? source : null;
    }

    private static WindowsSource CreateSource(WindowsEventLogChannel channel)
    {
        var sourceId = channel.Name.ToLowerInvariant() switch
        {
            "application" => "windows-eventlog-application",
            "system" => "windows-eventlog-system",
            "security" => "windows-eventlog-security",
            "setup" => "windows-eventlog-setup",
            "forwardedevents" => "windows-eventlog-forwarded-events",
            _ => $"windows-eventlog-channel-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(channel.Name))).ToLowerInvariant()[..16]}"
        };
        var descriptor = new ClientLogSourceDescriptor(sourceId, "event-log", channel.Name, "windows", channel.Readable,
            channel.Readable ? null : channel.UnavailableReason ?? "channel_unavailable", channel.Readable, channel.Readable, channel.Readable, FilterCapabilities);
        return new(channel.Name, descriptor);
    }

    private static int CommonChannelOrder(string channel) => Array.FindIndex(CommonChannels, value => string.Equals(value, channel, StringComparison.OrdinalIgnoreCase)) switch
    {
        var index when index >= 0 => index,
        _ => CommonChannels.Length
    };

    private static string BeforeCursor(string value) => $"before:{value}";

    private static string EncodeCursor(string sourceId, long recordId)
    {
        var payload = Encoding.UTF8.GetBytes($"{sourceId}\n{recordId.ToString(CultureInfo.InvariantCulture)}");
        return $"eventlog:{Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    private static bool TryParseCursor(string? value, string sourceId, out WindowsCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.Length > 256) return false;
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1) return false;
        var direction = value[..separator];
        if (!string.Equals(direction, "before", StringComparison.Ordinal) && !string.Equals(direction, "after", StringComparison.Ordinal)) return false;
        var encoded = value[(separator + 1)..];
        if (!encoded.StartsWith("eventlog:", StringComparison.Ordinal)) return false;

        try
        {
            var base64 = encoded[9..].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split('\n');
            if (parts.Length != 2 || !string.Equals(parts[0], sourceId, StringComparison.Ordinal) ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var recordId) || recordId <= 0) return false;
            cursor = new(string.Equals(direction, "before", StringComparison.Ordinal), recordId);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static (string Message, bool Truncated) Truncate(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaximumMessageBytes) return (value, false);
        var length = Math.Min(value.Length, MaximumMessageBytes - 32);
        return (string.Concat(value.AsSpan(0, length), "… [truncated]"), true);
    }

    private sealed record WindowsSource(string ChannelName, ClientLogSourceDescriptor Descriptor);
    private sealed record WindowsCursor(bool Before, long RecordId);

    private static IWindowsEventLogPlatform CreateDefaultPlatform() => OperatingSystem.IsWindows()
        ? new WindowsEventLogPlatform()
        : new UnsupportedWindowsEventLogPlatform();
}

internal sealed record WindowsEventLogChannel(string Name, bool Readable, string? UnavailableReason);

internal sealed record WindowsEventLogEntry(
    string Channel,
    long RecordId,
    long EventId,
    DateTimeOffset TimestampUtc,
    string Severity,
    string Provider,
    string? Machine,
    string Message);

internal sealed record WindowsEventLogWatchItem(WindowsEventLogEntry? Entry, bool RequiresResync = false);

internal interface IWindowsEventLogPlatform
{
    Task<IReadOnlyList<WindowsEventLogChannel>> DiscoverAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<WindowsEventLogEntry> ReadNewestAsync(string channel, CancellationToken cancellationToken);
    IAsyncEnumerable<WindowsEventLogWatchItem> WatchAsync(string channel, CancellationToken cancellationToken);
}

/// <summary>Thin wrapper over Eventing.Reader so discovery and watchers can be tested without a Windows host.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsEventLogPlatform : IWindowsEventLogPlatform
{
    public Task<IReadOnlyList<WindowsEventLogChannel>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.FromResult<IReadOnlyList<WindowsEventLogChannel>>([]);

        using var session = new EventLogSession();
        var channels = new List<WindowsEventLogChannel>();
        foreach (var channel in session.GetLogNames().Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            channels.Add(new(channel, CanRead(session, channel, out var reason), reason));
        }
        return Task.FromResult<IReadOnlyList<WindowsEventLogChannel>>(channels);
    }

    public async IAsyncEnumerable<WindowsEventLogEntry> ReadNewestAsync(string channel, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        using var session = new EventLogSession();
        var query = CreateQuery(session, channel);
        using var reader = new EventLogReader(query);
        while (reader.ReadEvent() is { } record)
        {
            using (record)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Map(channel, record);
            }
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<WindowsEventLogWatchItem> WatchAsync(string channel, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var events = Channel.CreateUnbounded<WindowsEventLogWatchItem>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        using var session = new EventLogSession();
        var query = CreateQuery(session, channel);
        using var watcher = new EventLogWatcher(query);
        EventHandler<EventRecordWrittenEventArgs>? handler = (_, args) =>
        {
            try
            {
                if (args.EventRecord is not { } record)
                {
                    events.Writer.TryWrite(new(null, RequiresResync: true));
                    return;
                }

                using (record) events.Writer.TryWrite(new(Map(channel, record)));
            }
            catch (EventLogException)
            {
                events.Writer.TryWrite(new(null, RequiresResync: true));
            }
            catch (UnauthorizedAccessException)
            {
                events.Writer.TryWrite(new(null, RequiresResync: true));
            }
        };
        watcher.EventRecordWritten += handler;
        watcher.Enabled = true;
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
        }
        finally
        {
            watcher.Enabled = false;
            watcher.EventRecordWritten -= handler;
            events.Writer.TryComplete();
        }
    }

    private static EventLogQuery CreateQuery(EventLogSession session, string channel) => new(channel, PathType.LogName)
    {
        Session = session,
        ReverseDirection = true,
        TolerateQueryErrors = false
    };

    private static bool CanRead(EventLogSession session, string channel, out string? reason)
    {
        try
        {
            using var reader = new EventLogReader(CreateQuery(session, channel));
            using var _ = reader.ReadEvent();
            reason = null;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "access_denied";
            return false;
        }
        catch (EventLogException)
        {
            reason = "channel_unavailable";
            return false;
        }
    }

    private static WindowsEventLogEntry Map(string channel, EventRecord record)
    {
        string message;
        try { message = record.FormatDescription() ?? $"Event {record.Id.ToString(CultureInfo.InvariantCulture)}"; }
        catch (EventLogException) { message = $"Event {record.Id.ToString(CultureInfo.InvariantCulture)}"; }
        var level = record.Level ?? 4;
        var severity = level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", 5 => "Verbose", _ => "Information" };
        return new(channel, record.RecordId ?? 0, record.Id, record.TimeCreated is { } time ? new DateTimeOffset(time.ToUniversalTime()) : DateTimeOffset.UtcNow,
            severity, record.ProviderName ?? "Windows Event Log", record.MachineName, message);
    }
}

internal sealed class UnsupportedWindowsEventLogPlatform : IWindowsEventLogPlatform
{
    public Task<IReadOnlyList<WindowsEventLogChannel>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WindowsEventLogChannel>>([]);
    public async IAsyncEnumerable<WindowsEventLogEntry> ReadNewestAsync(string channel, [EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<WindowsEventLogWatchItem> WatchAsync(string channel, [EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
}
