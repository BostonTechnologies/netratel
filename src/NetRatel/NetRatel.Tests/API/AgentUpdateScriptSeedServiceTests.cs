using FluentAssertions;
using System.IO.Compression;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using NetRatel.API.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentUpdateScriptSeedServiceTests
{
    [Fact]
    public void Seeded_Windows_Update_Uses_The_Verified_Installer_And_A_Detached_Service_Handoff()
    {
        var windows = GetSeedScript("WindowsScript");
        windows.Should().Contain("X-NetRatel-Tenant-Id");
        windows.Should().Contain("X-NetRatel-Enrollment-Code");
        windows.Should().Contain("onboarding-download");
        windows.Should().Contain("Get-NetRatelSha256Hex");
        windows.Should().Contain("Expand-NetRatelZip");
        windows.Should().Contain("netratel.client.manifest.v1");
        windows.Should().Contain("FileShare]::None");
        windows.Should().Contain("Move-Item -LiteralPath $versionTargetDir -Destination $versionBackupDir");
        windows.Should().Contain("The replacement service did not stop; its executable files were left untouched.");
        windows.Should().Contain("Get-CimInstance Win32_Service -Filter \"Name='$serviceName'\" -ErrorAction Stop");
        windows.Should().Contain("Start-Service -Name $serviceName -ErrorAction Stop");
        windows.Should().Contain("netratel.install-readiness.ready.v1");
        windows.Should().Contain("heartbeat_ready");
        windows.Should().Contain("connectionId");
        windows.Should().Contain("[int]$candidate.tenantId -ne $TenantId");
        windows.Should().Contain("Set-NetRatelSeedHandoffResult -State 'heartbeat_ready' -ReadyRecord $readyRecord");
        windows.Should().Contain("Set-NetRatelSeedHandoffResult -State \"handed_off\"");
        windows.Should().Contain("Set-NetRatelSeedHandoffResult -State 'processing'");
        windows.Should().Contain("$script:NetRatelSeedHandoffPreflightFailureCode = 'handoff_rejected'");
        windows.Should().Contain("$rawNetRatelSeedHandoffRequestPath = $NetRatelSeedHandoffRequestPath");
        windows.Should().Contain("$script:NetRatelSeedHandoffRequestPath = $ownedHandoffRequestPath");
        windows.Should().Contain("# Validate detached child input before the installer performs service or path preflight.");
        windows.Should().Contain("if (-not [string]::IsNullOrWhiteSpace($rawNetRatelSeedHandoffRequestPath)) {\n    Start-NetRatelSeedHandoff\n}");
        windows.Should().Contain("$failureCode = if ($script:NetRatelSeedHandoffPreflightFailureCode)");
        windows.Should().Contain("-FailureCode 'installer_failed' -ExceptionType $installerFailure.Exception.GetType().Name");
        windows.Should().Contain("-FailureCode 'installer_failed' -ExceptionType $_.Exception.GetType().Name");
        windows.Should().Contain("Service readiness will be reported after a fresh SYSTEM gateway heartbeat acknowledgment.");
        windows.Should().Contain("Get-CimInstance Win32_Process -Filter \"ProcessId=$ProcessId\" -ErrorAction Stop");
        windows.Should().Contain("Wait-NetRatelSeedOriginExit $sourceProcessId");
        windows.Should().Contain("WaitForExit(30000)");
        windows.Should().Contain("expiresAtUtc");
        windows.Should().Contain("$handoffCreatedAtUtc = [DateTimeOffset]::UtcNow");
        windows.Should().Contain("createdAtUtc = $handoffCreatedAtUtc.ToString(\"O\")");
        windows.Should().Contain("expiresAtUtc = $handoffCreatedAtUtc.AddMinutes(2).ToString(\"O\")");
        windows.Should().NotContain("expiresAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(2).ToString(\"O\")");
        windows.Should().Contain("-NetRatelSeedHandoffStateDirectory `\"$stateDirectory`\"");
        windows.Should().Contain("RandomNumberGenerator]::Create()");
        windows.Should().Contain("Assert-NetRatelTrustedReadinessPath $handoffDirectory $false $false $true $false $allowLegacyStateAncestors");
        windows.Should().Contain("New-NetRatelProtectedDirectory $handoffDirectory (Get-NetRatelTrustedStateSids) $allowLegacyStateAncestors");
        windows.Should().Contain("$allowLegacyStateAncestors = [bool]$script:NetRatelStateAncestorAllowance");
        windows.Should().Contain("function Assert-NetRatelSeedHandoffNoReparse");
        windows.Should().Contain("Assert-NetRatelSeedHandoffNoReparse $requestPath $true");
        windows.Should().Contain("Installer handoff state directory does not match the configured updater state path.");
        windows.Should().NotContain("icacls.exe $handoffDirectory");
        var mainBodyStart = windows.IndexOf("$tempDir = Join-Path $env:TEMP", StringComparison.Ordinal);
        var statePathInitialization = windows.IndexOf("Initialize-NetRatelProtectedStateDirectory", mainBodyStart, StringComparison.Ordinal);
        var installPathPreflight = windows.IndexOf("Initialize-NetRatelProtectedInstallDirectories -PreflightOnly", mainBodyStart, StringComparison.Ordinal);
        var lockAcquisition = windows.IndexOf("$updateLockPath = Join-Path $StateDir", mainBodyStart, StringComparison.Ordinal);
        var postLockNormalization = windows.IndexOf("Protect-NetRatelOwnedStateTree -path $StateDir -LockHeld", mainBodyStart, StringComparison.Ordinal);
        var handoffStart = windows.IndexOf("Start-NetRatelSeedHandoff", mainBodyStart, StringComparison.Ordinal);
        var artifactDownload = windows.IndexOf("$script:InstallerPhase = 'artifact-download-and-verification'", mainBodyStart, StringComparison.Ordinal);
        statePathInitialization.Should().BeGreaterThanOrEqualTo(mainBodyStart);
        installPathPreflight.Should().BeGreaterThan(statePathInitialization);
        lockAcquisition.Should().BeGreaterThan(installPathPreflight);
        postLockNormalization.Should().BeGreaterThan(lockAcquisition);
        handoffStart.Should().BeGreaterThan(postLockNormalization);
        artifactDownload.Should().BeGreaterThan(handoffStart);
        windows.Should().Contain("elseif ($null -ne $updateLock -and $script:NetRatelSeedHandoffResultPath)");
        windows.Should().Contain("if ($script:NetRatelSeedHandoffMode -and $null -ne $updateLock)");
        var requestPathResolution = windows.IndexOf("$requestPath = [System.IO.Path]::GetFullPath($rawNetRatelSeedHandoffRequestPath)", StringComparison.Ordinal);
        var requestPathGuard = windows.IndexOf("Assert-NetRatelSeedHandoffNoReparse $requestPath $true", requestPathResolution, StringComparison.Ordinal);
        var resultPathCapture = windows.IndexOf("$script:NetRatelSeedHandoffResultPath = Join-Path $expectedHandoffDirectory", requestPathGuard, StringComparison.Ordinal);
        var requestRead = windows.IndexOf("$handoff = Get-Content -LiteralPath $ownedHandoffRequestPath", resultPathCapture, StringComparison.Ordinal);
        requestPathResolution.Should().BeGreaterThanOrEqualTo(0);
        requestPathGuard.Should().BeGreaterThan(requestPathResolution);
        resultPathCapture.Should().BeGreaterThan(requestPathGuard);
        requestRead.Should().BeGreaterThan(resultPathCapture);
        var handoffPreambleStart = windows.IndexOf("$ownedHandoffRequestPath = $null", StringComparison.Ordinal);
        var handoffPreamble = windows[handoffPreambleStart..mainBodyStart];
        handoffPreamble.Should().Contain("$script:NetRatelSeedHandoffPreflightFailureCode = 'handoff_rejected'");
        handoffPreamble.Should().NotContain("Set-NetRatelSeedHandoffResult -State 'processing'");
        handoffPreamble.Should().Contain("Set-NetRatelSeedHandoffResult -State 'failed' -FailureCode 'handoff_rejected'");
        handoffPreamble.Should().Contain("Remove-Item -LiteralPath $ownedHandoffRequestPath, $ownedHandoffScriptPath");
        handoffPreamble.Should().Contain("Only these validated, uniquely named handoff files may be completed before the updater lock.");
        windows.IndexOf("elseif ($null -ne $updateLock -and $script:NetRatelSeedHandoffResultPath)", StringComparison.Ordinal)
            .Should().BeGreaterThan(postLockNormalization);
        windows.Should().Contain("-NetRatelSeedHandoffRequestPath");
        windows.Should().Contain("$EnrollmentCode = [string]$EnrollmentCode");
        windows.Should().Contain("enrollmentCode = $EnrollmentCode");
        windows.Should().NotContain("NETRATEL_SEED_ENROLLMENT_CODE_PLACEHOLDER");
        windows.Should().NotContain("--auth-check");
        windows.Should().NotContain("--enroll");
        windows.Should().NotContain("New-Service -Name \"NetRatel.Update\"");
        windows.Should().NotContain("sc.exe delete \"NetRatel.Client\"");

        var argumentAssignment = windows.IndexOf("$arguments =", StringComparison.Ordinal);
        argumentAssignment.Should().BeGreaterThanOrEqualTo(0);
        var argumentEnd = windows.IndexOf("$worker = Start-Process", argumentAssignment, StringComparison.Ordinal);
        windows[argumentAssignment..argumentEnd].Should().NotContain("$EnrollmentCode");
    }

    [Fact]
    [Trait("category", "hosted")]
    public async Task Seeded_Windows_HandoffPreambleRejectsMalformedRequestBeforeInstallerPreflight()
    {
        var seededContent = GetSeedScript("WindowsScript");
        var manifestEnd = seededContent.IndexOf("#| END", StringComparison.Ordinal);
        Assert.True(manifestEnd >= 0, "the seeded PowerShell script must contain its manifest boundary");
        var scriptContent = seededContent[(manifestEnd + "#| END".Length)..].TrimStart('\r', '\n');
        var root = Path.Combine(Path.GetTempPath(), $"netratel-seed-preamble-{Guid.NewGuid():N}");
        var stateDirectory = Path.Combine(root, "update-state");
        var handoffDirectory = Path.Combine(stateDirectory, "install-handoffs");
        Directory.CreateDirectory(handoffDirectory);
        try
        {
            const string handoffId = "0123456789abcdef0123456789abcdef";
            var requestPath = Path.Combine(handoffDirectory, $"handoff-{handoffId}.json");
            var scriptPath = Path.Combine(handoffDirectory, $"handoff-{handoffId}.ps1");
            var resultPath = Path.Combine(handoffDirectory, $"handoff-{handoffId}.result.json");
            var sentinelPath = Path.Combine(root, "unrelated.txt");
            await File.WriteAllTextAsync(requestPath, "{ malformed request");
            await File.WriteAllTextAsync(scriptPath, scriptContent);
            await File.WriteAllTextAsync(sentinelPath, "leave this file untouched");

            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(scriptPath);
            start.ArgumentList.Add("-TenantId");
            start.ArgumentList.Add("4098");
            start.ArgumentList.Add("-NetRatelSeedHandoffRequestPath");
            start.ArgumentList.Add(requestPath);
            start.ArgumentList.Add("-NetRatelSeedHandoffStateDirectory");
            start.ArgumentList.Add(stateDirectory);

            var result = await RunBoundedProcessAsync(start);
            result.ExitCode.Should().NotBe(0);
            result.StandardOutput.Length.Should().BeLessThan(4096);
            result.StandardError.Length.Should().BeLessThan(4096);
            var safeOutput = result.StandardOutput.Replace(root, "<fixture>", StringComparison.OrdinalIgnoreCase);
            safeOutput.Should().NotContain("Starting NetRatel Client deployment",
                "the invalid detached handoff must be rejected before the installer body starts");
            result.StandardError.Should().NotContain("Get-CimInstance", "the preamble must reject before Windows-only service preflight");
            result.StandardError.Should().NotContain("ApiBase must be an absolute", "the detached handoff must be rejected before public seed parameter validation");
            var safeError = result.StandardError.Replace(root, "<fixture>", StringComparison.OrdinalIgnoreCase);
            File.Exists(requestPath).Should().BeFalse(
                $"a validated handoff request is consumed when it is rejected; exit={result.ExitCode}; output={safeOutput}; error={safeError}");
            File.Exists(scriptPath).Should().BeFalse("only the exact handoff script may be cleaned up");
            File.Exists(resultPath).Should().BeTrue("the handed-off parent needs a terminal result even before the updater lock");
            (await File.ReadAllTextAsync(sentinelPath)).Should().Be("leave this file untouched");

            using var resultDocument = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            var resultRoot = resultDocument.RootElement;
            resultRoot.GetProperty("handoffId").GetString().Should().Be(handoffId);
            resultRoot.GetProperty("state").GetString().Should().Be("failed");
            resultRoot.GetProperty("failureCode").GetString().Should().Be("handoff_rejected");
            resultRoot.GetProperty("exceptionType").GetString().Should().MatchRegex("^[A-Za-z0-9]{1,64}$");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Seeded_Linux_Update_UsesSharedVerifiedInstallerAndProtectedDetachedHandoff()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The seeded Linux update handoff integration test requires a Linux host.");

        var script = GetSeedScript("LinuxScript");
        script.Should().Contain("/onboarding-download");
        script.Should().NotContain("/latest");
        script.Should().Contain("Artifact ZIP preflight failed: ");
        script.Should().Contain("archive = zipfile.ZipFile(archive_path, \"r\")");
        script.Should().Contain("unsafe ZIP path component");
        script.Should().Contain("archive.extractall(stage_dir)");
        var zipPreflight = script.IndexOf("archive = zipfile.ZipFile(archive_path, \"r\")", StringComparison.Ordinal);
        zipPreflight.Should().BeGreaterThanOrEqualTo(0);
        var extraction = script.IndexOf("archive.extractall(stage_dir)", zipPreflight, StringComparison.Ordinal);
        extraction.Should().BeGreaterThan(zipPreflight);
        script.Should().Contain("NETRATEL_SEED_GATEWAY_ENDPOINT");
        script.Should().Contain("NETRATEL_SEED_EXPECTED_SERVICE_PID");
        script.Should().Contain("NETRATEL_SEED_EXPECTED_SERVICE_START_TICKS");
        script.Should().Contain("NETRATEL_SEED_EXPECTED_UNIT_SHA256");
        script.Should().Contain("systemd-run");
        script.Should().NotContain("BASH_SOURCE");
        AssertNoRetiredLinuxSelectors(script);

        var fixture = await CreateLinuxSeedFixtureAsync(script);
        try
        {
            var result = await RunSeedScriptAsync(fixture, "https://api.example.invalid/api", "https://gateway.example.invalid");
            result.ExitCode.Should().Be(0, result.StandardError);
            result.StandardOutput.Should().Contain("detached root worker");

            var handoffDirectory = Path.Combine(fixture.StateDirectory, "install-handoffs");
            File.GetUnixFileMode(handoffDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var requestPath = Directory.EnumerateFiles(handoffDirectory, "handoff-*.json").Should().ContainSingle().Which;
            (File.GetUnixFileMode(requestPath) & (UnixFileMode)0x1FF).Should().Be((UnixFileMode)0x180);
            using var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath));
            var requestRoot = request.RootElement;
            var handoffId = requestRoot.GetProperty("handoff_id").GetString()!;
            handoffId.Should().MatchRegex("^[0-9a-f]{32}$");
            requestRoot.GetProperty("runtime").GetString().Should().Be("linux-x64");
            requestRoot.GetProperty("version").GetString().Should().Be("0.4.131-rc.10");
            requestRoot.GetProperty("gateway_endpoint").GetString().Should().Be("https://gateway.example.invalid");
            requestRoot.GetProperty("api_base").GetString().Should().Be("https://api.example.invalid");
            requestRoot.GetProperty("tenant_id").GetInt32().Should().Be(4098);
            requestRoot.GetProperty("enrollment_code").GetString().Should().Be(fixture.EnrollmentCode);
            requestRoot.GetProperty("origin_pid").GetInt32().Should().BeGreaterThan(1);
            requestRoot.GetProperty("origin_start_ticks").GetInt64().Should().BeGreaterThan(1);
            requestRoot.GetProperty("service_pid").GetInt32().Should().Be(Environment.ProcessId);
            requestRoot.GetProperty("service_start_ticks").GetInt64().Should().BeGreaterThan(1);
            requestRoot.GetProperty("unit_sha256").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
            requestRoot.GetProperty("installer_sha256").GetString().Should().MatchRegex("^[0-9a-f]{64}$");

            var installerPath = Path.Combine(handoffDirectory, $"handoff-{handoffId}.sh");
            var workerPath = Path.Combine(handoffDirectory, $"handoff-{handoffId}.py");
            foreach (var privateFile in new[] { requestPath, installerPath, workerPath })
                (File.GetUnixFileMode(privateFile) & (UnixFileMode)0x1FF).Should().Be((UnixFileMode)0x180);

            var launcherArguments = await File.ReadAllTextAsync(fixture.SystemdRunArgumentsPath);
            launcherArguments.Should().Contain(workerPath);
            launcherArguments.Should().Contain(requestPath);
            launcherArguments.Should().NotContain(fixture.EnrollmentCode);
            result.StandardOutput.Should().NotContain(fixture.EnrollmentCode);
            result.StandardError.Should().NotContain(fixture.EnrollmentCode);
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Seeded_Linux_WorkerRejectsChangedServiceProcessIdentity()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The seeded Linux update handoff integration test requires a Linux host.");

        var fixture = await CreateLinuxSeedFixtureAsync(GetSeedScript("LinuxScript"));
        try
        {
            var handoff = await RunSeedScriptAsync(fixture, "https://api.example.invalid", string.Empty);
            handoff.ExitCode.Should().Be(0, handoff.StandardError);
            var handoffDirectory = Path.Combine(fixture.StateDirectory, "install-handoffs");
            var requestPath = Directory.EnumerateFiles(handoffDirectory, "handoff-*.json").Single();
            using var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath));
            var handoffId = request.RootElement.GetProperty("handoff_id").GetString()!;
            var workerPath = Path.Combine(handoffDirectory, $"handoff-{handoffId}.py");
            var start = CreateSystemdProcessStartInfo(fixture);
            start.FileName = "/usr/bin/python3";
            start.ArgumentList.Add(workerPath);
            start.ArgumentList.Add(requestPath);
            start.Environment["FAKE_SYSTEMD_PID"] = "2147483647";
            var worker = await RunBoundedProcessAsync(start);

            worker.ExitCode.Should().Be(70, worker.StandardError);
            worker.StandardError.Should().Contain("Linux update handoff failed: RuntimeError");
            worker.StandardError.Should().NotContain(fixture.EnrollmentCode);
            worker.StandardOutput.Should().NotContain(fixture.EnrollmentCode);
            File.Exists(requestPath).Should().BeFalse();
            File.Exists(workerPath).Should().BeFalse();
            File.Exists(Path.Combine(handoffDirectory, $"handoff-{handoffId}.sh")).Should().BeFalse();
            new DirectoryInfo(Path.Combine(fixture.RootDirectory, "current"))
                .ResolveLinkTarget(true)!.Name.Should().Be("0.4.131-rc.1");
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Seeded_Linux_WorkerRunsVerifiedSameVersionInstallerAfterOriginExits()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The seeded Linux update handoff integration test requires a Linux host.");

        var fixture = await CreateLinuxSeedFixtureAsync(GetSeedScript("LinuxScript"));
        try
        {
            var handoff = await RunSeedScriptAsync(
                fixture,
                "https://api.example.invalid/api",
                string.Empty,
                version: "0.4.131-rc.1");
            handoff.ExitCode.Should().Be(0, handoff.StandardError);

            var handoffDirectory = Path.Combine(fixture.StateDirectory, "install-handoffs");
            var requestPath = Directory.EnumerateFiles(handoffDirectory, "handoff-*.json").Single();
            using (var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath)))
            {
                var handoffId = request.RootElement.GetProperty("handoff_id").GetString()!;
                var workerPath = Path.Combine(handoffDirectory, $"handoff-{handoffId}.py");
                var start = CreateSystemdProcessStartInfo(fixture);
                start.FileName = "/usr/bin/python3";
                start.ArgumentList.Add(workerPath);
                start.ArgumentList.Add(requestPath);
                start.Environment["FAKE_ARCHIVE"] = fixture.ArchivePath;
                start.Environment["FAKE_SHA"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.ArchivePath))).ToLowerInvariant();
                start.Environment["FAKE_SIZE"] = new FileInfo(fixture.ArchivePath).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                start.Environment["FAKE_ENROLL_ARGUMENTS_PATH"] = fixture.EnrollArgumentsPath;
                var worker = await RunBoundedProcessAsync(start);

                worker.ExitCode.Should().Be(0, worker.StandardError);
                worker.StandardOutput.Should().Contain("NetRatel Linux client installed");
                worker.StandardOutput.Should().NotContain(fixture.EnrollmentCode);
                worker.StandardError.Should().NotContain(fixture.EnrollmentCode);
                start.ArgumentList.ToArray().Should().NotContain(fixture.EnrollmentCode);

                var installerArguments = await File.ReadAllTextAsync(fixture.EnrollArgumentsPath);
                installerArguments.Should().Contain(fixture.EnrollmentCode);
                installerArguments.Should().Contain("--enroll");
                installerArguments.Should().Contain("https://api.example.invalid");

                var installedVersion = Path.Combine(fixture.RootDirectory, "versions", "0.4.131-rc.1");
                new DirectoryInfo(Path.Combine(fixture.RootDirectory, "current"))
                    .ResolveLinkTarget(true)!.Name.Should().Be("0.4.131-rc.1");
                using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(installedVersion, "clientsettings.json")));
                var clientSettings = settings.RootElement.GetProperty("Client");
                clientSettings.GetProperty("AgentId").GetString().Should().Be("installed-agent-identity");
                clientSettings.GetProperty("TerminalGracefulExitTimeoutMs").GetInt32().Should().Be(3210);
                settings.RootElement.GetProperty("Gateway").GetProperty("Endpoint").GetString()
                    .Should().Be("https://gateway.example.invalid");
                File.Exists(fixture.SystemdStatePath).Should().BeTrue("the normal installer starts the service after replacement");
                File.Exists(requestPath).Should().BeFalse("the detached worker removes its private request after processing");
            }
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    private sealed record LinuxSeedFixture(
        string Root,
        string BinDirectory,
        string ScriptPath,
        string RootDirectory,
        string StateDirectory,
        string UnitPath,
        string SystemdStatePath,
        string ArchivePath,
        string EnrollArgumentsPath,
        string SystemdRunArgumentsPath,
        string EnrollmentCode);

    [SupportedOSPlatform("linux")]
    private static async Task<LinuxSeedFixture> CreateLinuxSeedFixtureAsync(string script)
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-seeded-handoff-{Guid.NewGuid():N}");
        try
        {
            return await CreateLinuxSeedFixtureCoreAsync(root, script);
        }
        catch
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            throw;
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<LinuxSeedFixture> CreateLinuxSeedFixtureCoreAsync(string root, string script)
    {
        var bin = Path.Combine(root, "bin");
        var clientRoot = Path.Combine(root, "client");
        var stateDirectory = Path.Combine(root, "state");
        var unitDirectory = Path.Combine(root, "systemd");
        var scriptPath = Path.Combine(root, "seed.sh");
        var argsPath = Path.Combine(root, "systemd-run-args.txt");
        var statePath = Path.Combine(root, "systemd-active");
        var archivePath = Path.Combine(root, "artifact.zip");
        var enrollArgumentsPath = Path.Combine(root, "enroll-arguments.txt");
        const string enrollmentCode = "ENR-DO-NOT-LOG-9C70";

        Directory.CreateDirectory(root);
        SetUnixDirectoryMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(clientRoot, "versions", "0.4.131-rc.1"));
        Directory.CreateDirectory(stateDirectory);
        Directory.CreateDirectory(unitDirectory);
        await CreateLinuxArtifactAsync(archivePath, "0.4.131-rc.1");
        var publicDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        SetUnixDirectoryMode(clientRoot, publicDirectoryMode);
        SetUnixDirectoryMode(Path.Combine(clientRoot, "versions"), publicDirectoryMode);
        SetUnixDirectoryMode(Path.Combine(clientRoot, "versions", "0.4.131-rc.1"), publicDirectoryMode);
        SetUnixDirectoryMode(stateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SetUnixDirectoryMode(unitDirectory, publicDirectoryMode);

        var installedVersion = Path.Combine(clientRoot, "versions", "0.4.131-rc.1");
        await File.WriteAllTextAsync(Path.Combine(installedVersion, "netratel-client-manifest.json"), JsonSerializer.Serialize(new
        {
            schema = "netratel.client.manifest.v1",
            product = "NetRatel.Client",
            version = "0.4.131-rc.1",
            runtimeId = "linux-x64",
            executable = "NetRatel.Client",
            commitSha = new string('b', 40)
        }));
        File.SetUnixFileMode(Path.Combine(installedVersion, "netratel-client-manifest.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var executable = Path.Combine(installedVersion, "NetRatel.Client");
        await File.WriteAllTextAsync(executable, "#!/usr/bin/env bash\nexit 0\n");
        SetUnixExecutable(executable);
        await File.WriteAllTextAsync(Path.Combine(installedVersion, "clientsettings.json"), """
            {
              "Client": { "ApiBaseUrl": "https://old-api.example.invalid", "AgentId": "installed-agent-identity", "TerminalGracefulExitTimeoutMs": 3210 },
              "Gateway": { "Endpoint": "https://gateway.example.invalid" },
              "Transport": { "Mode": "AkkaPresence" }
            }
            """);
        File.SetUnixFileMode(Path.Combine(installedVersion, "clientsettings.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.CreateSymbolicLink(Path.Combine(clientRoot, "current"), installedVersion);
        var launcher = Path.Combine(clientRoot, "netratel-client-start.sh");
        await File.WriteAllTextAsync(launcher, $"#!/usr/bin/env bash\nROOT_DIR=\"{clientRoot}\"\nexec \"{clientRoot}/current/NetRatel.Client\" --service\n");
        SetUnixExecutable(launcher);
        var unitPath = Path.Combine(unitDirectory, "netratel-client.service");
        await File.WriteAllTextAsync(unitPath, $$"""
[Unit]
Description=NetRatel Client Test Unit
[Service]
WorkingDirectory={{clientRoot}}/current
ExecStart={{launcher}}
Environment=NetRatelCLIENT__Client__AutoUpdate__StateDirectory={{stateDirectory}}
Environment=NetRatelCLIENT__Gateway__Endpoint=https://gateway.example.invalid
""");
        File.SetUnixFileMode(unitPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        await File.WriteAllTextAsync(scriptPath, GetExecutableSeedBody(script));
        SetUnixExecutable(scriptPath);

        await WriteUnixExecutableAsync(Path.Combine(bin, "systemctl"), """
            #!/usr/bin/env bash
            case "$1" in
              show)
                case "$3" in
                  FragmentPath) printf '%s\n' "$FAKE_SYSTEMD_FRAGMENT" ;;
                  DropInPaths) printf '\n' ;;
                  MainPID) if [ -e "$FAKE_SYSTEMD_STATE" ]; then printf '%s\n' "$FAKE_SYSTEMD_PID"; else printf '0\n'; fi ;;
                  LoadState) printf 'loaded\n' ;;
                  *) exit 0 ;;
                esac ;;
              is-active) [ -e "$FAKE_SYSTEMD_STATE" ] && exit 0 || exit 3 ;;
              is-enabled) exit 0 ;;
              stop) rm -f "$FAKE_SYSTEMD_STATE" ;;
              start) touch "$FAKE_SYSTEMD_STATE" ;;
              cat) exit 1 ;;
              *) exit 0 ;;
            esac
            """);
        await WriteUnixExecutableAsync(Path.Combine(bin, "systemd-run"), """
            #!/usr/bin/env bash
            printf '%s\n' "$@" > "$FAKE_SYSTEMD_RUN_ARGS_PATH"
            """);
        await WriteUnixExecutableAsync(Path.Combine(bin, "curl"), """
            #!/usr/bin/env bash
            headers=""
            out=""
            while [ "$#" -gt 0 ]; do
              case "$1" in
                -D) headers="$2"; shift 2 ;;
                -o) out="$2"; shift 2 ;;
                -H) shift 2 ;;
                *) shift ;;
              esac
            done
            cp "$FAKE_ARCHIVE" "$out"
            printf 'HTTP/1.1 200 OK\r\nX-NetRatel-Artifact-Rid: linux-x64\r\nX-NetRatel-Artifact-Version: 0.4.131-rc.1\r\nX-NetRatel-Artifact-Sha256: %s\r\nX-NetRatel-Artifact-Size: %s\r\n\r\n' "$FAKE_SHA" "$FAKE_SIZE" > "$headers"
            """);
        await File.WriteAllTextAsync(statePath, "active");
        File.SetUnixFileMode(statePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new LinuxSeedFixture(root, bin, scriptPath, clientRoot, stateDirectory, unitPath, statePath, archivePath, enrollArgumentsPath, argsPath, enrollmentCode);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunSeedScriptAsync(
        LinuxSeedFixture fixture,
        string apiBase,
        string gatewayEndpoint,
        string version = "0.4.131-rc.10")
    {
        var start = CreateSystemdProcessStartInfo(fixture);
        start.FileName = "/bin/bash";
        start.ArgumentList.Add(fixture.ScriptPath);
        start.Environment["ApiBase"] = apiBase;
        start.Environment["TenantId"] = "4098";
        start.Environment["EnrollmentCode"] = fixture.EnrollmentCode;
        start.Environment["Runtime"] = "linux-x64";
        start.Environment["Version"] = version;
        start.Environment["GatewayEndpoint"] = gatewayEndpoint;
        return await RunBoundedProcessAsync(start);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunBoundedProcessAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start the Linux installer fixture process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                await process.WaitForExitAsync();
                var timeoutError = await stderrTask;
                throw new TimeoutException($"The Linux installer fixture exceeded its 35-second deadline. {timeoutError}");
            }

            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKillProcessTree(process);
                await process.WaitForExitAsync();
            }

            await Task.WhenAll(stdoutTask, stderrTask);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            if (!process.HasExited) throw;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (!process.HasExited) throw;
        }
    }

    private static ProcessStartInfo CreateSystemdProcessStartInfo(LinuxSeedFixture fixture)
    {
        var start = new ProcessStartInfo("/bin/bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["PATH"] = $"{fixture.BinDirectory}:/usr/bin:/bin";
        start.Environment["NetRatel_TEST_ALLOW_NONROOT"] = "true";
        start.Environment["FAKE_SYSTEMD_FRAGMENT"] = fixture.UnitPath;
        start.Environment["FAKE_SYSTEMD_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["FAKE_SYSTEMD_STATE"] = Path.Combine(fixture.Root, "systemd-active");
        start.Environment["FAKE_SYSTEMD_RUN_ARGS_PATH"] = fixture.SystemdRunArgumentsPath;
        return start;
    }

    [SupportedOSPlatform("linux")]
    private static async Task CreateLinuxArtifactAsync(string path, string version)
    {
        await using (var file = File.Create(path))
        {
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);
            var manifest = JsonSerializer.Serialize(new
            {
                schema = "netratel.client.manifest.v1",
                product = "NetRatel.Client",
                version,
                runtimeId = "linux-x64",
                executable = "NetRatel.Client",
                commitSha = new string('c', 40)
            });
            await WriteZipEntryAsync(archive, "netratel-client-manifest.json", manifest);
            await WriteZipEntryAsync(archive, "NetRatel.Client", "#!/usr/bin/env bash\nprintf '%s\\n' \"$@\" > \"$FAKE_ENROLL_ARGUMENTS_PATH\"\n");
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task WriteZipEntryAsync(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(contents);
        await stream.WriteAsync(bytes);
    }

    private static string GetExecutableSeedBody(string script)
    {
        var marker = "\n#| END\n";
        var end = script.IndexOf(marker, StringComparison.Ordinal);
        end.Should().BeGreaterThanOrEqualTo(0);
        return script[(end + marker.Length)..];
    }

    private static void AssertNoRetiredLinuxSelectors(string script)
    {
        script.Should().NotContain("NetRatelAkkaMigration:Enabled=true");
        script.Should().NotContain("RequiredPresenceAuthority=akka");
        script.Should().NotContain("TelemetryShadowEnabled=true");
        script.Should().NotContain("TelemetryAuthorityEnabled=true");
        script.Should().NotContain("CommandAuthorityEnabled=true");
        script.Should().NotContain("JobAuthorityEnabled=true");
        script.Should().NotContain("TerminalAuthorityEnabled=true");
        script.Should().NotContain("RemoteSupportV2InventoryEnabled=true");
    }

    [SupportedOSPlatform("linux")]
    private static async Task WriteUnixExecutableAsync(string path, string contents)
    {
        await File.WriteAllTextAsync(path, contents);
        SetUnixExecutable(path);
    }

    [SupportedOSPlatform("linux")]
    private static void SetUnixExecutable(string path)
        => File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    [SupportedOSPlatform("linux")]
    private static void SetUnixDirectoryMode(string path, UnixFileMode mode)
        => File.SetUnixFileMode(path, mode);


    private static string GetSeedScript(string fieldName)
    {
        var seedField = typeof(AgentUpdateScriptSeedService).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        var seed = seedField!.GetValue(null)!;
        return (string)seed.GetType().GetProperty("Content", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(seed)!;
    }
}
