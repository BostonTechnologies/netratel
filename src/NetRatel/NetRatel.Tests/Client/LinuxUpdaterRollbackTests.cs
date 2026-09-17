using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class LinuxUpdaterRollbackTests
{
    [Fact]
    public async Task FailedReadiness_RestoresPreviousVersion_AndPersistsSuspension()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"netratel-updater-{Guid.NewGuid():N}");
        var state = Path.Combine(root, "state");
        var versions = Path.Combine(root, "versions");
        var previous = Path.Combine(versions, "0.4.111");
        var package = Path.Combine(root, "0.4.112-test.zip");
        var request = Path.Combine(state, "request.json");
        var ready = Path.Combine(state, "ready.json");
        var presence = Path.Combine(state, "presence.json");
        var fakeBin = Path.Combine(root, "bin");
        var current = Path.Combine(root, "current");

        Directory.CreateDirectory(previous);
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(fakeBin);
        await File.WriteAllTextAsync(Path.Combine(previous, "NetRatel.Client"), "previous");
        File.CreateSymbolicLink(current, previous);

        try
        {
            CreateArchive(package, "0.4.112-test");
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(package))).ToLowerInvariant();
            var requestPayload = new
            {
                version = "0.4.112-test",
                toVersion = "0.4.112-test",
                fromVersion = "0.4.111",
                packagePath = package,
                sha256,
                readyPath = ready,
                resultPath = Path.Combine(state, "result.json"),
                presencePath = presence,
                attemptId = "11111111-1111-1111-1111-111111111111",
                releaseId = "22222222-2222-2222-2222-222222222222",
                runtimeId = "linux-x64"
            };
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(requestPayload));

            var systemctl = Path.Combine(fakeBin, "systemctl");
            var serviceState = Path.Combine(root, "service-running");
            await File.WriteAllTextAsync(serviceState, "running");
            await File.WriteAllTextAsync(systemctl, "#!/usr/bin/env bash\ncase \"$1\" in\n  stop) rm -f \"$FAKE_SERVICE_STATE\" ;;\n  start) touch \"$FAKE_SERVICE_STATE\"; printf '{\"version\":\"0.4.111\"}' > \"$FAKE_PRESENCE_PATH\" ;;\n  is-active) test -f \"$FAKE_SERVICE_STATE\" ;;\nesac\n");
            File.SetUnixFileMode(systemctl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
            var updater = Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/tools/netratel-update.sh");
            var start = new ProcessStartInfo("bash", $"\"{updater}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            start.Environment["NetRatel_UPDATE_ROOT"] = root;
            start.Environment["NetRatel_UPDATE_STATE"] = state;
            start.Environment["NetRatel_UPDATE_REQUEST"] = request;
            start.Environment["NetRatel_UPDATE_ACTIVATION_TIMEOUT"] = "1";
            start.Environment["NetRatel_UPDATE_ROLLBACK_CHECKIN_TIMEOUT"] = "1";
            start.Environment["FAKE_PRESENCE_PATH"] = presence;
            start.Environment["FAKE_SERVICE_STATE"] = serviceState;
            start.Environment["PATH"] = $"{fakeBin}:/usr/bin:/bin";

            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(2);
            new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true)!.FullName.Should().Be(previous);
            Directory.EnumerateDirectories(Path.Combine(root, "failed"), "0.4.112-test-*").Should().ContainSingle();

            var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(state, "result.json")));
            result.RootElement.GetProperty("state").GetString().Should().Be("RolledBack");
            result.RootElement.GetProperty("fromVersion").GetString().Should().Be("0.4.111");
            result.RootElement.GetProperty("toVersion").GetString().Should().Be("0.4.112-test");
            File.Exists(Path.Combine(state, "suspension.json")).Should().BeTrue();
            File.GetUnixFileMode(Path.Combine(state, "suspension.json")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateArchive(string archivePath, string version)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry("netratel-client-manifest.json");
        using (var writer = new StreamWriter(manifest.Open()))
        {
            writer.Write(JsonSerializer.Serialize(new
            {
                schema = "netratel.client.manifest.v1",
                product = "NetRatel.Client",
                version,
                runtimeId = "linux-x64",
                commitSha = new string('a', 40),
                executable = "NetRatel.Client"
            }));
        }

        using var executable = new StreamWriter(archive.CreateEntry("NetRatel.Client").Open());
        executable.Write("test binary");
    }
}
