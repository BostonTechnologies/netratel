namespace NetRatel.Shared.Contracts;

public sealed record ClientLogEntryDto(
    int Id,
    string ClientIdentity,
    int? TenantId,
    DateTimeOffset Timestamp,
    string Source,
    string Line);
