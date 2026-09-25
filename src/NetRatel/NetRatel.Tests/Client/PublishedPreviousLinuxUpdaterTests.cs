using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using NetRatel.API.Services;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class PublishedPreviousLinuxUpdaterTests
{
    [Fact]
    [Trait("category", "hosted")]
    public Task PublishedUpdaterVersionGateIsObservedBeforeRepair() => VerifyPublishedUpdaterAsync(repair: false);

    [Fact]
    [Trait("category", "hosted")]
    public Task PublishedUpdaterVersionGateAcceptsCandidateAfterVerifiedRepair() => VerifyPublishedUpdaterAsync(repair: true);

    private static async Task VerifyPublishedUpdaterAsync(bool repair)
    {
        if (!OperatingSystem.IsLinux()) return;

        var fixtureDirectory = Environment.GetEnvironmentVariable("NETRATEL_RELEASE_FIXTURE_DIR")
            ?? throw new InvalidOperationException("NETRATEL_RELEASE_FIXTURE_DIR must contain a completed public release fixture.");
        using var publication = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(fixtureDirectory, "publication.json"), TestContext.Current.CancellationToken));
        var previousVersion = publication.RootElement.GetProperty("productVersion").GetString()!;
        var candidateVersion = typeof(PublishedPreviousLinuxUpdaterTests).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        var candidateArchive = Environment.GetEnvironmentVariable("NETRATEL_CANDIDATE_CLIENT_ARCHIVE");
        if (repair && candidateArchive is null)
            throw new InvalidOperationException("NETRATEL_CANDIDATE_CLIENT_ARCHIVE must contain the candidate package.");
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
            await ExtractArchiveFileAsync(archivePath, installedUpdater, "/updater/netratel-update.sh");
            File.SetUnixFileMode(installedUpdater, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.CreateSymbolicLink(Path.Combine(root, "current"), previousTarget);
            var identityPath = Path.Combine(root, "agent.dat");
            var identity = Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(identityPath, identity, TestContext.Current.CancellationToken);

            var requestPath = Path.Combine(state, "request.json");
            var attemptId = Guid.NewGuid();
            var releaseId = Guid.NewGuid();
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
            {
                fromVersion = previousVersion,
                toVersion = candidateVersion,
                packagePath = Path.Combine(root, "not-yet-downloaded.zip"),
                sha256 = new string('a', 64),
                attemptId,
                releaseId,
                runtimeId = "linux-x64",
                readyPath = Path.Combine(state, "ready.json")
            }), TestContext.Current.CancellationToken);

            var beforeRepair = await RunUpdaterAsync(installedUpdater, root, state, requestPath);
            Assert.True(beforeRepair.FailureCode == "invalid_version" ||
                beforeRepair.Message.Contains("Staged package was not found", StringComparison.Ordinal),
                $"Published updater failed for an unexpected reason: {beforeRepair.Message}");
            Console.WriteLine($"Published {previousVersion} updater rejected candidate {candidateVersion}: " +
                (beforeRepair.FailureCode == "invalid_version"));
            if (!repair) return;
            var verifiedCandidateArchive = candidateArchive!;

            var repairScript = Path.Combine(root, "repair-linux-updater.py");
            await ExtractArchiveFileAsync(verifiedCandidateArchive, repairScript, "/updater/repair-linux-updater.py");
            await using var candidateStream = File.OpenRead(verifiedCandidateArchive);
            var expectedSha = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(
                candidateStream, TestContext.Current.CancellationToken)).ToLowerInvariant();
            var originalUpdater = await File.ReadAllBytesAsync(installedUpdater, TestContext.Current.CancellationToken);
            await RunRepairAsync(repairScript, verifiedCandidateArchive, new string('0', 64), installedUpdater,
                Path.Combine(state, "update.lock"), expectedExitCode: 1);
            Assert.Equal(originalUpdater, await File.ReadAllBytesAsync(installedUpdater, TestContext.Current.CancellationToken));
            await RunRepairAsync(repairScript, verifiedCandidateArchive, expectedSha, installedUpdater,
                Path.Combine(state, "update.lock"), expectedExitCode: 0);
            var repairedUpdater = await File.ReadAllBytesAsync(installedUpdater, TestContext.Current.CancellationToken);
            var expectedUpdater = Path.Combine(root, "expected-candidate-updater.sh");
            await ExtractArchiveFileAsync(verifiedCandidateArchive, expectedUpdater, "/updater/netratel-update.sh");
            Assert.Equal(await File.ReadAllBytesAsync(expectedUpdater, TestContext.Current.CancellationToken), repairedUpdater);
            await RunRepairAsync(repairScript, verifiedCandidateArchive, expectedSha, installedUpdater,
                Path.Combine(state, "update.lock"), expectedExitCode: 0);
            Assert.Equal(repairedUpdater, await File.ReadAllBytesAsync(installedUpdater, TestContext.Current.CancellationToken));
            Assert.Equal(originalUpdater.AsSpan().SequenceEqual(repairedUpdater) ? 0 : 1,
                Directory.GetFiles(updaterDirectory, "*.before-repair.*").Length);

            var afterRepair = await RunUpdaterAsync(installedUpdater, root, state, requestPath);
            Assert.Equal(string.Empty, afterRepair.FailureCode);
            Assert.Contains("Staged package was not found", afterRepair.Message);
            Assert.Equal(previousTarget, new FileInfo(Path.Combine(root, "current")).ResolveLinkTarget(true)!.FullName);
            Assert.Equal(identity, await File.ReadAllTextAsync(identityPath, TestContext.Current.CancellationToken));
            Console.WriteLine($"Verified repair passed the {candidateVersion} version gate.");
            await ActivateCandidateArchiveAsync(verifiedCandidateArchive, root, state, requestPath,
                installedUpdater, previousVersion, candidateVersion, attemptId, releaseId);
            Assert.Equal(identity, await File.ReadAllTextAsync(identityPath, TestContext.Current.CancellationToken));
            Console.WriteLine($"Candidate {candidateVersion} Linux archive activated from published {previousVersion} state.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunRepairAsync(string repairScript, string archive, string sha256,
        string installedUpdater, string lockPath, int expectedExitCode)
    {
        var start = new ProcessStartInfo("python3")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { repairScript, "--archive", archive, "--sha256", sha256,
                     "--updater", installedUpdater, "--lock", lockPath })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(expectedExitCode, process.ExitCode);
    }

    private static async Task ActivateCandidateArchiveAsync(
        string candidateArchive, string root, string state, string requestPath, string installedUpdater,
        string previousVersion, string candidateVersion, Guid attemptId, Guid releaseId)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();

        var manifestPath = Path.Combine(root, "candidate-manifest.json");
        await ExtractArchiveFileAsync(candidateArchive, manifestPath, "/netratel-client-manifest.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            manifestPath, TestContext.Current.CancellationToken));
        Assert.Equal("NetRatel.Client", manifest.RootElement.GetProperty("product").GetString());
        Assert.Equal("linux-x64", manifest.RootElement.GetProperty("runtimeId").GetString());
        Assert.Equal(candidateVersion, manifest.RootElement.GetProperty("version").GetString());
        var commit = manifest.RootElement.GetProperty("commitSha").GetString()!;
        var packagePath = Path.Combine(root, "candidate.zip");
        var package = await ClientReleaseArchiveAdapter.NormalizeAsync(candidateArchive, packagePath,
            "linux-x64", candidateVersion, commit, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
        {
            fromVersion = previousVersion,
            toVersion = candidateVersion,
            packagePath,
            sha256 = package.Sha256,
            attemptId,
            releaseId,
            runtimeId = "linux-x64",
            readyPath = Path.Combine(state, "ready.json"),
            presencePath = Path.Combine(state, "presence.json")
        }), TestContext.Current.CancellationToken);

        var fakeBin = Path.Combine(root, "bin");
        Directory.CreateDirectory(fakeBin);
        var fakeSystemctl = Path.Combine(fakeBin, "systemctl");
        await File.WriteAllTextAsync(fakeSystemctl, """
            #!/usr/bin/env bash
            case "$1" in
              stop) rm -f "$FAKE_SERVICE_STATE" ;;
              start)
                touch "$FAKE_SERVICE_STATE"
                python3 - "$FAKE_REQUEST" "$FAKE_READY" <<'PY'
            import json, sys
            with open(sys.argv[1], encoding='utf-8') as source: request = json.load(source)
            with open(sys.argv[2], 'w', encoding='utf-8') as destination:
                json.dump({'schema': 'netratel.update.ready.v2', 'attemptId': request['attemptId'],
                    'releaseId': request['releaseId'], 'version': request['toVersion'],
                    'confirmationId': '11111111-1111-1111-1111-111111111111'}, destination)
            PY
                ;;
              is-active) test -f "$FAKE_SERVICE_STATE" ;;
            esac
            """, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(fakeSystemctl,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = await RunUpdaterAsync(installedUpdater, root, state, requestPath, 0, fakeBin);
        Assert.Equal("Accepted", result.State);
        Assert.Equal(candidateVersion,
            new FileInfo(Path.Combine(root, "current")).ResolveLinkTarget(true)!.Name);
        Assert.True(File.Exists(Path.Combine(root, "versions", candidateVersion, "NetRatel.Client")));
        Assert.True((File.GetUnixFileMode(Path.Combine(root, "versions", candidateVersion)) &
            UnixFileMode.OtherExecute) != 0, "a nonroot Client service must be able to traverse the activated package");
    }

    private static async Task ExtractArchiveFileAsync(string archivePath, string destination, string entrySuffix)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = Assert.Single(archive.Entries, item =>
                item.FullName.EndsWith(entrySuffix, StringComparison.Ordinal));
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
            if (!entryInTar.Name.EndsWith(entrySuffix, StringComparison.Ordinal)) continue;
            Assert.False(found, "Client archive contains the requested entry more than once.");
            Assert.Equal(TarEntryType.RegularFile, entryInTar.EntryType);
            Assert.InRange(entryInTar.Length, 1, 256 * 1024);
            await using var output = File.Create(destination);
            await entryInTar.DataStream!.CopyToAsync(output, TestContext.Current.CancellationToken);
            found = true;
        }
        Assert.True(found, "Client archive is missing the requested entry.");
    }

    private static async Task<(string FailureCode, string Message, string State)> RunUpdaterAsync(
        string updater, string root, string state, string request, int expectedExitCode = 1, string? fakeBin = null)
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
        if (fakeBin is not null)
        {
            start.Environment["PATH"] = $"{fakeBin}:/usr/bin:/bin";
            start.Environment["FAKE_SERVICE_STATE"] = Path.Combine(state, "service-running");
            start.Environment["FAKE_REQUEST"] = request;
            start.Environment["FAKE_READY"] = Path.Combine(state, "ready.json");
        }
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(expectedExitCode, process.ExitCode);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(state, "result.json"), timeout.Token));
        return (result.RootElement.GetProperty("failureCode").GetString()!,
            result.RootElement.GetProperty("message").GetString()!,
            result.RootElement.GetProperty("state").GetString()!);
    }
}
