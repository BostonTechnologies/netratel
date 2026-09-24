using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using NuGet.Versioning;

namespace NetRatel.API.Services;

public sealed record GitHubClientAsset(long Id, string Name, string RuntimeId, long SizeBytes, string? Sha256Digest);

public sealed record GitHubClientRelease(
    long Id,
    string Tag,
    string Version,
    string Name,
    DateTimeOffset PublishedAtUtc,
    bool IsPrerelease,
    string DetailsUrl,
    IReadOnlyList<GitHubClientAsset> ClientAssets,
    long TotalClientBytes,
    string PublicationState);

public sealed record GitHubClientReleasePage(
    IReadOnlyList<GitHubClientRelease> Items,
    int Page,
    bool HasMore,
    bool ScanLimitReached,
    DateTimeOffset RefreshedAtUtc,
    string? Warning);

public interface IGitHubClientReleaseCatalog
{
    Task<GitHubClientReleasePage> ListAsync(string channel, int page, bool refresh, CancellationToken cancellationToken);
    Task<GitHubClientRelease?> FindAsync(long releaseId, CancellationToken cancellationToken);
}

/// <summary>
/// A bounded cache of the fixed official release source. Asset bytes and the
/// publication record are verified by the import path before an item is eligible
/// for deployment; catalogue metadata alone never authorizes publication.
/// </summary>
public sealed class GitHubClientReleaseCatalog(
    HttpClient http,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<GitHubClientReleaseCatalog> logger) : IGitHubClientReleaseCatalog
{
    private const string Repository = "BostonTechnologies/netratel";
    private const int UpstreamPageSize = 30;
    private const int DisplayPageSize = 20;
    private const int MaxUpstreamPages = 20;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> SupportedRuntimes = new(StringComparer.Ordinal)
    {
        "win-x64", "win-arm64", "linux-x64", "osx-x64", "osx-arm64"
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, CachedPage> _pages = new();
    private DateTimeOffset _retryAfterUtc;
    private string? _warning;
    private int _consecutiveFailures;

    public async Task<GitHubClientReleasePage> ListAsync(string channel, int page, bool refresh, CancellationToken cancellationToken)
    {
        if (page is < 0 or > 29)
            throw new ArgumentOutOfRangeException(nameof(page));
        if (channel is not ("all" or "stable" or "prerelease"))
            throw new ArgumentException("Channel must be all, stable, or prerelease.", nameof(channel));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var desiredCount = checked((page + 1) * DisplayPageSize + 1);
            var filtered = new List<GitHubClientRelease>();
            var upstreamHasNext = true;
            var scannedPages = 0;
            var now = clock.GetUtcNow();
            for (var upstreamPage = 1; upstreamPage <= MaxUpstreamPages && upstreamHasNext && filtered.Count < desiredCount; upstreamPage++)
            {
                var cached = await GetUpstreamPageAsync(upstreamPage, refresh, cancellationToken).ConfigureAwait(false);
                if (cached is null)
                    break;
                scannedPages++;
                filtered.AddRange(cached.Releases.Where(item => channel == "all" ||
                    item.IsPrerelease == (channel == "prerelease")));
                upstreamHasNext = cached.HasNext;
            }

            var first = page * DisplayPageSize;
            var items = filtered.Skip(first).Take(DisplayPageSize).ToArray();
            var limitReached = scannedPages == MaxUpstreamPages && upstreamHasNext && filtered.Count < desiredCount;
            var hasMore = filtered.Count > first + items.Length ||
                          upstreamHasNext && scannedPages > 0 && !limitReached;
            var refreshedAt = _pages.Count == 0 ? DateTimeOffset.MinValue : _pages.Values.Min(x => x.FetchedAtUtc);
            return new GitHubClientReleasePage(items, page, hasMore, limitReached, refreshedAt,
                _warning ?? (limitReached ? "The scan limit was reached; older matching releases may exist." : null));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<GitHubClientRelease?> FindAsync(long releaseId, CancellationToken cancellationToken)
    {
        if (releaseId <= 0) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var page = 1; page <= MaxUpstreamPages; page++)
            {
                var cached = await GetUpstreamPageAsync(page, false, cancellationToken).ConfigureAwait(false);
                if (cached is null) return null;
                var found = cached.Releases.FirstOrDefault(x => x.Id == releaseId);
                if (found is not null) return found;
                if (!cached.HasNext) return null;
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CachedPage?> GetUpstreamPageAsync(int page, bool refresh, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        _pages.TryGetValue(page, out var prior);
        if (!refresh && prior is not null && now - prior.FetchedAtUtc < CacheLifetime)
            return prior;
        if (now < _retryAfterUtc)
        {
            _warning = "GitHub requests are rate limited; cached releases remain available.";
            return prior;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"repos/{Repository}/releases?per_page={UpstreamPageSize}&page={page}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("NetRatel-ClientReleaseCatalog");
        var token = configuration["GitHubReleases:ReadOnlyToken"];
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (prior?.ETag is not null)
            request.Headers.IfNoneMatch.Add(prior.ETag);

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && prior is not null)
            {
                _consecutiveFailures = 0;
                _warning = null;
                return _pages[page] = prior with { FetchedAtUtc = now };
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - now) ?? TimeSpan.FromMinutes(1);
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
                    long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var resetEpoch) &&
                    resetEpoch > now.ToUnixTimeSeconds())
                    delay = TimeSpan.FromSeconds(Math.Max(delay.TotalSeconds, resetEpoch - now.ToUnixTimeSeconds()));
                _retryAfterUtc = now + TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds + Random.Shared.NextDouble() * 5, 5, 3600));
                _warning = "GitHub requests are rate limited; cached releases remain available.";
                return prior;
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                BackOff(now);
                _warning = "GitHub release metadata is temporarily unavailable; cached releases remain available.";
                logger.LogWarning("GitHub release catalogue returned {StatusCode}", (int)response.StatusCode);
                return prior;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("GitHub release list must be an array.");
            var releases = document.RootElement.EnumerateArray()
                .Select(TryParseRelease)
                .Where(x => x is not null)
                .Cast<GitHubClientRelease>()
                .ToArray();
            var hasNext = response.Headers.TryGetValues("Link", out var links) &&
                          links.Any(x => x.Contains("rel=\"next\"", StringComparison.Ordinal));
            _consecutiveFailures = 0;
            _warning = null;
            return _pages[page] = new CachedPage(releases, hasNext, response.Headers.ETag, now);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            logger.LogWarning(exception, "GitHub release catalogue refresh failed");
            BackOff(now);
            _warning = "GitHub release metadata is temporarily unavailable; cached releases remain available.";
            return prior;
        }
    }

    private void BackOff(DateTimeOffset now)
    {
        _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 5);
        var seconds = Math.Min(15 * (1 << _consecutiveFailures), 300) + Random.Shared.NextDouble() * 5;
        _retryAfterUtc = now.AddSeconds(seconds);
    }

    private static GitHubClientRelease? TryParseRelease(JsonElement release)
    {
        try
        {
            return ParseRelease(release);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static GitHubClientRelease? ParseRelease(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object ||
            release.GetProperty("draft").GetBoolean() ||
            !release.TryGetProperty("published_at", out var published) || published.ValueKind == JsonValueKind.Null)
            return null;
        var id = release.GetProperty("id").GetInt64();
        var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
        var textVersion = tag.StartsWith('v') ? tag[1..] : tag;
        if (!NuGetVersion.TryParse(textVersion, out var version) ||
            !string.Equals(version.ToNormalizedString(), textVersion, StringComparison.OrdinalIgnoreCase))
            return null;
        var isPrerelease = release.GetProperty("prerelease").GetBoolean();
        var consistent = isPrerelease == version.IsPrerelease;
        var assets = new List<GitHubClientAsset>();
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("state").GetString(), "uploaded", StringComparison.Ordinal)) continue;
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            foreach (var runtime in SupportedRuntimes)
            {
                var stem = $"netratel-client-{version.ToNormalizedString()}-{runtime}";
                if (name is not null && (name == stem + ".zip" || name == stem + ".tar.gz"))
                {
                    assets.Add(new GitHubClientAsset(asset.GetProperty("id").GetInt64(), name, runtime,
                        asset.GetProperty("size").GetInt64(), asset.TryGetProperty("digest", out var digest) ? digest.GetString() : null));
                    break;
                }
            }
        }
        var hasPublication = release.GetProperty("assets").EnumerateArray().Any(x => x.GetProperty("name").GetString() == "publication.json");
        var hasChecksums = release.GetProperty("assets").EnumerateArray().Any(x => x.GetProperty("name").GetString() == "SHA256SUMS");
        var state = !consistent ? "incompatible prerelease metadata" :
            !hasPublication || !hasChecksums || assets.Count == 0 ? "incomplete publication evidence" :
            assets.Select(x => x.RuntimeId).Distinct(StringComparer.Ordinal).Count() != assets.Count ? "duplicate runtime assets" :
            "verification required";
        return new GitHubClientRelease(id, tag, version.ToNormalizedString(),
            release.GetProperty("name").GetString() ?? tag,
            published.GetDateTimeOffset(), isPrerelease,
            $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}",
            assets, assets.Sum(x => x.SizeBytes), state);
    }

    private sealed record CachedPage(IReadOnlyList<GitHubClientRelease> Releases, bool HasNext, EntityTagHeaderValue? ETag, DateTimeOffset FetchedAtUtc);
}
