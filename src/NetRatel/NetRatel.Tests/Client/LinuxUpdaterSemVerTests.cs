using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class LinuxUpdaterSemVerTests
{
    [Fact]
    public async Task IncreasingPrereleaseInstallsVerifiedPackageAndAcceptsReadiness()
    {
        if (!OperatingSystem.IsLinux()) return;

        const string fromVersion = "1.2.3-rc.7";
        const string toVersion = "1.2.3-rc.8";
        var root = Path.Combine(Path.GetTempPath(), $"netratel-updater-activation-{Guid.NewGuid():N}");
        var state = Path.Combine(root, "state");
        var prior = Path.Combine(root, "versions", fromVersion);
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(prior);
        Directory.CreateDirectory(bin);
        try
        {
            File.CreateSymbolicLink(Path.Combine(root, "current"), prior);
            var packagePath = Path.Combine(root, "candidate.zip");
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("netratel-client-manifest.json").Open()))
                    writer.Write(JsonSerializer.Serialize(new
                    {
                        schema = "netratel.client.manifest.v1", product = "NetRatel.Client",
                        version = toVersion, runtimeId = "linux-x64", commitSha = new string('a', 40),
                        executable = "NetRatel.Client"
                    }));
                using var executable = new StreamWriter(archive.CreateEntry("NetRatel.Client").Open());
                executable.Write("fixture executable");
            }
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath))).ToLowerInvariant();
            var attemptId = Guid.NewGuid();
            var releaseId = Guid.NewGuid();
            var requestPath = Path.Combine(state, "request.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
            {
                fromVersion, toVersion, packagePath, sha256 = hash,
                attemptId, releaseId, runtimeId = "linux-x64",
                readyPath = Path.Combine(state, "ready.json"),
                presencePath = Path.Combine(state, "presence.json")
            }));
            var serviceState = Path.Combine(state, "service-running");
            var systemctl = Path.Combine(bin, "systemctl");
            await File.WriteAllTextAsync(systemctl, """
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
                """);
            File.SetUnixFileMode(systemctl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
            var updater = Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/tools/netratel-update.sh");
            var start = new ProcessStartInfo("bash")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(updater);
            start.Environment["PATH"] = $"{bin}:/usr/bin:/bin";
            start.Environment["NetRatel_UPDATE_ROOT"] = root;
            start.Environment["NetRatel_UPDATE_STATE"] = state;
            start.Environment["NetRatel_UPDATE_REQUEST"] = requestPath;
            start.Environment["FAKE_SERVICE_STATE"] = serviceState;
            start.Environment["FAKE_REQUEST"] = requestPath;
            start.Environment["FAKE_READY"] = Path.Combine(state, "ready.json");
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync(timeout.Token));
            Assert.Equal(toVersion, new FileInfo(Path.Combine(root, "current")).ResolveLinkTarget(true)!.Name);
            Assert.True(File.Exists(Path.Combine(root, "versions", toVersion, "NetRatel.Client")));
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(state, "result.json"), timeout.Token));
            Assert.Equal("Accepted", result.RootElement.GetProperty("state").GetString());
            Assert.False(File.Exists(Path.Combine(state, "suspension.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("1.2.3-rc.7", "1.2.3-rc.8", true)]
    [InlineData("1.2.3-rc.9", "1.2.3-rc.10", true)]
    [InlineData("1.2.3-rc.10", "1.2.3-rc.2", false)]
    [InlineData("1.2.3-1", "1.2.3-alpha", true)]
    [InlineData("1.2.3-alpha", "1.2.3-1", false)]
    [InlineData("1.2.3-rc.8", "1.2.3-rc.8.1", true)]
    [InlineData("1.2.3-rc.8", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.3-rc.9", false)]
    [InlineData("1.2.3", "1.2.3+new-build", false)]
    [InlineData("1.2.3-rc.8", "1.2.4-rc.1", true)]
    [InlineData("1.2.3-rc.8", "1.2.3-rc.08", false)]
    public async Task UpdateRequestUsesSemVerPrecedenceBeforeOpeningThePackage(
        string currentVersion, string candidateVersion, bool accepted)
    {
        if (!OperatingSystem.IsLinux()) return;

        var root = Path.Combine(Path.GetTempPath(), $"netratel-updater-version-{Guid.NewGuid():N}");
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);
        try
        {
            var requestPath = Path.Combine(state, "request.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
            {
                fromVersion = currentVersion,
                toVersion = candidateVersion,
                packagePath = Path.Combine(root, "missing-package.zip"),
                sha256 = new string('a', 64),
                attemptId = Guid.NewGuid(),
                releaseId = Guid.NewGuid(),
                runtimeId = "linux-x64",
                readyPath = Path.Combine(state, "ready.json")
            }));
            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
            var updater = Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/tools/netratel-update.sh");
            var start = new ProcessStartInfo("bash")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(updater);
            start.Environment["NetRatel_UPDATE_ROOT"] = root;
            start.Environment["NetRatel_UPDATE_STATE"] = state;
            start.Environment["NetRatel_UPDATE_REQUEST"] = requestPath;
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, process.ExitCode);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(state, "result.json"), timeout.Token));
            var message = result.RootElement.GetProperty("message").GetString();
            var failureCode = result.RootElement.GetProperty("failureCode").GetString();
            if (accepted)
            {
                Assert.Contains("Staged package was not found", message);
                Assert.True(string.IsNullOrEmpty(failureCode));
            }
            else
            {
                Assert.Contains("not newer than the running version", message);
                Assert.Equal("invalid_version", failureCode);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
