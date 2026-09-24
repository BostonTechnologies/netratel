using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetRatel.API.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientReleaseArchiveAdapterTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task WrappedZipBecomesDeterministicFlatUpdaterPackage()
    {
        var work = NewWorkDirectory();
        try
        {
            var source = Path.Combine(work, "client.zip");
            CreateZip(source, "win-x64", "1.2.3", Commit,
                ("netratel-client-win-x64/NetRatel.Client.exe", "executable"),
                ("netratel-client-win-x64/updater/netratel-update.ps1", "updater"));
            var first = Path.Combine(work, "first.zip");
            var second = Path.Combine(work, "second.zip");

            var result = await ClientReleaseArchiveAdapter.NormalizeAsync(
                source, first, "win-x64", "1.2.3", Commit, TestContext.Current.CancellationToken);
            var repeated = await ClientReleaseArchiveAdapter.NormalizeAsync(
                source, second, "win-x64", "1.2.3", Commit, TestContext.Current.CancellationToken);

            Assert.Equal(ClientReleaseArchiveAdapter.Contract, result.Contract);
            Assert.Equal(result.Sha256, repeated.Sha256);
            Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
            Assert.NotEqual(await HashAsync(source), result.Sha256);
            using var archive = ZipFile.OpenRead(first);
            Assert.Contains(archive.Entries, entry => entry.FullName == "netratel-client-manifest.json");
            Assert.Contains(archive.Entries, entry => entry.FullName == "NetRatel.Client.exe");
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("netratel-client-win-x64/", StringComparison.Ordinal));
            await ClientArtifactManifestValidator.ValidateFileAsync(first, "win-x64", "1.2.3", TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Theory]
    [InlineData("netratel-client-win-x64/../escape", false)]
    [InlineData("netratel-client-win-x64/NetRatel.Client.exe", true)]
    [InlineData("netratel-client-win-x64/UPDATER/netratel-update.ps1", false)]
    public async Task UnsafeOrConflictingSourceEntriesAreRejected(string extraName, bool link)
    {
        var work = NewWorkDirectory();
        try
        {
            var source = Path.Combine(work, "client.zip");
            CreateZip(source, "win-x64", "1.2.3", Commit,
                ("netratel-client-win-x64/NetRatel.Client.exe", "executable"),
                ("netratel-client-win-x64/updater/netratel-update.ps1", "updater"));
            using (var stream = new FileStream(source, FileMode.Open, FileAccess.ReadWrite))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
            {
                var entry = archive.CreateEntry(extraName);
                if (link) entry.ExternalAttributes = 0xA000 << 16;
                using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("untrusted");
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => ClientReleaseArchiveAdapter.NormalizeAsync(
                source, Path.Combine(work, "output.zip"), "win-x64", "1.2.3", Commit,
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(work, "output.zip")));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task WrongPublicationCommitIsRejected()
    {
        var work = NewWorkDirectory();
        try
        {
            var source = Path.Combine(work, "client.zip");
            CreateZip(source, "win-x64", "1.2.3", Commit,
                ("netratel-client-win-x64/NetRatel.Client.exe", "executable"));
            await Assert.ThrowsAsync<InvalidDataException>(() => ClientReleaseArchiveAdapter.NormalizeAsync(
                source, Path.Combine(work, "output.zip"), "win-x64", "1.2.3",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    [Trait("category", "hosted")]
    public async Task PublishedClientPackMatchesPublicationAndNormalizesForExistingUpdaters()
    {
        var fixtureDirectory = Environment.GetEnvironmentVariable("NETRATEL_RELEASE_FIXTURE_DIR")
            ?? throw new InvalidOperationException("NETRATEL_RELEASE_FIXTURE_DIR must contain a completed public release fixture.");
        using var publication = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, "publication.json")));
        var root = publication.RootElement;
        Assert.Equal("complete", root.GetProperty("verification").GetProperty("state").GetString());
        Assert.Equal("BostonTechnologies/netratel", root.GetProperty("inputReceipt").GetProperty("repository").GetString());
        var version = root.GetProperty("productVersion").GetString()!;
        var commit = root.GetProperty("publicCommit").GetString()!;
        Assert.Equal(commit, root.GetProperty("inputReceipt").GetProperty("headSha").GetString());
        var inventory = root.GetProperty("inputReceipt").GetProperty("files");
        var checksums = (await File.ReadAllLinesAsync(Path.Combine(fixtureDirectory, "SHA256SUMS")))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[1].TrimStart('*'), parts => parts[0], StringComparer.Ordinal);
        var pattern = new Regex($"^netratel-client-{Regex.Escape(version)}-(?<rid>[a-z0-9-]+)\\.(?:zip|tar\\.gz)$",
            RegexOptions.CultureInvariant);
        var assets = inventory.EnumerateObject().Where(file => pattern.IsMatch(file.Name)).ToArray();
        Assert.NotEmpty(assets);
        var runtimes = new HashSet<string>(StringComparer.Ordinal);
        var work = NewWorkDirectory();
        try
        {
            foreach (var asset in assets)
            {
                var rid = pattern.Match(asset.Name).Groups["rid"].Value;
                Assert.True(runtimes.Add(rid), $"Duplicate client runtime {rid} in publication inventory.");
                var source = Path.Combine(fixtureDirectory, asset.Name);
                var sourceHash = await HashAsync(source);
                Assert.Equal(asset.Value.GetProperty("sha256").GetString(), sourceHash);
                Assert.Equal(root.GetProperty("artifacts").GetProperty(asset.Name).GetString(), sourceHash);
                Assert.Equal(checksums[asset.Name], sourceHash);
                var output = Path.Combine(work, $"{rid}.zip");
                var result = await ClientReleaseArchiveAdapter.NormalizeAsync(source, output, rid, version, commit,
                    TestContext.Current.CancellationToken);
                Assert.Equal(await HashAsync(output), result.Sha256);
                Assert.Equal(new FileInfo(output).Length, result.SizeBytes);
                await ClientArtifactManifestValidator.ValidateFileAsync(output, rid, version,
                    TestContext.Current.CancellationToken);
                using var zip = ZipFile.OpenRead(output);
                Assert.Contains(zip.Entries, entry => entry.FullName == "netratel-client-manifest.json");
                var executable = zip.Entries.Single(entry => entry.FullName ==
                    (rid.StartsWith("win-", StringComparison.Ordinal) ? "NetRatel.Client.exe" : "NetRatel.Client"));
                if (!rid.StartsWith("win-", StringComparison.Ordinal))
                    Assert.NotEqual(0, (executable.ExternalAttributes >> 16) & 0x49);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static string NewWorkDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netratel-client-adapter-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateZip(string path, string runtimeId, string version, string commit,
        params (string Name, string Content)[] files)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry($"netratel-client-{runtimeId}/netratel-client-manifest.json");
        using (var writer = new StreamWriter(manifest.Open()))
            writer.Write(JsonSerializer.Serialize(new
            {
                schema = "netratel.client.manifest.v1",
                product = "NetRatel.Client",
                version,
                runtimeId,
                commitSha = commit,
                executable = runtimeId.StartsWith("win-", StringComparison.Ordinal) ? "NetRatel.Client.exe" : "NetRatel.Client"
            }));
        foreach (var file in files)
        {
            using var content = new StreamWriter(archive.CreateEntry(file.Name).Open());
            content.Write(file.Content);
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var source = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(source)).ToLowerInvariant();
    }
}
