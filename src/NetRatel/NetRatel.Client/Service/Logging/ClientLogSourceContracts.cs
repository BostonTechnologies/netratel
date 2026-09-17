using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Client.Service.Logging.Linux;
using NetRatel.Client.Service.Logging.Windows;

namespace NetRatel.Client.Service.Logging;

/// <summary>
/// Agent-owned structured log provider. Implementations must map source ids to
/// local allowlists and must never execute a browser-supplied query language.
/// </summary>
internal interface IClientLogSourceAdapter
{
    Task<IReadOnlyList<ClientLogSourceDescriptor>> DiscoverAsync(CancellationToken cancellationToken);
    Task<ClientLogPage> ReadHistoryAsync(ClientLogQuery query, CancellationToken cancellationToken);
    IAsyncEnumerable<ClientLogFollowResult> FollowAsync(ClientLogQuery query, CancellationToken cancellationToken);
}

internal sealed record ClientLogSourceDescriptor(
    string SourceId,
    string Kind,
    string DisplayName,
    string Platform,
    bool Available,
    string? UnavailableReason,
    bool SupportsLive,
    bool SupportsHistory,
    bool SupportsPaging,
    IReadOnlyList<string> FilterCapabilities);

internal sealed record ClientLogQuery(
    string SourceId,
    string? Cursor,
    int PageSize,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    IReadOnlyList<string> Severities,
    IReadOnlyList<string> Prefixes,
    string? Text,
    IReadOnlyList<string>? Providers = null,
    IReadOnlyList<long>? EventIds = null,
    IReadOnlyList<string>? Categories = null);

internal sealed record ClientLogPage(
    IReadOnlyList<ClientRuntimeLogRecord> Records,
    string? NextCursor,
    string? PreviousCursor,
    bool HasMore,
    ulong DroppedRecordCount = 0,
    bool ResyncRequired = false,
    string? ErrorCode = null);

/// <summary>
/// A live provider can signal an Event Log watcher gap without fabricating a
/// log record. The gateway serializes that as an empty resync batch.
/// </summary>
internal sealed record ClientLogFollowResult(ClientRuntimeLogRecord? Record, bool ResyncRequired = false);

internal static class ClientLogSourceAdapterFactory
{
    public static IClientLogSourceAdapter Create() => OperatingSystem.IsWindows()
        ? new WindowsEventLogSourceAdapter()
        : new LinuxLogSourceAdapter();
}
