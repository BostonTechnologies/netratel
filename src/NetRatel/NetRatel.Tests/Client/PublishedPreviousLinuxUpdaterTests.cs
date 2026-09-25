using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class PublishedPreviousLinuxUpdaterTests
{
    [Fact]
    [Trait("category", "hosted")]
    public async Task PublishedUpdaterVersionGateAcceptsCandidateAfterAtomicScriptReplacement()
    {
        if (!OperatingSystem.IsLinux()) return;

        var fixtureDirectory = Environment.GetEnvironmentVariable("NETRATEL_RELEASE_FIXTURE_DIR")
            ?? throw new InvalidOperationException("NETRATEL_RELEASE_FIXTURE_DIR must contain a completed public release fixture.");
        using var publication = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(fixtureDirectory, "publication.json"), TestContext.Current.CancellationToken));
        var previousVersion = publication.RootElement.GetProperty("productVersion").GetString()!;
        var candidateVersion = typeof(PublishedPreviousLinuxUpdaterTests).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        var archiveBase = $"netratel-client-{previousVersion}-linux-x64";
        var archives = new[] { $"{archiveBase}.tar.gz", $"{archiveBase}.zip" }
            .Select(name => Path.Combine(fixtureDirectory, name)).Where(File.Exists).ToArray();
        var archivePath = Assert.Single(archives);

        var root = Path.Combine(Path.GetTempPath(), $"netratel-published-updater-{Guid.NewGuid():N}");
        var state = Path.Combine(root, "state");
        var updaterDirectory = Path.Combine(root, "updater");
        var previousTarget = Path.Combine(root, "versions", previousVersion);
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(updaterDirectory);
        Directory.CreateDirectory(previousTarget);
        try
        {
            var installedUpdater = Path.Combine(updaterDirectory, "netratel-update.sh");
            await ExtractUpdaterAsync(archivePath, installedUpdater);
            File.SetUnixFileMode(installedUpdater, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.CreateSymbolicLink(Path.Combine(root, "current"), previousTarget);
            var identityPath = Path.Combine(root, "agent.dat");
            var identity = Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(identityPath, identity, TestContext.Current.CancellationToken);

            var requestPath = Path.Combine(state, "request.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
            {
                fromVersion = previousVersion,
                toVersion = candidateVersion,
                packagePath = Path.Combine(root, "not-yet-downloaded.zip"),
                sha256 = new string('a', 64),
                attemptId = Guid.NewGuid(),
                releaseId = Guid.NewGuid(),
                runtimeId = "linux-x64",
                readyPath = Path.Combine(state, "ready.json")
            }), TestContext.Current.CancellationToken);

            var beforeRepair = await RunUpdaterAsync(installedUpdater, root, state, requestPath);
            Assert.True(beforeRepair.FailureCode == "invalid_version" ||
                beforeRepair.Message.Contains("Staged package was not found", StringComparison.Ordinal),
                $"Published updater failed for an unexpected reason: {beforeRepair.Message}");

            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
            var candidateUpdater = Path.Combine(repositoryRoot,
                "src/NetRatel/NetRatel.Client/tools/netratel-update.sh");
            var stagedUpdater = Path.Combine(updaterDirectory, ".netratel-update.sh.replacement");
            File.Copy(candidateUpdater, stagedUpdater);
            File.SetUnixFileMode(stagedUpdater, File.GetUnixFileMode(installedUpdater));
            File.Move(stagedUpdater, installedUpdater, overwrite: true);

            var afterRepair = await RunUpdaterAsync(installedUpdater, root, state, requestPath);
            Assert.Equal(string.Empty, afterRepair.FailureCode);
            Assert.Contains("Staged package was not found", afterRepair.Message);
            Assert.Equal(previousTarget, new FileInfo(Path.Combine(root, "current")).ResolveLinkTarget(true)!.FullName);
            Assert.Equal(identity, await File.ReadAllTextAsync(identityPath, TestContext.Current.CancellationToken));
            Console.WriteLine($"Published {previousVersion} updater rejected candidate {candidateVersion}: " +
                (beforeRepair.FailureCode == "invalid_version") + "; replacement passed the version gate.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ExtractUpdaterAsync(string archivePath, string destination)
    {
        const string updaterSuffix = "/updater/netratel-update.sh";
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = Assert.Single(archive.Entries, item =>
                item.FullName.EndsWith(updaterSuffix, StringComparison.Ordinal));
            Assert.InRange(entry.Length, 1, 256 * 1024);
            await using var input = entry.Open();
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, TestContext.Current.CancellationToken);
            return;
        }

        using var source = File.OpenRead(archivePath);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entryInTar;
        var found = false;
        while ((entryInTar = tar.GetNextEntry()) is not null)
        {
            if (!entryInTar.Name.EndsWith(updaterSuffix, StringComparison.Ordinal)) continue;
            Assert.False(found, "Published archive contains more than one Linux updater.");
            Assert.Equal(TarEntryType.RegularFile, entryInTar.EntryType);
            Assert.InRange(entryInTar.Length, 1, 256 * 1024);
            await using var output = File.Create(destination);
            await entryInTar.DataStream!.CopyToAsync(output, TestContext.Current.CancellationToken);
            found = true;
        }
        Assert.True(found, "Published archive is missing its Linux updater.");
    }

    private static async Task<(string FailureCode, string Message)> RunUpdaterAsync(
        string updater, string root, string state, string request)
    {
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(updater);
        start.Environment["NetRatel_UPDATE_ROOT"] = root;
        start.Environment["NetRatel_UPDATE_STATE"] = state;
        start.Environment["NetRatel_UPDATE_REQUEST"] = request;
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(1, process.ExitCode);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(state, "result.json"), timeout.Token));
        return (result.RootElement.GetProperty("failureCode").GetString()!,
            result.RootElement.GetProperty("message").GetString()!);
    }
}
