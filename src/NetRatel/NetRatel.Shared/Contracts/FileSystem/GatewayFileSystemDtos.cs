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
}

public sealed record GatewayFileSystemCreateDirectoryRequest(string ParentPath, string Name);

public sealed record GatewayFileSystemWriteResponse(
    int TenantId,
    Guid AgentId,
    string Path,
    string Authority);
