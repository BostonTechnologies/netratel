namespace NetRatel.Shared.Contracts.Flows;

/// <summary>A graph-independent compare-and-set intent, shared by every deletion transport.</summary>
public sealed record FlowDeleteRequest(long ExpectedRevision);
public enum FlowDeleteDisposition { Deleted, AlreadyDeleted, NotFound, Conflict, ActiveWork, Invalid }
public sealed record FlowDeleteResultDto(int TenantId, Guid FlowId, long? Revision,
    FlowDeleteDisposition Disposition, string Code, DateTimeOffset? DeletedAtUtc = null)
{
    public string? HistoryPath { get; init; }
    public string? Explanation => Disposition switch
    {
        FlowDeleteDisposition.ActiveWork => "Current unfinished runs, leases or delivery/continuation obligations remain. Review authorized history; deletion did not disable, cancel or replay work.",
        FlowDeleteDisposition.Conflict => "The reviewed revision does not match this definition. Discover and review the exact target again; never substitute a newer revision automatically.",
        _ => null
    };
}

public enum FlowEnabledFilter { All, Enabled, Disabled }
public enum FlowPublicationFilter { All, Published, Unpublished }
public enum FlowSort { Name, UpdatedAt, LatestRunAt }
public sealed record FlowQueryDto(string? Search = null, FlowEnabledFilter Enabled = FlowEnabledFilter.All,
    FlowPublicationFilter Publication = FlowPublicationFilter.All, FlowSort Sort = FlowSort.Name,
    bool Descending = false, int PageIndex = 0, int PageSize = 20)
{
    public const int MaximumSearchLength = 128;
    public bool IsValid => (Search?.Trim().Length ?? 0) <= MaximumSearchLength &&
        Enum.IsDefined(Enabled) && Enum.IsDefined(Publication) && Enum.IsDefined(Sort) &&
        PageIndex >= 0 && PageSize is >= 1 and <= 100 && (long)PageIndex * PageSize <= int.MaxValue;
}
/// <summary>Safe list/history metadata: never includes draft graphs, prompts or execution pins.</summary>
public sealed record FlowSummaryDto(Guid Id, int TenantId, string Name, long Revision, bool Enabled,
    Guid? PublishedVersionId, int PublishedVersionNumber, DateTimeOffset UpdatedAtUtc,
    FlowRunSummaryDto? LatestRun = null, DateTimeOffset? DeletedAtUtc = null);
public sealed record FlowPageDto(IReadOnlyList<FlowSummaryDto> Items, int Total, int PageIndex, int PageSize);
