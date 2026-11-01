namespace NetRatel.Web.Services;

/// <summary>
/// Presents operator-selected page sizes over the existing bounded, cached 20-row API.
/// The API remains authoritative for filtering, scan limits and release verification.
/// </summary>
public static class ClientReleaseCataloguePaging
{
    public const int SourcePageSize = 20;
    public const int MaximumSourcePages = 30;

    public static async Task<GitHubClientReleasePageModel> ReadAsync(
        Func<int, bool, CancellationToken, Task<GitHubClientReleasePageModel>> read,
        int page, int pageSize, bool refresh, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (pageSize is not (20 or 50 or 100)) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (page < 0 || page >= MaximumSourcePages * SourcePageSize / pageSize)
            throw new ArgumentOutOfRangeException(nameof(page));
        ct.ThrowIfCancellationRequested();
        var offset = checked(page * pageSize);
        var first = offset / SourcePageSize;
        var skip = offset % SourcePageSize;
        var last = (offset + pageSize - 1) / SourcePageSize;
        GitHubClientReleasePageModel? prefetched = null;
        // One forced read refreshes the entire upstream prefix needed by this view.
        // Subsequent slices use that cache, rather than repeatedly re-fetching GitHub.
        if (refresh) prefetched = await read(last, true, ct).ConfigureAwait(false);
        var items = new List<GitHubClientReleaseModel>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(prefetched?.Warning)) warnings.Add(prefetched.Warning);
        var scanLimit = prefetched?.ScanLimitReached == true;
        var hasMore = false;
        DateTimeOffset? refreshedAt = null;
        for (var sourcePage = first; sourcePage <= last; sourcePage++)
        {
            ct.ThrowIfCancellationRequested();
            var result = sourcePage == last && prefetched is not null
                ? prefetched : await read(sourcePage, false, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.Page != sourcePage || result.Items.Count > SourcePageSize)
                throw new HttpRequestException("The release catalogue returned an inconsistent page.");
            if (!string.IsNullOrWhiteSpace(result.Warning)) warnings.Add(result.Warning);
            scanLimit |= result.ScanLimitReached;
            refreshedAt = refreshedAt is null || result.RefreshedAtUtc < refreshedAt.Value ? result.RefreshedAtUtc : refreshedAt;
            items.AddRange(sourcePage == first ? result.Items.Skip(skip) : result.Items);
            // A short page with HasMore can be incomplete discovery, not an exhausted
            // catalogue. Keep its warning and require Refresh rather than skipping
            // unknown rows when the next display page starts at a larger offset.
            hasMore = result.HasMore && result.Items.Count == SourcePageSize;
            if (!hasMore) break;
        }
        var bufferedMore = items.Count > pageSize;
        return new GitHubClientReleasePageModel
        {
            Page = page,
            Items = items.Take(pageSize).ToList(),
            HasMore = bufferedMore || (hasMore && (page + 1) * pageSize < MaximumSourcePages * SourcePageSize),
            ScanLimitReached = scanLimit,
            RefreshedAtUtc = refreshedAt ?? DateTimeOffset.MinValue,
            Warning = warnings.Count == 0 ? null : string.Join(" ", warnings)
        };
    }
}
