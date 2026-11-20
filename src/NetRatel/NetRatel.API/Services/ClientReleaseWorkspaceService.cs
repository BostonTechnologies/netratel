using Microsoft.EntityFrameworkCore;
using NetRatel.API.Models;
using NetRatel.Infrastructure.Persistence;
using NuGet.Versioning;

namespace NetRatel.API.Services;

public sealed record ClientReleaseWorkspaceRow(string Version, string Name, string Channel,
    DateTimeOffset? PublishedAtUtc, string SourceState, GitHubClientRelease? Remote,
    IReadOnlyList<ClientArtifactSummaryDto> Packages, IReadOnlyList<ClientReleaseImportStatus> Imports,
    IReadOnlyList<ClientReleaseWorkspaceRuntime> Releases, ClientReleaseDeletionStatus? Cleanup, bool HasStaging = false)
{
    public bool HasLocal => HasStaging || Packages.Count != 0 || Imports.Count != 0 || Releases.Count != 0;
    public long ArchiveBytes => Packages.Sum(x => x.Size);
}
public sealed record ClientReleaseWorkspaceRuntime(Guid ReleaseId, string RuntimeId, string Version,
    string Channel, string Sha256, long SizeBytes, bool Enabled, DateTimeOffset PublishedAt);
public sealed record ClientReleaseWorkspacePage(IReadOnlyList<ClientReleaseWorkspaceRow> Items, int Page,
    int PageSize, int Total, DateTimeOffset RefreshedAtUtc, string? Warning, string[] SelectableVersions, bool CanManageCleanup = true)
{
    public Guid? DiscoveryId { get; init; }
    public bool HasMore { get; init; }
    public bool IsComplete { get; init; }
    public bool SourcePaged { get; init; }
}

/// <summary>Version identity comes from canonical metadata, never a friendly release name.
/// The local inventory is complete before filtering/paging and survives partial upstream discovery.</summary>
public sealed class ClientReleaseWorkspaceService(OrchestratorDbContext db, IClientArtifactsService artifacts,
    IGitHubClientReleaseCatalog upstream, ClientReleaseDeletionService? deletion = null)
{
    public Task<ClientReleaseWorkspacePage> ReadAsync(int page, int pageSize, string? search,
        string channel, string? runtime, string? localState, bool refresh, CancellationToken ct, bool includeManagement = true) =>
        ReadAsync(page, pageSize, search, channel, runtime, localState, refresh, null, ct, includeManagement);

    public async Task<ClientReleaseWorkspacePage> ReadAsync(int page, int pageSize, string? search,
        string channel, string? runtime, string? localState, bool refresh, Guid? discoveryId, CancellationToken ct,
        bool includeManagement = true)
    {
        if (page < 0 || pageSize is not (20 or 50 or 100) || page > int.MaxValue / pageSize ||
            channel is not ("all" or "stable" or "prerelease"))
            throw new ArgumentException("Use a valid page, 20/50/100 rows and release channel.");

        var packages = await artifacts.InventoryAsync(ct);
        var staging = new HashSet<string>(StringComparer.Ordinal);
        string? storageWarning = null;
        if (deletion is not null)
        {
            try { staging.UnionWith(await deletion.StagedVersionsAsync(ct)); }
            catch (IOException) { storageWarning = "Some upload staging could not be inspected safely. Resolve storage ownership before cleaning those files."; }
        }
        var imports = await db.ClientReleaseImportOperations.AsNoTracking().Include(x => x.Assets).ToListAsync(ct);
        var releases = includeManagement ? await db.ClientUpdateReleases.AsNoTracking().ToListAsync(ct) : [];
        var receipts = await db.ClientReleaseDeletionReceipts.AsNoTracking().ToListAsync(ct);
        var packagesByVersion = packages.ToLookup(x => x.Version, StringComparer.Ordinal);
        var importsByVersion = imports.ToLookup(x => x.Version, StringComparer.Ordinal);
        var releasesByVersion = releases.ToLookup(x => x.Version, StringComparer.Ordinal);
        var receiptsByVersion = receipts.ToDictionary(x => x.Version, StringComparer.Ordinal);
        var localVersions = packages.Select(x => x.Version).Concat(imports.Select(x => x.Version))
            .Concat(releases.Select(x => x.Version)).Concat(staging)
            .Concat(includeManagement ? receipts.Select(x => x.Version) : [])
            .Where(x => NuGetVersion.TryParse(x, out var parsed) && parsed.ToNormalizedString() == x)
            .Distinct(StringComparer.Ordinal);

        ClientReleaseWorkspaceRow CreateLocalRow(string version)
        {
            var receipt = receiptsByVersion.GetValueOrDefault(version);
            var suppressed = receipt?.Suppressed == true;
            return new ClientReleaseWorkspaceRow(version, version,
                NuGetVersion.Parse(version).IsPrerelease ? "prerelease" : "stable", null, "Unknown", null,
                suppressed ? [] : packagesByVersion[version].ToArray(),
                suppressed ? [] : importsByVersion[version].OrderByDescending(x => x.CreatedAtUtc).Select(x =>
                    new ClientReleaseImportStatus(x.Id, x.GitHubReleaseId, x.Tag, x.Version,
                        x.State, x.TotalBytes, x.DownloadedBytes, x.Error, x.CreatedAtUtc, x.UpdatedAtUtc, x.PublishedAtUtc,
                        x.Assets.Select(a => new ClientReleaseImportAssetStatus(a.RuntimeId, a.SourceName, a.State,
                            a.SourceSizeBytes, a.DownloadedBytes, a.SourceSha256, a.LocalSha256, a.Error)).ToArray(),
                        x.AutomaticPublishError)).ToArray(),
                suppressed ? [] : releasesByVersion[version].Select(x => new ClientReleaseWorkspaceRuntime(
                    x.PublicId, x.RuntimeId, x.Version, x.Channel, x.Sha256, x.SizeBytes, x.Enabled, x.PublishedAtUtc)).ToArray(),
                !includeManagement || receipt is null ? null : new(receipt.Id, receipt.Version, receipt.State, receipt.Suppressed,
                    receipt.UpdatedAtUtc, receipt.Error, receipt.State == "Deleted" ?
                    System.Text.Json.JsonSerializer.Deserialize<ClientReleaseDeletionPlan>(receipt.PlanJson)?.ReclaimedBytes ?? 0 : 0),
                staging.Contains(version));
        }

        var localRows = localVersions.ToDictionary(version => version, CreateLocalRow, StringComparer.Ordinal);
        var searchText = search?.Trim();
        bool Matches(ClientReleaseWorkspaceRow row) =>
            (channel == "all" || row.Channel == channel) &&
            MatchesRuntime(row, runtime) && MatchesLocalState(row, localState) && MatchesSearch(row, searchText);
        var localMatches = localRows.Values.Where(Matches)
            .OrderByDescending(row => NuGetVersion.Parse(row.Version)).ThenBy(row => row.Version, StringComparer.Ordinal).ToArray();
        var selectable = includeManagement
            ? localMatches.Where(x => x.HasLocal || x.Cleanup?.State is "Pending" or "Failed")
                .Select(x => x.Version).Take(101).ToArray()
            : Array.Empty<string>();
        var localOnly = localState is "local" or "enabled" or "disabled" or "cleanup";
        if (localOnly)
        {
            page = Math.Min(page, Math.Max(0, (localMatches.Length - 1) / pageSize));
            var offset = page * pageSize;
            return new(localMatches.Skip(offset).Take(pageSize).ToArray(), page, pageSize, localMatches.Length,
                default, storageWarning, selectable, includeManagement)
            { HasMore = (long)offset + pageSize < localMatches.Length, IsComplete = true };
        }

        // Local matches form the first part of the workspace independently of
        // discovery. The rest follows GitHub source slots, before filtering.
        if (refresh) page = 0;
        var localMatchVersions = localMatches.Select(x => x.Version).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<ClientReleaseWorkspaceRow> LocalPage() => localMatches.Skip(page * pageSize).Take(pageSize).ToArray();
        var localPage = LocalPage();

        async Task<GitHubReleaseDiscoverySlice> DiscoverAsync(bool force, Guid? id)
        {
            var sourceOffset = Math.Max(0, page * pageSize - localMatches.Length);
            // Check one source slot on the first page even when local matches
            // fill it. Later local-only slices reuse the same discovery.
            var count = page == 0 ? Math.Max(1, pageSize - localPage.Count) : pageSize - localPage.Count;
            return await upstream.DiscoverAsync(sourceOffset, count, id, force, ct);
        }

        var remote = await DiscoverAsync(refresh, discoveryId);
        var warning = remote.Warning;
        if (remote.Restart)
        {
            page = 0;
            localPage = LocalPage();
            remote = await DiscoverAsync(true, null);
        }

        ClientReleaseWorkspaceRow RemoteRow(GitHubClientRelease source) =>
            WithSource(localRows.GetValueOrDefault(source.Version) ?? CreateLocalRow(source.Version), source);
        bool IsAdditional(GitHubClientRelease source) => !localMatchVersions.Contains(source.Version);

        var rows = localPage.Select(row => WithSource(row, remote.DiscoveredVersions.GetValueOrDefault(row.Version)))
            .Concat(remote.Items.Where(IsAdditional).Select(RemoteRow).Where(Matches))
            .Take(pageSize).ToArray();
        var moreLocal = ((long)page + 1) * pageSize < localMatches.Length;
        var total = remote.Complete
            ? localMatches.Length + remote.DiscoveredVersions.Values.Where(IsAdditional).Select(RemoteRow).Count(Matches)
            : localMatches.Length + rows.Length - localPage.Count;
        return new(rows, page, pageSize, total, remote.CheckedAtUtc, remote.Warning ?? warning ?? storageWarning,
            selectable, includeManagement)
        {
            DiscoveryId = remote.DiscoveryId,
            HasMore = moreLocal || remote.HasMore,
            IsComplete = remote.Complete,
            SourcePaged = true
        };
    }

    private static ClientReleaseWorkspaceRow WithSource(ClientReleaseWorkspaceRow row, GitHubClientRelease? source) =>
        source is null ? row : row with
        {
            Name = source.Name,
            PublishedAtUtc = source.PublishedAtUtc,
            SourceState = row.Imports.Any(x => x.GitHubReleaseId != source.Id) ? "Changed" : "Available",
            Remote = source
        };

    private static bool MatchesRuntime(ClientReleaseWorkspaceRow row, string? runtime) =>
        string.IsNullOrWhiteSpace(runtime) || row.Packages.Any(x => x.Rid == runtime) ||
        row.Releases.Any(x => x.RuntimeId == runtime) || row.Imports.Any(x => x.Assets.Any(a => a.RuntimeId == runtime)) ||
        row.Remote?.ClientAssets.Any(x => x.RuntimeId == runtime) == true;

    private static bool MatchesLocalState(ClientReleaseWorkspaceRow row, string? localState) => localState switch
    {
        "local" => row.HasLocal,
        "remote" => !row.HasLocal && row.Cleanup is null && row.Remote is not null,
        "enabled" => row.Releases.Any(x => x.Enabled),
        "disabled" => row.Releases.Count != 0 && row.Releases.All(x => !x.Enabled),
        "cleanup" => row.Cleanup is not null,
        _ => true
    };

    private static bool MatchesSearch(ClientReleaseWorkspaceRow row, string? search) =>
        string.IsNullOrEmpty(search) || new[] { row.Name, row.Version, row.SourceState }
            .Concat(row.Packages.SelectMany(x => new[] { x.FileName, x.Sha256, x.Notes ?? "" }))
            .Any(x => x.Contains(search, StringComparison.OrdinalIgnoreCase));
}
