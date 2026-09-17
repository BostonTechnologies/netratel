using FluentAssertions;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using NetRatel.Application.Artifacts;
using NetRatel.Infrastructure.Artifacts;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ScriptTemplateServiceTests
{
    [Fact]
    public async Task Build_Bash_InstallsAnExactArtifactAtomically_AndStartsTheNewUnit()
    {
        if (!OperatingSystem.IsLinux()) return;

        var root = Path.Combine(Path.GetTempPath(), $"netratel-installer-{Guid.NewGuid():N}");
        var bin = Path.Combine(root, "bin");
        var artifact = Path.Combine(root, "artifact.zip");
        var scriptPath = Path.Combine(root, "install.sh");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(bin);
        try
        {
            CreateLinuxArtifact(artifact, "0.4.131-rc.1");
            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                4098, "linux-x64", "ENR-ABC123", "https://example.test", DateTimeOffset.UtcNow.AddHours(1), true, true,
                "0.4.131-rc.1", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(artifact))).ToLowerInvariant()));
            await File.WriteAllTextAsync(scriptPath, script);
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(Path.Combine(bin, "curl"), "#!/usr/bin/env bash\nwhile [ \"$#\" -gt 0 ]; do if [ \"$1\" = -o ]; then cp \"$FAKE_ARCHIVE\" \"$2\"; exit 0; fi; shift; done\nexit 1\n");
            await File.WriteAllTextAsync(Path.Combine(bin, "systemctl"), "#!/usr/bin/env bash\ncase \"$1\" in is-active) test -f \"$FAKE_SYSTEMD_STATE\" ;; stop) rm -f \"$FAKE_SYSTEMD_STATE\" ;; start) touch \"$FAKE_SYSTEMD_STATE\" ;; *) exit 0 ;; esac\n");
            foreach (var file in Directory.EnumerateFiles(bin))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var start = new ProcessStartInfo("bash", scriptPath) { RedirectStandardError = true, UseShellExecute = false };
            start.Environment["PATH"] = $"{bin}:/usr/bin:/bin";
            start.Environment["FAKE_ARCHIVE"] = artifact;
            start.Environment["FAKE_SYSTEMD_STATE"] = Path.Combine(root, "service-running");
            start.Environment["NetRatel_ROOT"] = Path.Combine(root, "client");
            start.Environment["NetRatel_STATE"] = Path.Combine(root, "state");
            start.Environment["NetRatel_SYSTEMD_UNIT_DIR"] = Path.Combine(root, "systemd");
            start.Environment["NetRatel_TEST_ALLOW_NONROOT"] = "true";
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0, await process.StandardError.ReadToEndAsync());
            var current = Path.Combine(root, "client", "current");
            new FileInfo(current).ResolveLinkTarget(true)!.Name.Should().Be("0.4.131-rc.1");
            File.Exists(Path.Combine(root, "systemd", "netratel-client.service")).Should().BeTrue();
            File.Exists(Path.Combine(root, "service-running")).Should().BeTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Build_PowerShell_ReplacesValues_AndInjectsServiceBlock()
    {
        var service = new ScriptTemplateService();
        var script = service.Build(new DeploymentScriptTemplateRequest(
            TenantId: 4098,
            RuntimeId: "win-x64",
            EnrollmentCode: "ENR-ABC123",
            ApiBaseUrl: "https://netratel.example.invalid",
            ValidToUtc: DateTimeOffset.UtcNow.AddHours(1),
            InstallAsService: true,
            SilentInstall: true));

        script.Should().Contain("ENR-ABC123");
        script.Should().Contain("https://netratel.example.invalid");
        script.Should().Contain("netratel.enroll.json");
        script.Should().Contain("ConvertTo-Json -Depth 4");
        script.Should().Contain("New-Service");
        script.Should().Contain("-BinaryPathName \"`\"$exe`\" --service\"");
        script.Should().Contain("NetRatel.Update");
        script.Should().Contain("versions");
        script.Should().Contain("netratel-update.ps1");
        script.Should().Contain("/onboarding-download");
        script.Should().Contain("X-NetRatel-Enrollment-Code");
        script.Should().Contain("NetRatel.Client service failed to start");
        script.Should().Contain("Get-Content -Path $latestLog.FullName -Tail 80");
        script.Should().Contain("Get-NetRatelSha256Hex");
        script.Should().Contain("Expand-NetRatelZip");
        script.Should().Contain("Get-Command Get-FileHash -ErrorAction SilentlyContinue");
        script.Should().Contain("[System.Security.Cryptography.SHA256]::Create()");
        script.Should().Contain("Get-Command Expand-Archive -ErrorAction SilentlyContinue");
        script.Should().Contain("[System.IO.Compression.ZipFile]::ExtractToDirectory");
        script.Should().Contain("[System.Net.ServicePointManager]::SecurityProtocol");
        script.Should().Contain("PowerShell version:");
        script.Should().Contain("powershell.exe -NoProfile -ExecutionPolicy Bypass -File");
        script.Should().Contain("$actualSha = Get-NetRatelSha256Hex -Path $zipPath");
        script.Should().Contain("Expand-NetRatelZip -ZipPath $zipPath -DestinationPath $targetDir");
        script.Should().NotContain("(Get-FileHash -Path $zipPath -Algorithm SHA256).Hash");
        script.Should().NotContain("Expand-Archive -Path $zipPath -DestinationPath $targetDir -Force");
        script.Should().NotContain("& $exe --enroll");
        script.Should().NotContain("{{");
        script.Should().NotContain("}}");
    }

    [Fact]
    public void Build_PowerShell_OmitsServiceBlock_WhenDisabled()
    {
        var service = new ScriptTemplateService();
        var script = service.Build(new DeploymentScriptTemplateRequest(
            TenantId: 4098,
            RuntimeId: "win-x64",
            EnrollmentCode: "ENR-ABC123",
            ApiBaseUrl: "https://netratel.example.invalid",
            ValidToUtc: DateTimeOffset.UtcNow.AddHours(1),
            InstallAsService: false,
            SilentInstall: true));

        script.Should().NotContain("New-Service");
        script.Should().Contain("--enroll");
    }

    [Fact]
    public void Build_Bash_Service_UsesOptNetRatelVersionedLayout_AndUpdaterService()
    {
        var service = new ScriptTemplateService();
        var script = service.Build(new DeploymentScriptTemplateRequest(
            TenantId: 4098,
            RuntimeId: "linux-x64",
            EnrollmentCode: "ENR-ABC123",
            ApiBaseUrl: "https://netratel.example.invalid",
            ValidToUtc: DateTimeOffset.UtcNow.AddHours(1),
            InstallAsService: true,
            SilentInstall: true));

        script.Should().Contain("/opt/netratel/client");
        script.Should().Contain("/var/lib/netratel/update");
        script.Should().Contain("netratel-update.service");
        script.Should().Contain("netratel-update.sh");
        script.Should().Contain("/onboarding-download");
        script.Should().Contain("X-NetRatel-Enrollment-Code");
        script.Should().Contain("NetRatelCLIENT__Client__AutoUpdate__Mode=Service");
        script.Should().Contain("NetRatelCLIENT__Transport__Mode=AkkaPresence");
        script.Should().Contain("NetRatelCLIENT__Gateway__Endpoint=${API_BASE}");
        script.Should().Contain("NetRatelCLIENT__Gateway__RequiredPresenceAuthority=akka");
        script.Should().Contain("NetRatelCLIENT__Gateway__TelemetryAuthorityEnabled=true");
        script.Should().Contain("NetRatelCLIENT__Gateway__CommandAuthorityEnabled=true");
        script.Should().Contain("NetRatelCLIENT__Gateway__JobAuthorityEnabled=true");
        script.Should().Contain("NetRatelCLIENT__Gateway__FileGatewayEnabled=true");
        script.Should().Contain("NetRatelCLIENT__Gateway__LogGatewayEnabled=true");
        script.Should().Contain("NetRatelCLIENT__Gateway__RemoteSupportGatewayEnabled=true");
        script.Should().Contain("NetRatelCLIENT__Gateway__TerminalGatewayEnabled=true");
        script.Should().Contain("NetRatelCLIENT__Gateway__TerminalAuthorityEnabled=true");
        script.Should().Contain("EUID");
        script.Should().Contain("curl unzip sha256sum systemctl");
        script.Should().Contain("\"${CLIENT_EXE}\" --enroll \"${ENROLLMENT_CODE}\" --api \"${API_BASE}\"");
        script.Should().NotContain("netratel.enroll.json");
        script.Should().NotContain("sudo");
        script.Should().Contain("netratel-client-start.sh");
        script.Should().Contain("NetRatel.Client");
        script.Should().Contain("RestartPreventExitStatus=78");
        script.Should().Contain("systemctl is-active --quiet netratel-client.service");
        script.Should().Contain("journalctl -u netratel-client.service -n 80 --no-pager");
        script.Should().Contain("flock -n 9");
        script.Should().Contain("netratel-client-manifest.json");
        script.Should().Contain("Immutable target version");
        script.Should().Contain("systemctl disable --now sto-client.service");
        script.Should().Contain("rollback_install");
        script.Should().Contain("install -m 0755");
    }

    private static void CreateLinuxArtifact(string path, string version)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("netratel-client-manifest.json").Open()))
            writer.Write(JsonSerializer.Serialize(new { schema = "netratel.client.manifest.v1", product = "NetRatel.Client", version, runtimeId = "linux-x64", executable = "NetRatel.Client" }));
        using (var writer = new StreamWriter(archive.CreateEntry("NetRatel.Client").Open()))
            writer.Write("#!/usr/bin/env bash\nexit 0\n");
        using var updater = new StreamWriter(archive.CreateEntry("updater/netratel-update.sh").Open());
        updater.Write("#!/usr/bin/env bash\nexit 0\n");
    }
}
