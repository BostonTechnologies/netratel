using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace NetRatel.API.Services;

public sealed record DownloadedGitHubAsset(long SizeBytes, string Sha256);

/// <summary>Downloads an asset ID from the fixed repository with explicit redirect policy.</summary>
public sealed class GitHubClientAssetDownloader(HttpClient http, IConfiguration configuration)
{
    private const long MaxAssetBytes = 1L << 30;
    private const int MaxRedirects = 3;
    private static readonly HashSet<string> AllowedRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"
    };

    public async Task<DownloadedGitHubAsset> DownloadAsync(
        long assetId,
        long expectedSize,
        string? expectedDigest,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (assetId <= 0 || expectedSize is <= 0 or > MaxAssetBytes)
            throw new InvalidDataException("The GitHub asset identity or size is invalid.");
        if (expectedDigest is not null && !IsDigest(expectedDigest))
            throw new InvalidDataException("The GitHub asset digest is malformed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var token = timeout.Token;
        var uri = new Uri($"https://api.github.com/repos/BostonTechnologies/netratel/releases/assets/{assetId}");
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            ValidateUri(uri, redirect == 0);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("NetRatel-ClientReleaseImporter");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            if (redirect == 0)
            {
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                var credential = configuration["GitHubReleases:ReadOnlyToken"];
                if (!string.IsNullOrWhiteSpace(credential))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirect == MaxRedirects || response.Headers.Location is null ||
                    !response.Headers.Location.IsAbsoluteUri)
                    throw new InvalidDataException("GitHub asset redirects exceeded the allowed limit or had no absolute destination.");
                uri = response.Headers.Location;
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException($"GitHub asset request returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is { } reportedLength && reportedLength != expectedSize)
                throw new InvalidDataException("GitHub asset length differs from release metadata.");

            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))
                ?? throw new InvalidOperationException("The asset output requires a parent directory.");
            Directory.CreateDirectory(directory);
            var created = false;
            try
            {
                await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous);
                created = true;
                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    total = checked(total + count);
                    if (total > expectedSize)
                        throw new InvalidDataException("GitHub asset exceeds its advertised size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                if (total != expectedSize)
                    throw new InvalidDataException("GitHub asset ended before its advertised size.");
                var actualDigest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (expectedDigest is not null && expectedDigest != "sha256:" + actualDigest)
                    throw new InvalidDataException("GitHub asset digest differs from downloaded bytes.");
                return new DownloadedGitHubAsset(total, actualDigest);
            }
            catch
            {
                if (created) File.Delete(outputPath);
                throw;
            }
        }
        throw new InvalidDataException("GitHub asset redirect policy rejected the response.");
    }

    private static void ValidateUri(Uri uri, bool initial)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns ||
            initial && (uri.Host != "api.github.com" ||
                !uri.AbsolutePath.StartsWith("/repos/BostonTechnologies/netratel/releases/assets/", StringComparison.Ordinal)) ||
            !initial && !AllowedRedirectHosts.Contains(uri.Host))
            throw new InvalidDataException("GitHub asset redirect destination is not an allowed HTTPS host.");
    }

    private static bool IsDigest(string digest) => digest.StartsWith("sha256:", StringComparison.Ordinal) &&
        digest.Length == 71 && digest.AsSpan(7).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
