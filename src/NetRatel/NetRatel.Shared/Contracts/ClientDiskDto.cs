namespace NetRatel.Shared.Contracts;

public sealed record ClientDiskDto(
    string DriveLetter,
    long TotalBytes,
    long FreeBytes
);