using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Web.Services.Monitoring;

/// <summary>Independent local projections over the complete bounded authorized snapshot.</summary>
public sealed class MonitoringManagementQuery
{
    public string Search { get; set; } = "";
    public string Filter { get; set; } = "all";
    public string Severity { get; set; } = "all";
    public string Metric { get; set; } = "all";
    public string Sort { get; set; } = "name";
    public bool Descending { get; set; }
    public int Size { get; set; } = 20;
    public int Page { get; set; } = 1;
    public bool Matches(params string?[] values) => string.IsNullOrWhiteSpace(Search) || values.Any(value => value?.Contains(Search, StringComparison.OrdinalIgnoreCase) == true);
    public ManagementPage<T> Project<T, TKey>(IEnumerable<T> matching, Func<T, TKey> key, Func<T, Guid> id)
    {
        Size = Size is 20 or 50 or 100 ? Size : 20;
        var sorted = Descending ? matching.OrderByDescending(key, Comparer<TKey>.Default).ThenBy(id) : matching.OrderBy(key, Comparer<TKey>.Default).ThenBy(id);
        var all = sorted.ToArray();
        Page = Math.Clamp(Page, 1, Math.Max(1, (all.Length + Size - 1) / Size));
        var offset = (Page - 1) * Size;
        return new(all.Skip(offset).Take(Size).ToArray(), all.Length, all.Length == 0 ? 0 : offset + 1, Math.Min(offset + Size, all.Length), Page, Size);
    }
}

public sealed record ManagementPage<T>(IReadOnlyList<T> Items, int Total, int From, int To, int Page, int Size)
{
    public bool CanPrevious => Page > 1;
    public bool CanNext => To < Total;
    public string Range => $"{From}–{To} of {Total} matching";
}

public sealed class MonitoringManagementWorkspace
{
    public int TenantId { get; set; }
    public Dictionary<string, MonitoringManagementQuery> Queries { get; } = [];
}
