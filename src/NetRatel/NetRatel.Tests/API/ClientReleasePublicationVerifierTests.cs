using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.API.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientReleasePublicationVerifierTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Name = "netratel-client-1.2.3-linux-x64.tar.gz";
    private static readonly string Hash = new('b', 64);

    [Fact]
    public async Task CompleteConsistentPublicationDeclaresExactlyTheClientPack()
    {
        var directory = NewDirectory();
        try
        {
            var release = MakeFixture(directory, "complete", Hash);
            var result = await ClientReleasePublicationVerifier.VerifyAsync(release,
                Path.Combine(directory, "publication.json"), Path.Combine(directory, "SHA256SUMS"),
                Commit, TestContext.Current.CancellationToken);
            Assert.Equal(Commit, result.BuildCommit);
            Assert.Single(result.Assets);
            Assert.Equal(Hash, result.Assets[0].SourceSha256);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("incomplete", false)]
    [InlineData("complete", true)]
    public async Task IncompleteOrConflictingPublicationIsRejected(string state, bool corruptChecksums)
    {
        var directory = NewDirectory();
        try
        {
            var release = MakeFixture(directory, state, Hash);
            if (corruptChecksums)
                await File.AppendAllTextAsync(Path.Combine(directory, "SHA256SUMS"), new string('c', 64) + "  publication.json\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => ClientReleasePublicationVerifier.VerifyAsync(
                release, Path.Combine(directory, "publication.json"), Path.Combine(directory, "SHA256SUMS"),
                Commit, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DownloadedArchiveMustMatchDeclaredLengthAndHash()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, Name);
            await File.WriteAllTextAsync(path, "payload");
            var source = new VerifiedClientSourceAsset(1, Name, "linux-x64", 7, Hash);
            await Assert.ThrowsAsync<InvalidDataException>(() => ClientReleasePublicationVerifier.VerifyDownloadedAssetAsync(
                path, source, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static GitHubClientRelease MakeFixture(string directory, string state, string hash)
    {
        var publication = JsonSerializer.Serialize(new
        {
            productVersion = "1.2.3",
            publicCommit = Commit,
            verification = new { state },
            inputReceipt = new
            {
                repository = "BostonTechnologies/netratel",
                productVersion = "1.2.3",
                headSha = Commit,
                files = new Dictionary<string, object> { [Name] = new { sha256 = hash } }
            },
            artifacts = new Dictionary<string, string> { [Name] = hash }
        });
        File.WriteAllText(Path.Combine(directory, "publication.json"), publication);
        var publicationHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publication))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(directory, "SHA256SUMS"), $"{hash}  {Name}\n{publicationHash}  publication.json\n");
        return new GitHubClientRelease(1, "v1.2.3", "1.2.3", "fixture", DateTimeOffset.UtcNow,
            false, "https://github.com/BostonTechnologies/netratel/releases/tag/v1.2.3",
            [new GitHubClientAsset(1, Name, "linux-x64", 7, "sha256:" + hash)], 7,
            "verification required");
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netratel-publication-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
