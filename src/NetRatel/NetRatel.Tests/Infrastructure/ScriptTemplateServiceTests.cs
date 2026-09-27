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
            var existingUnit = Path.Combine(root, "existing-netratel-client.service");
            await File.WriteAllTextAsync(existingUnit, """
[Unit]
Description=Previous NetRatel Client
[Service]
Environment=NetRatelCLIENT__Client__ApiBaseUrl=https://legacy-api.example.invalid
Environment=NetRatelCLIENT__Gateway__Endpoint=https://split-gateway.example.invalid
Environment=NetRatelCLIENT__Gateway__FileGatewayEnabled=false
Environment=NetRatelCLIENT__Gateway__ControlGatewayEnabled=true
Environment=NetRatelCLIENT__Transport__Mode=AkkaPresence
Environment=Custom__ServiceValue="kept value"
EnvironmentFile=-/etc/netratel-client.env
""");
            await File.WriteAllTextAsync(Path.Combine(bin, "systemctl"), "#!/usr/bin/env bash\ncase \"$1\" in show) printf '%s\\n' \"$FAKE_SYSTEMD_FRAGMENT\" ;; is-active) test -f \"$FAKE_SYSTEMD_STATE\" ;; stop) rm -f \"$FAKE_SYSTEMD_STATE\" ;; start) touch \"$FAKE_SYSTEMD_STATE\" ;; *) exit 0 ;; esac\n");
            foreach (var file in Directory.EnumerateFiles(bin))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var start = new ProcessStartInfo("bash", scriptPath) { RedirectStandardError = true, UseShellExecute = false };
            start.Environment["PATH"] = $"{bin}:/usr/bin:/bin";
            start.Environment["FAKE_ARCHIVE"] = artifact;
            start.Environment["FAKE_SYSTEMD_STATE"] = Path.Combine(root, "service-running");
            start.Environment["FAKE_SYSTEMD_FRAGMENT"] = existingUnit;
            start.Environment["NetRatel_ROOT"] = Path.Combine(root, "client");
            start.Environment["NetRatel_STATE"] = Path.Combine(root, "state");
            start.Environment["NetRatel_SYSTEMD_UNIT_DIR"] = Path.Combine(root, "systemd");
            start.Environment["NetRatel_TEST_ALLOW_NONROOT"] = "true";
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0, await process.StandardError.ReadToEndAsync());
            var current = Path.Combine(root, "client", "current");
            new FileInfo(current).ResolveLinkTarget(true)!.Name.Should().Be("0.4.131-rc.1");
            var rewrittenUnit = await File.ReadAllTextAsync(Path.Combine(root, "systemd", "netratel-client.service"));
            rewrittenUnit.Should().Contain("Environment=NetRatelCLIENT__Gateway__Endpoint=https://split-gateway.example.invalid");
            rewrittenUnit.Should().Contain("Environment=NetRatelCLIENT__Gateway__FileGatewayEnabled=false");
            rewrittenUnit.Should().Contain("Environment=Custom__ServiceValue=\"kept value\"");
            rewrittenUnit.Should().Contain("EnvironmentFile=-/etc/netratel-client.env");
            rewrittenUnit.Should().Contain("Environment=NetRatelCLIENT__Client__ApiBaseUrl=https://example.test");
            rewrittenUnit.Should().NotContain("Environment=NetRatelCLIENT__Client__ApiBaseUrl=https://legacy-api.example.invalid");
            rewrittenUnit.Should().NotContain("Environment=NetRatelCLIENT__Gateway__ControlGatewayEnabled=true");
            rewrittenUnit.Should().NotContain("Environment=NetRatelCLIENT__Transport__Mode=AkkaPresence");
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
        script.Should().Contain("NetRatelCLIENT__Client__ApiBaseUrl=$ApiBase");
        script.Should().Contain("NetRatel.Update");
        script.Should().Contain("versions");
        script.Should().Contain("netratel-update.ps1");
        script.Should().Contain("/onboarding-download");
        script.Should().Contain("X-NetRatel-Enrollment-Code");
        script.Should().Contain("NetRatel.Client service failed to start");
        script.Should().Contain("Get-Content -Path $latestLog.FullName -Tail 80");
        AssertNoRetiredClientDefaults(script);
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
        script.Should().Contain("$wrapperName = \"netratel-client-$Runtime\"");
        script.Should().Contain("$rootEntries.Count -eq 1");
        script.Should().Contain("$rootEntries[0].Name -ceq $wrapperName");
        script.Should().Contain("Wrapped Client package is missing its manifest or executable.");
        script.Should().Contain("Move-Item -LiteralPath $entry.FullName -Destination $targetDir -ErrorAction Stop");
        script.Should().Contain("Wrapped Client package contains conflicting root entries.");
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
        script.Should().Contain("--enroll $EnrollmentCode --api $ApiBase");
        AssertNoRetiredClientDefaults(script);
    }

    [Fact]
    public void Build_PowerShell_RejectsUnexpectedPackageLayouts_AndKeepsTheVersionRootExecutablePath()
    {
        var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
            4098, "win-x64", "ENR-ABC123", "https://netratel.example.invalid",
            DateTimeOffset.UtcNow.AddHours(1), false, true, "1.2.3", new string('a', 64)));

        script.Should().Contain("$hasFlatPackage = (Test-Path -LiteralPath $manifestPath -PathType Leaf)");
        script.Should().Contain("Client package has an unexpected mixed archive layout.");
        script.Should().Contain("Client package has an unexpected archive layout.");
        script.Should().Contain("$exe = Join-Path $targetDir \"NetRatel.Client.exe\"");
        script.Should().Contain("$actualSha -ne $ExpectedSha256.ToLowerInvariant()");
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
        script.Should().Contain("Environment=NetRatelCLIENT__Client__ApiBaseUrl=${API_BASE}");
        script.Should().Contain("NetRatelCLIENT__Client__AutoUpdate__StateDirectory=${STATE_DIR}");
        AssertNoRetiredClientDefaults(script);
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

    [Fact]
    public void Build_MacOS_UsesShellAndLaunchdForService()
    {
        var service = new ScriptTemplateService();
        var script = service.Build(new DeploymentScriptTemplateRequest(
            4098, "osx-arm64", "ENR-ABC123", "https://netratel.example.invalid",
            DateTimeOffset.UtcNow.AddHours(1), true, true, "0.4.131-rc.1", "abcdef"));

        service.GetFileExtension("osx-arm64").Should().Be("sh");
        script.Should().StartWith("#!/usr/bin/env bash");
        script.Should().Contain("shasum -a 256");
        script.Should().Contain("launchctl bootstrap system");
        var launchdApiEnvironmentKey = string.Concat(
            "<", "key>NetRatelCLIENT__Client__ApiBaseUrl</", "key>",
            "<string>${API_BASE}</string>");
        script.Should().Contain("environment[\"NetRatelCLIENT__Client__ApiBaseUrl\"] = api_base");
        script.Should().NotContain(launchdApiEnvironmentKey);
        AssertNoRetiredClientDefaults(script);
        script.Should().Contain("/Library/LaunchDaemons/");
        script.Should().Contain("--enroll");
        script.Should().NotContain("systemctl");
        script.Should().NotContain("New-Service");
        AssertBashSyntax(script);
    }

    [Fact]
    public void Build_LinuxWithoutService_DoesNotRequireSystemdOrRoot()
    {
        var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
            4098, "linux-x64", "ENR-ABC123", "https://netratel.example.invalid",
            DateTimeOffset.UtcNow.AddHours(1), false, true, "0.4.131-rc.1", "abcdef"));

        script.Should().Contain("${HOME}/.local/share/netratel/client");
        script.Should().Contain("--enroll");
        script.Should().NotContain("This NetRatel systemd installer must be run as root.");
        script.Should().NotContain("systemctl is-active");
        AssertNoRetiredClientDefaults(script);
        AssertBashSyntax(script);
    }

    [Fact]
    public async Task Build_MacOS_InstallsExactPackageWithoutServiceOnUnixHost()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), $"netratel-macos-installer-{Guid.NewGuid():N}");
        var bin = Path.Combine(root, "bin");
        var archivePath = Path.Combine(root, "client.zip");
        var scriptPath = Path.Combine(root, "install.sh");
        Directory.CreateDirectory(bin);
        try
        {
            CreateUnixArtifact(archivePath, "0.4.131-rc.1", "osx-arm64");
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(archivePath))).ToLowerInvariant();
            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                4098, "osx-arm64", "ENR-ABC123", "https://example.test",
                DateTimeOffset.UtcNow.AddHours(1), false, true, "0.4.131-rc.1", sha));
            AssertNoRetiredClientDefaults(script);
            await File.WriteAllTextAsync(scriptPath, script);
            var curl = Path.Combine(bin, "curl");
            await File.WriteAllTextAsync(curl,
                "#!/usr/bin/env bash\nwhile [ \"$#\" -gt 0 ]; do if [ \"$1\" = -o ]; then cp \"$FAKE_ARCHIVE\" \"$2\"; exit 0; fi; shift; done\nexit 1\n");
            File.SetUnixFileMode(curl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var start = new ProcessStartInfo("bash", scriptPath)
            {
                RedirectStandardError = true, UseShellExecute = false
            };
            start.Environment["PATH"] = $"{bin}:{Environment.GetEnvironmentVariable("PATH")}";
            start.Environment["FAKE_ARCHIVE"] = archivePath;
            start.Environment["NetRatel_ROOT"] = Path.Combine(root, "installed");
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, await process.StandardError.ReadToEndAsync());
            var installed = Path.Combine(root, "installed", "versions", "0.4.131-rc.1", "NetRatel.Client");
            File.Exists(installed).Should().BeTrue($"installer output was expected at {installed}; " +
                $"available files: {string.Join(", ", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Build_MacOS_Service_Preserves_SplitGateway_And_False_OptOut()
    {
        if (!OperatingSystem.IsLinux()) return;

        var root = Path.Combine(Path.GetTempPath(), $"netratel-macos-service-installer-{Guid.NewGuid():N}");
        var bin = Path.Combine(root, "bin");
        var archivePath = Path.Combine(root, "client.zip");
        var scriptPath = Path.Combine(root, "install.sh");
        var plistPath = Path.Combine(root, "existing.plist");
        Directory.CreateDirectory(bin);
        try
        {
            CreateUnixArtifact(archivePath, "0.4.131-rc.1", "osx-arm64");
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(archivePath))).ToLowerInvariant();
            await File.WriteAllTextAsync(plistPath, """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict>
                  <key>Label</key><string>co.za.netratel.client</string>
                  <key>EnvironmentVariables</key><dict>
                    <key>NetRatelCLIENT__Client__ApiBaseUrl</key><string>https://old-api.example.invalid</string>
                    <key>NetRatelCLIENT__Gateway__Endpoint</key><string>https://split-gateway.example.invalid</string>
                    <key>NetRatelCLIENT__Gateway__FileGatewayEnabled</key><string>false</string>
                    <key>NetRatelCLIENT__Gateway__ControlGatewayEnabled</key><string>true</string>
                    <key>NetRatelCLIENT__Transport__Mode</key><string>AkkaPresence</string>
                    <key>Custom__ServiceValue</key><string>kept &amp; safe</string>
                  </dict>
                </dict></plist>
                """);

            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                4098, "osx-arm64", "ENR-ABC123", "https://example.test",
                DateTimeOffset.UtcNow.AddHours(1), true, true, "0.4.131-rc.1", sha));
            await File.WriteAllTextAsync(scriptPath, script);
            var curl = Path.Combine(bin, "curl");
            await File.WriteAllTextAsync(curl,
                "#!/usr/bin/env bash\nwhile [ \"$#\" -gt 0 ]; do if [ \"$1\" = -o ]; then cp \"$FAKE_ARCHIVE\" \"$2\"; exit 0; fi; shift; done\nexit 1\n");
            var launchctl = Path.Combine(bin, "launchctl");
            await File.WriteAllTextAsync(launchctl, "#!/usr/bin/env bash\nexit 0\n");
            foreach (var file in Directory.EnumerateFiles(bin))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var start = new ProcessStartInfo("bash", scriptPath)
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.Environment["PATH"] = $"{bin}:{Environment.GetEnvironmentVariable("PATH")}";
            start.Environment["FAKE_ARCHIVE"] = archivePath;
            start.Environment["NetRatel_ROOT"] = Path.Combine(root, "installed");
            start.Environment["NetRatel_LAUNCHD_PLIST"] = plistPath;
            start.Environment["NetRatel_TEST_ALLOW_NONROOT"] = "true";
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, await process.StandardError.ReadToEndAsync());

            var rewrittenPlist = await File.ReadAllTextAsync(plistPath);
            rewrittenPlist.Should().Contain("<key>NetRatelCLIENT__Gateway__Endpoint</key><string>https://split-gateway.example.invalid</string>");
            rewrittenPlist.Should().Contain("<key>NetRatelCLIENT__Gateway__FileGatewayEnabled</key><string>false</string>");
            rewrittenPlist.Should().Contain("<key>Custom__ServiceValue</key><string>kept &amp; safe</string>");
            rewrittenPlist.Should().Contain("<key>NetRatelCLIENT__Client__ApiBaseUrl</key><string>https://example.test</string>");
            rewrittenPlist.Should().NotContain("https://old-api.example.invalid");
            rewrittenPlist.Should().NotContain("<key>NetRatelCLIENT__Gateway__ControlGatewayEnabled</key>");
            rewrittenPlist.Should().NotContain("<key>NetRatelCLIENT__Transport__Mode</key>");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CreateLinuxArtifact(string path, string version)
        => CreateUnixArtifact(path, version, "linux-x64");

    private static void AssertNoRetiredClientDefaults(string script)
    {
        script.Should().NotContain("NetRatelCLIENT__Transport__Mode=AkkaPresence");
        script.Should().NotContain("NetRatelCLIENT__Gateway__Endpoint=$ApiBase");
        script.Should().NotContain("NetRatelCLIENT__Gateway__Endpoint=${API_BASE}");
        script.Should().NotContain("NetRatelCLIENT__Gateway__RequiredPresenceAuthority=akka");
        script.Should().NotContain("NetRatelCLIENT__Gateway__TelemetryShadowEnabled=true");
        script.Should().NotContain("NetRatelCLIENT__Gateway__TelemetryAuthorityEnabled=true");
        script.Should().NotContain("NetRatelCLIENT__Gateway__CommandAuthorityEnabled=true");
        script.Should().NotContain("NetRatelCLIENT__Gateway__JobAuthorityEnabled=true");
        script.Should().NotContain("NetRatelCLIENT__Gateway__TerminalAuthorityEnabled=true");
        script.Should().NotContain("NetRatelCLIENT__Gateway__ControlGatewayEnabled=true");
        script.Should().NotContain("NetRatelCLIENT__Gateway__FileGatewayEnabled=true");
        script.Should().NotContain("NetRatelCLIENT__Gateway__LogGatewayEnabled=true");
        script.Should().NotContain("NetRatelCLIENT__Gateway__RemoteSupportGatewayEnabled=true");
        script.Should().NotContain("NetRatelCLIENT__Gateway__TerminalGatewayEnabled=true");
        script.Should().NotContain("<key>NetRatelCLIENT__Gateway__Endpoint</key>");
    }

    private static void AssertBashSyntax(string script)
    {
        if (OperatingSystem.IsWindows()) return;
        var start = new ProcessStartInfo("bash") { RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
    }

    private static void CreateUnixArtifact(string path, string version, string runtimeId)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("netratel-client-manifest.json").Open()))
            writer.Write(JsonSerializer.Serialize(new { schema = "netratel.client.manifest.v1", product = "NetRatel.Client", version, runtimeId, executable = "NetRatel.Client" }));
        using (var writer = new StreamWriter(archive.CreateEntry("NetRatel.Client").Open()))
            writer.Write("#!/usr/bin/env bash\nexit 0\n");
        using var updater = new StreamWriter(archive.CreateEntry("updater/netratel-update.sh").Open());
        updater.Write("#!/usr/bin/env bash\nexit 0\n");
    }
}
