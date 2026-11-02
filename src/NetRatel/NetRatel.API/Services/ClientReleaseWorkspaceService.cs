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
    int PageSize, int Total, DateTimeOffset RefreshedAtUtc, string? Warning, string[] SelectableVersions, bool CanManageCleanup = true);

/// <summary>Version identity comes from canonical metadata, never a friendly release name.
/// The local inventory is complete before filtering/paging and survives partial upstream discovery.</summary>
public sealed class ClientReleaseWorkspaceService(OrchestratorDbContext db, IClientArtifactsService artifacts,
    IGitHubClientReleaseCatalog upstream, ClientReleaseDeletionService? deletion = null)
{
    public async Task<ClientReleaseWorkspacePage> ReadAsync(int page, int pageSize, string? search,
        string channel, string? runtime, string? localState, bool refresh, CancellationToken ct, bool includeManagement = true)
    {
        if (page < 0 || pageSize is not (20 or 50 or 100) || channel is not ("all" or "stable" or "prerelease"))
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
        var remote = new List<GitHubClientRelease>();
        DateTimeOffset checkedAt = default;
        string? warning = storageWarning;
        try
        {
            // Upstream has a documented bounded scan; it never bounds the local estate.
            for (var sourcePage = 0; sourcePage < 30; sourcePage++)
            {
                var result = await upstream.ListAsync("all", sourcePage, refresh && sourcePage == 0, ct);
                remote.AddRange(result.Items); warning ??= result.Warning;
                checkedAt = result.RefreshedAtUtc;
                if (!result.HasMore || result.ScanLimitReached || result.Warning is not null) break;
                if (sourcePage == 29) warning ??= "Upstream scan limit reached. All local versions remain included; older remote releases may exist.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        { warning = "GitHub discovery is unavailable. All retained local versions remain manageable; source absence is unconfirmed."; }
        var versions = packages.Select(x => x.Version).Concat(imports.Select(x => x.Version))
            .Concat(releases.Select(x => x.Version)).Concat(staging).Concat(includeManagement ? receipts.Select(x => x.Version) : []).Concat(remote.Select(x => x.Version))
            .Where(x => NuGetVersion.TryParse(x, out var parsed) && parsed.ToNormalizedString() == x).Distinct(StringComparer.Ordinal);
        var rows = versions.Select(version =>
        {
            var source = remote.Where(x => x.Version == version).OrderByDescending(x => x.PublishedAtUtc).ThenByDescending(x => x.Id).FirstOrDefault();
            var localImports = imports.Where(x => x.Version == version).OrderByDescending(x => x.CreatedAtUtc).ToArray();
            var receipt = receipts.FirstOrDefault(x => x.Version == version);
            var suppressed = receipt?.Suppressed == true;
            return new ClientReleaseWorkspaceRow(version, source?.Name ?? version,
                NuGetVersion.Parse(version).IsPrerelease ? "prerelease" : "stable", source?.PublishedAtUtc,
                source is null ? "Unknown" : localImports.Any(x => x.GitHubReleaseId != source.Id) ? "Changed" : "Available", source,
                suppressed ? [] : packages.Where(x => x.Version == version).ToArray(),
                suppressed ? [] : localImports.Select(x => new ClientReleaseImportStatus(x.Id, x.GitHubReleaseId, x.Tag, x.Version,
                    x.State, x.TotalBytes, x.DownloadedBytes, x.Error, x.CreatedAtUtc, x.UpdatedAtUtc, x.PublishedAtUtc,
                    x.Assets.Select(a => new ClientReleaseImportAssetStatus(a.RuntimeId, a.SourceName, a.State,
                        a.SourceSizeBytes, a.DownloadedBytes, a.SourceSha256, a.LocalSha256, a.Error)).ToArray(), x.AutomaticPublishError)).ToArray(),
                suppressed ? [] : releases.Where(x => x.Version == version).Select(x => new ClientReleaseWorkspaceRuntime(
                    x.PublicId, x.RuntimeId, x.Version, x.Channel, x.Sha256, x.SizeBytes, x.Enabled, x.PublishedAtUtc)).ToArray(),
                !includeManagement || receipt is null ? null : new(receipt.Id, receipt.Version, receipt.State, receipt.Suppressed, receipt.UpdatedAtUtc, receipt.Error, receipt.State == "Deleted" ?
                    System.Text.Json.JsonSerializer.Deserialize<ClientReleaseDeletionPlan>(receipt.PlanJson)?.ReclaimedBytes ?? 0 : 0), staging.Contains(version));
        }).Where(row => channel == "all" || row.Channel == channel)
          .Where(row => string.IsNullOrWhiteSpace(runtime) || row.Packages.Any(x => x.Rid == runtime) ||
              row.Releases.Any(x => x.RuntimeId == runtime) || row.Imports.Any(x => x.Assets.Any(a => a.RuntimeId == runtime)) ||
              row.Remote?.ClientAssets.Any(x => x.RuntimeId == runtime) == true)
          .Where(row => localState switch { "local" => row.HasLocal, "remote" => !row.HasLocal && row.Cleanup is null,
              "enabled" => row.Releases.Any(x => x.Enabled), "disabled" => row.Releases.Count != 0 && row.Releases.All(x => !x.Enabled),
              "cleanup" => row.Cleanup is not null, _ => true })
          .Where(row => string.IsNullOrWhiteSpace(search) || new[] { row.Name, row.Version, row.SourceState }
              .Concat(row.Packages.SelectMany(x => new[] { x.FileName, x.Sha256, x.Notes ?? "" }))
              .Any(x => x.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)))
          .OrderByDescending(row => NuGetVersion.Parse(row.Version)).ThenBy(row => row.Version, StringComparer.Ordinal).ToArray();
        page = Math.Min(page, Math.Max(0, (rows.Length - 1) / pageSize));
        return new(rows.Skip(page * pageSize).Take(pageSize).ToArray(), page, pageSize, rows.Length, checkedAt, warning,
            rows.Where(x => x.HasLocal || x.Cleanup?.State is "Pending" or "Failed").Select(x => x.Version).Take(101).ToArray(), includeManagement);
    }
}
