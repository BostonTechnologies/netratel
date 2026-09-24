using System.Security.Cryptography;
using System.Text.Json;

namespace NetRatel.API.Services;

public sealed record VerifiedClientSourceAsset(
    long AssetId,
    string Name,
    string RuntimeId,
    long SizeBytes,
    string SourceSha256);

public sealed record VerifiedClientPublication(
    string Repository,
    string Tag,
    string Version,
    string BuildCommit,
    IReadOnlyList<VerifiedClientSourceAsset> Assets);

/// <summary>
/// Cross-checks the publisher's completed inventory, checksum file, immutable
/// tag commit, and GitHub asset metadata before any client archive is imported.
/// This establishes consistent source provenance, not an independent signature.
/// </summary>
public static class ClientReleasePublicationVerifier
{
    private const string Repository = "BostonTechnologies/netratel";
    private const int MaxPublicationBytes = 2 * 1024 * 1024;
    private const int MaxChecksumsBytes = 1024 * 1024;

    public static async Task<VerifiedClientPublication> VerifyAsync(
        GitHubClientRelease release,
        string publicationPath,
        string checksumsPath,
        string tagCommit,
        CancellationToken cancellationToken)
    {
        if (release.PublicationState != "verification required" ||
            tagCommit.Length != 40 || !tagCommit.All(Uri.IsHexDigit))
            throw new InvalidDataException("Release metadata is not eligible for verification.");
        var publicationBytes = await ReadBoundedAsync(publicationPath, MaxPublicationBytes, cancellationToken).ConfigureAwait(false);
        var sumsBytes = await ReadBoundedAsync(checksumsPath, MaxChecksumsBytes, cancellationToken).ConfigureAwait(false);
        var sums = ParseChecksums(sumsBytes);
        var publicationHash = Convert.ToHexString(SHA256.HashData(publicationBytes)).ToLowerInvariant();
        if (!sums.TryGetValue("publication.json", out var expectedPublicationHash) ||
            !string.Equals(publicationHash, expectedPublicationHash, StringComparison.Ordinal))
            throw new InvalidDataException("The publication record differs from SHA256SUMS.");

        try
        {
            using var document = JsonDocument.Parse(publicationBytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var receipt = root.GetProperty("inputReceipt");
            var recordCommit = root.GetProperty("publicCommit").GetString();
            if (root.GetProperty("verification").GetProperty("state").GetString() != "complete" ||
                root.GetProperty("productVersion").GetString() != release.Version ||
                receipt.GetProperty("productVersion").GetString() != release.Version ||
                receipt.GetProperty("repository").GetString() != Repository ||
                !string.Equals(recordCommit, tagCommit, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.GetProperty("headSha").GetString(), tagCommit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The completed publication record does not match the repository, tag, version, or build commit.");

            var files = receipt.GetProperty("files");
            var artifacts = root.GetProperty("artifacts");
            var declaredNames = files.EnumerateObject()
                .Select(item => item.Name)
                .Where(name => name.StartsWith($"netratel-client-{release.Version}-", StringComparison.Ordinal) &&
                    (name.EndsWith(".zip", StringComparison.Ordinal) || name.EndsWith(".tar.gz", StringComparison.Ordinal)))
                .ToArray();
            if (declaredNames.Length == 0 || declaredNames.Length != release.ClientAssets.Count)
                throw new InvalidDataException("The publication inventory does not match the advertised client pack.");

            var verified = new List<VerifiedClientSourceAsset>(declaredNames.Length);
            var runtimes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in declaredNames)
            {
                var asset = release.ClientAssets.SingleOrDefault(x => x.Name == name)
                    ?? throw new InvalidDataException("A declared client archive is missing from the release.");
                if (!runtimes.Add(asset.RuntimeId) || asset.SizeBytes <= 0)
                    throw new InvalidDataException("The client pack has duplicate runtimes or invalid asset size.");
                var receiptHash = files.GetProperty(name).GetProperty("sha256").GetString();
                var publicationArtifactHash = artifacts.GetProperty(name).GetString();
                if (!sums.TryGetValue(name, out var sumsHash) || !IsSha256(sumsHash) ||
                    !string.Equals(receiptHash, sumsHash, StringComparison.Ordinal) ||
                    !string.Equals(publicationArtifactHash, sumsHash, StringComparison.Ordinal) ||
                    asset.Sha256Digest is not null && asset.Sha256Digest != "sha256:" + sumsHash)
                    throw new InvalidDataException("Client source hashes disagree across publication evidence.");
                verified.Add(new VerifiedClientSourceAsset(asset.Id, name, asset.RuntimeId, asset.SizeBytes, sumsHash));
            }
            return new VerifiedClientPublication(Repository, release.Tag, release.Version, tagCommit, verified);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("The publication record is malformed.", exception);
        }
    }

    public static async Task VerifyDownloadedAssetAsync(
        string path,
        VerifiedClientSourceAsset evidence,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != evidence.SizeBytes)
            throw new InvalidDataException("The downloaded client archive length differs from its publication evidence.");
        await using var stream = file.OpenRead();
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        if (!string.Equals(digest, evidence.SourceSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The downloaded client archive differs from its publication evidence.");
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaxPublicationBytes || file.Length > limit)
            throw new InvalidDataException("Publication evidence is missing or exceeds its size limit.");
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseChecksums(byte[] contents)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in System.Text.Encoding.UTF8.GetString(contents).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.TrimEnd('\r');
            if (value.Length < 67 || value[64..66] != "  " || !IsSha256(value[..64]))
                throw new InvalidDataException("SHA256SUMS has an invalid entry.");
            var name = value[66..];
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') ||
                !result.TryAdd(name, value[..64]))
                throw new InvalidDataException("SHA256SUMS has an unsafe or duplicate asset name.");
        }
        return result;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
