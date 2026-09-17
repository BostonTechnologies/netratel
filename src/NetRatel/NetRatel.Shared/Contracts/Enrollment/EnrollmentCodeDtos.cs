namespace NetRatel.Shared.Contracts.Enrollment;

public sealed record IssueEnrollmentCodeRequest(
    int ValidForMinutes,
    int MaxUses,
    string? Note);

public sealed record IssuedEnrollmentCodeDto(
    Guid EnrollmentCodeId,
    string EnrollmentCode,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    int? MaxUses,
    string? Note);

public sealed record EnrollmentCodeDto(
    Guid EnrollmentCodeId,
    string? EnrollmentCode,
    string DisplayCode,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ValidFromUtc,
    DateTimeOffset ExpiresUtc,
    int Uses,
    int? MaxUses,
    bool IsRevoked,
    DateTimeOffset? RevokedUtc,
    string? Note,
    DateTimeOffset? LastUsedUtc,
    bool IsActive);

public sealed record RevokeEnrollmentCodeRequest(string? Reason);

public sealed record EnrollmentCodeListResponse(IReadOnlyList<EnrollmentCodeDto> Items);
