namespace NetRatel.Shared.Contracts;

/// <summary>Capability-advertised, browser-safe description of a log source.</summary>
public sealed record GatewayLogSourceDescriptorDto(
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

/// <summary>
/// Structured transient log record. Cursor and sequence, rather than message
/// text, establish identity so equal messages are deliberately retained.
/// </summary>
public sealed record GatewayLogRecordDto(
    string Cursor,
    ulong Sequence,
    DateTimeOffset TimestampUtc,
    string Severity,
    string SourceId,
    string? Category,
    string? Prefix,
    string? Provider,
    long? EventId,
    long? RecordId,
    string? Machine,
    string Message,
    IReadOnlyDictionary<string, string>? StructuredProperties,
    bool Truncated);

/// <summary>Typed, bounded filters shared by history and live-log requests.</summary>
public sealed record GatewayLogQueryFilters(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    IReadOnlyList<string>? Severities = null,
    IReadOnlyList<string>? Prefixes = null,
    IReadOnlyList<string>? Providers = null,
    IReadOnlyList<long>? EventIds = null,
    string? Text = null,
    IReadOnlyList<string>? Categories = null);

public sealed record GatewayLogPageRequest(
    string SourceId,
    string? Cursor,
    int PageSize,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    IReadOnlyList<string>? Severities,
    IReadOnlyList<string>? Prefixes,
    string? Text,
    IReadOnlyList<string>? Providers = null,
    IReadOnlyList<long>? EventIds = null,
    IReadOnlyList<string>? Categories = null);

public sealed record GatewayLogPageDto(
    IReadOnlyList<GatewayLogRecordDto> Records,
    string? NextCursor,
    string? PreviousCursor,
    bool HasMore,
    ulong DroppedRecordCount,
    bool ResyncRequired,
    string? ErrorCode = null);

/// <summary>Live, browser-safe log batch sent only through an authorized hub group.</summary>
public sealed record GatewayLogBatchDto(
    int TenantId,
    Guid AgentId,
    string SessionId,
    IReadOnlyList<GatewayLogRecordDto> Records,
    string? NextCursor,
    ulong DroppedRecordCount,
    bool ResyncRequired);
