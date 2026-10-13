namespace NetRatel.Shared.Contracts.Netclaw;

public static class AiPromptLimits
{
    public const int MaxBodyUtf8Bytes = 8192;
    public const int MaxSelected = 4;
    public const int MaxListItems = 100;
    public const int MaxNameLength = 128;
    public const int MaxDescriptionLength = 1024;
}

public sealed record AiPromptReference(Guid PromptId, long Revision);
public sealed record AiPromptSnapshot(Guid PromptId, long Revision, string Name, string? Description, string Body, bool IsDefault = false);
public sealed record AiPromptTenantDto(int TenantId, string TenantName, bool CanManage);
public sealed record AiPromptSummaryDto(Guid Id, string Name, string? Description, long Revision, bool Archived, bool IsDefault,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, string CreatedBy, string UpdatedBy);
public sealed record AiPromptDetailDto(AiPromptSummaryDto Summary, string Body);
public sealed record AiPromptDefaultDto(long Revision, AiPromptSnapshot? Prompt);
public sealed record AiPromptListDto(IReadOnlyList<AiPromptSummaryDto> Items, AiPromptDefaultDto Default, bool HasMore);
public sealed record AiPromptSaveRequest(string Name, string? Description, string Body, long ExpectedRevision = 0);
public sealed record AiPromptSetDefaultRequest(Guid? PromptId, long ExpectedRevision, long? ExpectedPromptRevision = null);
public sealed record AiPromptArchiveRequest(long ExpectedRevision, long ExpectedDefaultRevision, bool RemoveDefault = false);
