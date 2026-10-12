namespace NetRatel.Shared.Contracts.FileSystem;

public sealed record FileSystemEntryDto(
    string ParentPath,
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes)
{
    public DateTimeOffset? CreatedUtc { get; init; }
    public DateTimeOffset? LastModifiedUtc { get; init; }
}

public sealed record FileSystemListResponse(
    string RequestId,
    string Path,
    string Status,
    string? Error,
    IReadOnlyList<FileSystemEntryDto> Entries);

public sealed record FileSystemFileResponse(
    string RequestId,
    string Path,
    string Status,
    string? Error,
    string Content,
    long SizeBytes,
    string? Base64Content,
    string ContentType,
    string ViewerKind,
    bool IsEditable);

public sealed record FileSystemWriteRequest(
    string Path,
    string FileContent);

public sealed record FileSystemWriteResponse(
    string RequestId,
    string Path,
    string Status,
    string? Error);
