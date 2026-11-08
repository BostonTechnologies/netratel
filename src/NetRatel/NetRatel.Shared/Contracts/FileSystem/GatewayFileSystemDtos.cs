namespace NetRatel.Shared.Contracts.FileSystem;

/// <summary>
/// Agent-ID keyed file gateway response. It is intentionally separate from
/// the existing client-identity file contracts used by the primary client UI.
/// </summary>
public sealed record GatewayFileSystemEntryDto(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes)
{
    public DateTimeOffset? CreatedUtc { get; init; }
    public DateTimeOffset? LastModifiedUtc { get; init; }
}

public sealed record GatewayFileSystemListResponse(
    int TenantId,
    Guid AgentId,
    string Path,
    string Authority,
    IReadOnlyList<GatewayFileSystemEntryDto> Entries)
{
    public bool CanWriteFiles { get; init; }
    public bool CanCreateDirectory { get; init; }
    public bool CanDeleteDirectory { get; init; }
    public GatewayFileSystemCapabilities Capabilities { get; init; } = new();
}

public sealed record GatewayFileSystemCreateDirectoryRequest(string ParentPath, string Name);

public sealed record GatewayFileSystemWriteResponse(
    int TenantId,
    Guid AgentId,
    string Path,
    string Authority);

public sealed record GatewayFileSystemCapabilities
{
    public int Version { get; init; } = 1;
    public bool Stat { get; init; }
    public bool CreateFile { get; init; }
    public bool MoveEntry { get; init; }
    public bool DeleteFile { get; init; }
    public bool DeleteRecursive { get; init; }
    public bool ReadRange { get; init; }
    public bool ConditionalUpload { get; init; }
}

public sealed record GatewayFileSystemStatResponse(int TenantId, Guid AgentId, string Path, bool IsDirectory,
    long SizeBytes, DateTimeOffset LastModifiedUtc, DateTimeOffset? CreatedUtc, string ObservedVersion, bool CanReadRange);

public sealed record GatewayFileSystemMutationItem(string Path, string? DestinationPath = null);
public sealed record GatewayFileSystemMutationRequest(string Operation, IReadOnlyList<GatewayFileSystemMutationItem> Items,
    bool Recursive = false);
public sealed record GatewayFileSystemMutationResult(string Path, string? DestinationPath, string Outcome,
    string? Code = null, long AffectedEntries = 0);
public sealed record GatewayFileSystemMutationResponse(IReadOnlyList<GatewayFileSystemMutationResult> Items);
