namespace NetRatel.Shared.Contracts.Jobs;

/// <summary>A tenant-owned explicit target set. Managing a group does not grant execution permission.</summary>
public sealed record ExecutionGroupDto(Guid Id, int TenantId, string Name, string? Description, long Revision,
    bool Enabled, bool Archived, int MemberCount, int OnlineCount, int OfflineCount,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record ExecutionGroupDetailsDto(ExecutionGroupDto Group, IReadOnlyList<JobNodeInventoryDto> Members);
public sealed record ExecutionGroupPageDto(IReadOnlyList<ExecutionGroupDto> Items, int Page, int PageSize, int TotalCount, int? NextPage);
public sealed record ExecutionGroupWriteDto(string Name, string? Description, bool Enabled, bool Archived,
    IReadOnlyList<Guid> AgentIds, long? ExpectedRevision = null);
public sealed record ExecutionGroupMembershipWriteDto(long ExpectedRevision, IReadOnlyList<Guid> Add, IReadOnlyList<Guid> Remove);
public sealed record ExecutionGroupReferenceDto(string Kind, string Id, string Name);

/// <summary>Last-known canonical directory metadata. Availability is separate from membership and eligibility.</summary>
public sealed record JobNodeInventoryDto(Guid AgentId, string Hostname, string DisplayName, string? Name,
    IReadOnlyList<string> Tags, string? OperatingSystem, string? Architecture, ClientEnvironment? Environment,
    bool IsOnline, bool IsEnabled, string? UnavailableReason);
public sealed record JobNodePageDto(IReadOnlyList<JobNodeInventoryDto> Items, int Page, int PageSize, int TotalCount, int? NextPage);
public sealed record JobNodePreviewRequest(string? Filter, ClientEnvironment? Environment = null, int Page = 1,
    int PageSize = 50, IReadOnlyDictionary<string, string[]>? Parameters = null);
public sealed record JobNodePreviewDto(IReadOnlyList<JobNodeInventoryDto> Items, int Page, int PageSize,
    int TotalCount, int? NextPage, string NormalizedFilter, string Digest);
public sealed record JobNodeFilterPropertyDto(string Name, string Description, bool SupportsRegex, bool SupportsAllOf = false);
public sealed record JobNodeFilterValidationDto(bool Valid, string? NormalizedFilter, string? Code = null, string? Message = null);

public enum JobNodeFilterOperandKind { Literal = 1, Regex = 2, Parameter = 3 }
public sealed record JobNodeFilterOperand(JobNodeFilterOperandKind Kind, string Value)
{
    /// <summary>Literal values bound from a parameter. These are never parsed as selector syntax.</summary>
    public IReadOnlyList<string>? BoundValues { get; init; }
}
public sealed record JobNodeFilterTerm(IReadOnlyList<JobNodeFilterOperand> Operands);
public sealed record JobNodeFilterClause(string Field, bool Excluded, IReadOnlyList<JobNodeFilterTerm> Alternatives);
public sealed record JobNodeFilterExpression(IReadOnlyList<JobNodeFilterClause> Clauses);
