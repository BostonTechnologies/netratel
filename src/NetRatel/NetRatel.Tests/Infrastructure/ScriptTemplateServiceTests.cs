using FluentAssertions;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Xml.Linq;
using NetRatel.Application.Artifacts;
using NetRatel.Infrastructure.Artifacts;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ScriptTemplateServiceTests
{
    [Fact]
    public void Build_WindowsServiceReadinessTimeoutIsBoundedAndConfigurable()
    {
        var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
            12, "win-x64", "ENR-TEST", "https://api.example", DateTimeOffset.UtcNow.AddHours(1),
            InstallAsService: true, SilentInstall: true, ReadinessTimeoutSeconds: 17));

        script.Should().Contain("expiresAtUtc = $requestedAt.AddSeconds(17).ToString('O')");
        script.Should().Contain("$readinessDeadline = $requestedAt.AddSeconds(17)");

        var invalidRequest = new DeploymentScriptTemplateRequest(
            12, "win-x64", "ENR-TEST", "https://api.example", DateTimeOffset.UtcNow.AddHours(1),
            InstallAsService: true, SilentInstall: true, ReadinessTimeoutSeconds: 4);
        var build = () => new ScriptTemplateService().Build(invalidRequest);
        build.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(DeploymentScriptTemplateRequest.ReadinessTimeoutSeconds));
    }

    [Theory]
    [SupportedOSPlatform("linux")]
    [InlineData("https://legacy-api.example.invalid", "https://split-gateway.example.invalid", "https://legacy-api.example.invalid", "https://split-gateway.example.invalid", "https://split-gateway.example.invalid", "https://split-gateway.example.invalid")]
    [InlineData("https://legacy-api.example.invalid/api/", "https://legacy-api.example.invalid", "https://legacy-api.example.invalid/api/", "https://legacy-api.example.invalid", "", "")]
    [InlineData("https://file-api.example.invalid", "https://file-api.example.invalid", "https://service-api.example.invalid", "", "", "")]
    [InlineData("https://file-api.example.invalid", "https://split-gateway.example.invalid", "https://service-api.example.invalid", "", "", "https://split-gateway.example.invalid")]
    public async Task Build_Bash_InstallsAnExactArtifactAtomically_AndStartsTheNewUnit(
        string previousApiBase,
        string previousGatewayEndpoint,
        string previousServiceApiBase,
        string previousServiceGatewayEndpoint,
        string expectedServiceGatewayEndpoint,
        string expectedSettingsGatewayEndpoint)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The Linux systemd installer integration test requires a Linux host.");

        var root = Path.Combine(Path.GetTempPath(), $"netratel-installer-{Guid.NewGuid():N}");
        var bin = Path.Combine(root, "bin");
        var artifact = Path.Combine(root, "artifact.zip");
        var scriptPath = Path.Combine(root, "install.sh");
        Directory.CreateDirectory(root);
        try
        {
            const UnixFileMode privateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            const UnixFileMode publicDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            const UnixFileMode publicFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

            File.SetUnixFileMode(root, privateDirectoryMode);
            Directory.CreateDirectory(bin);
            File.SetUnixFileMode(bin, privateDirectoryMode);
            CreateLinuxArtifact(artifact, "0.4.131-rc.1");
            File.SetUnixFileMode(artifact, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var artifactSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(artifact))).ToLowerInvariant();
            var artifactSize = new FileInfo(artifact).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var clientRoot = Path.Combine(root, "client");
            var previousVersion = Path.Combine(clientRoot, "versions", "0.4.131-rc.1");
            Directory.CreateDirectory(previousVersion);
            File.SetUnixFileMode(clientRoot, publicDirectoryMode);
            File.SetUnixFileMode(Path.Combine(clientRoot, "versions"), publicDirectoryMode);
            File.SetUnixFileMode(previousVersion, publicDirectoryMode);
            var manifestPath = Path.Combine(previousVersion, "netratel-client-manifest.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
            {
                schema = "netratel.client.manifest.v1",
                product = "NetRatel.Client",
                version = "0.4.131-rc.1",
                runtimeId = "linux-x64",
                executable = "NetRatel.Client",
                commitSha = new string('a', 40)
            }));
            File.SetUnixFileMode(manifestPath, publicFileMode);
            var previousExecutable = Path.Combine(previousVersion, "NetRatel.Client");
            await File.WriteAllTextAsync(previousExecutable, "#!/usr/bin/env bash\nexit 0\n");
            SetUnixExecutable(previousExecutable);
            var previousSettingsPath = Path.Combine(previousVersion, "clientsettings.json");
            await File.WriteAllTextAsync(previousSettingsPath, $$"""
                {
                  "Client": { "ApiBaseUrl": "{{previousApiBase}}", "TerminalGracefulExitTimeoutMs": 3210 },
                  "Gateway": {
                    "Endpoint": "{{previousGatewayEndpoint}}",
                    "FileGatewayEnabled": false,
                    "ControlGatewayEnabled": true
                  },
                  "Transport": { "Mode": "AkkaPresence" }
                }
                """);
            File.SetUnixFileMode(previousSettingsPath, publicFileMode);
            Directory.CreateSymbolicLink(Path.Combine(clientRoot, "current"), previousVersion);
            var launcherPath = Path.Combine(clientRoot, "netratel-client-start.sh");
            await File.WriteAllTextAsync(launcherPath, $"#!/usr/bin/env bash\nROOT_DIR=\"{clientRoot}\"\nexec \"{clientRoot}/current/NetRatel.Client\" --service\n");
            SetUnixExecutable(launcherPath);
            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                4098, "linux-x64", "ENR-ABC123", "https://example.test", DateTimeOffset.UtcNow.AddHours(1), true, true,
                "0.4.131-rc.1", artifactSha));
            await File.WriteAllTextAsync(scriptPath, script);
            SetUnixExecutable(scriptPath);
            await File.WriteAllTextAsync(Path.Combine(bin, "curl"), """
                #!/usr/bin/env bash
                headers=""
                out=""
                while [ "$#" -gt 0 ]; do case "$1" in -D) headers="$2"; shift 2;; -o) out="$2"; shift 2;; *) shift;; esac; done
                cp "$FAKE_ARCHIVE" "$out"
                printf 'HTTP/1.1 200 OK\r\nX-NetRatel-Artifact-Rid: linux-x64\r\nX-NetRatel-Artifact-Version: 0.4.131-rc.1\r\nX-NetRatel-Artifact-Sha256: %s\r\nX-NetRatel-Artifact-Size: %s\r\n\r\n' "$FAKE_SHA" "$FAKE_SIZE" > "$headers"
                """);
            var unitDirectory = Path.Combine(root, "systemd");
            Directory.CreateDirectory(unitDirectory);
            File.SetUnixFileMode(unitDirectory, publicDirectoryMode);
            var existingUnit = Path.Combine(unitDirectory, "netratel-client.service");
            var stateDirectory = Path.Combine(root, "state");
            var previousServiceGatewayLine = string.IsNullOrEmpty(previousServiceGatewayEndpoint)
                ? string.Empty
                : $"Environment=NetRatelCLIENT__Gateway__Endpoint={previousServiceGatewayEndpoint}{Environment.NewLine}";
            var optionalEnvironmentFile = Path.Combine(root, "optional", "netratel-client.env");
            await File.WriteAllTextAsync(existingUnit, $$"""
[Unit]
Description=Previous NetRatel Client
[Service]
WorkingDirectory={{clientRoot}}/current
ExecStart={{launcherPath}}
Environment=NetRatelCLIENT__Client__ApiBaseUrl={{previousServiceApiBase}}
Environment=NetRatelCLIENT__Client__AutoUpdate__StateDirectory={{stateDirectory}}
{{previousServiceGatewayLine}}Environment=NetRatelCLIENT__Gateway__FileGatewayEnabled=false
Environment=NetRatelCLIENT__Gateway__ControlGatewayEnabled=true
Environment=NetRatelCLIENT__Transport__Mode=AkkaPresence
Environment=Custom__ServiceValue="kept value"
EnvironmentFile=-{{optionalEnvironmentFile}}
""");
            File.SetUnixFileMode(existingUnit, publicFileMode);
            await File.WriteAllTextAsync(Path.Combine(bin, "systemctl"), """
                #!/usr/bin/env bash
                case "$1" in
                  show)
                    case "$3" in
                      FragmentPath) printf '%s\n' "$FAKE_SYSTEMD_FRAGMENT" ;;
                      DropInPaths) printf '\n' ;;
                      LoadState) printf 'loaded\n' ;;
                      MainPID) if [ -f "$FAKE_SYSTEMD_STATE" ]; then printf '%s\n' "$FAKE_SYSTEMD_PID"; else printf '0\n'; fi ;;
                      *) exit 0 ;;
                    esac ;;
                  is-active) if [ -f "$FAKE_SYSTEMD_STATE" ]; then exit 0; else exit 3; fi ;;
                  is-enabled) exit 0 ;;
                  stop) rm -f "$FAKE_SYSTEMD_STATE" ;;
                  start) touch "$FAKE_SYSTEMD_STATE" ;;
                  cat) exit 1 ;;
                  *) exit 0 ;;
                esac
                """);
            foreach (var file in Directory.EnumerateFiles(bin))
                SetUnixExecutable(file);

            var start = new ProcessStartInfo("bash", scriptPath) { RedirectStandardError = true, UseShellExecute = false };
            start.Environment["PATH"] = $"{bin}:/usr/bin:/bin";
            start.Environment["FAKE_ARCHIVE"] = artifact;
            start.Environment["FAKE_SHA"] = artifactSha;
            start.Environment["FAKE_SIZE"] = artifactSize;
            var fakeServiceState = Path.Combine(root, "service-running");
            await File.WriteAllTextAsync(fakeServiceState, "active");
            File.SetUnixFileMode(fakeServiceState, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            start.Environment["FAKE_SYSTEMD_STATE"] = fakeServiceState;
            start.Environment["FAKE_SYSTEMD_FRAGMENT"] = existingUnit;
            start.Environment["FAKE_SYSTEMD_PID"] = "424242";
            start.Environment["NetRatel_ROOT"] = clientRoot;
            start.Environment["NetRatel_STATE"] = stateDirectory;
            start.Environment["NetRatel_SYSTEMD_UNIT_DIR"] = unitDirectory;
            start.Environment["NetRatel_TEST_ALLOW_NONROOT"] = "true";
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0, await process.StandardError.ReadToEndAsync());
            var current = Path.Combine(root, "client", "current");
            new FileInfo(current).ResolveLinkTarget(true)!.Name.Should().Be("0.4.131-rc.1");
            var rewrittenUnit = await File.ReadAllTextAsync(Path.Combine(root, "systemd", "netratel-client.service"));
            if (string.IsNullOrEmpty(expectedServiceGatewayEndpoint))
                rewrittenUnit.Should().NotContain("Environment=NetRatelCLIENT__Gateway__Endpoint=");
            else
                rewrittenUnit.Should().Contain($"Environment=NetRatelCLIENT__Gateway__Endpoint={expectedServiceGatewayEndpoint}");
            rewrittenUnit.Should().NotContain("Environment=NetRatelCLIENT__Gateway__FileGatewayEnabled=false");
            rewrittenUnit.Should().Contain("Environment=Custom__ServiceValue=\"kept value\"");
            rewrittenUnit.Should().Contain($"EnvironmentFile=-{optionalEnvironmentFile}");
            rewrittenUnit.Should().Contain("Environment=NetRatelCLIENT__Client__ApiBaseUrl=https://example.test");
            rewrittenUnit.Should().NotContain("Environment=NetRatelCLIENT__Client__ApiBaseUrl=https://legacy-api.example.invalid");
            rewrittenUnit.Should().NotContain("Environment=NetRatelCLIENT__Gateway__ControlGatewayEnabled=true");
            rewrittenUnit.Should().NotContain("Environment=NetRatelCLIENT__Transport__Mode=AkkaPresence");
            File.Exists(Path.Combine(root, "service-running")).Should().BeTrue();

            using var installedSettings = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(root, "client", "versions", "0.4.131-rc.1", "clientsettings.json")));
            installedSettings.RootElement.GetProperty("Client").GetProperty("TerminalGracefulExitTimeoutMs").GetInt32().Should().Be(3210);
            var installedGateway = installedSettings.RootElement.GetProperty("Gateway");
            if (string.IsNullOrEmpty(expectedSettingsGatewayEndpoint))
                installedGateway.TryGetProperty("Endpoint", out _).Should().BeFalse();
            else
                installedGateway.GetProperty("Endpoint").GetString().Should().Be(expectedSettingsGatewayEndpoint);
            installedGateway.TryGetProperty("FileGatewayEnabled", out _).Should().BeFalse();
            installedGateway.TryGetProperty("ControlGatewayEnabled", out _).Should().BeFalse();
            installedSettings.RootElement.TryGetProperty("Transport", out _).Should().BeFalse();
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

        script.Should().NotContain("NetRatelSeedHandoff");
        script.Should().Contain("catch {\n    # NetRatel installer preflight failure-result extension point.\n    throw\n}");
        script.Should().Contain("ENR-ABC123");
        script.Should().Contain("https://netratel.example.invalid");
        script.Should().Contain("netratel.enroll.json");
        script.Should().Contain("ConvertTo-Json -Depth 4");
        script.Should().Contain("New-Service");
        script.Should().Contain("-BinaryPathName \"`\"$exe`\" --service\"");
        script.Should().Contain("NetRatelCLIENT__Client__ApiBaseUrl=$ApiBase");
        script.Should().Contain("NetRatel_UPDATE_ROOT=$RootDir");
        script.Should().Contain("NetRatel_UPDATE_STATE=$StateDir");
        script.Should().Contain("NetRatel_UPDATE_REQUEST=$autoUpdateRequestPath");
        script.Should().Contain("NetRatelCLIENT__Client__AutoUpdate__StateDirectory=$StateDir");
        script.Should().Contain("NetRatelCLIENT__Client__AutoUpdate__RequestPath=$autoUpdateRequestPath");
        script.Should().Contain("NetRatelCLIENT__Client__AutoUpdate__ReadyPath=$autoUpdateReadyPath");
        script.Should().Contain("update.lock");
        script.Should().Contain("netratel.install-readiness.request.v1");
        script.Should().Contain("netratel.install-readiness.ready.v1");
        script.Should().Contain("heartbeat_ready");
        script.Should().Contain("heartbeatSequence");
        script.Should().Contain("-not (Test-NetRatelHasTwoAcknowledgedHeartbeats $candidate) -or");
        script.Should().Contain("connectionEpoch");
        script.Should().Contain("connectionId");
        script.Should().Contain("S-1-5-18");
        script.Should().Contain("NT SERVICE', 'TrustedInstaller");
        script.Should().Contain("function Test-NetRatelSameOrAncestorPath");
        script.Should().Contain("function Test-NetRatelTrustedAdministratorSid");
        script.Should().Contain("function Test-NetRatelTrustedPartialFileAcl");
        script.Should().Contain("Test-NetRatelTrustedPartialFileAcl $lockItem.FullName $script:NetRatelStateAncestorAllowance 'updater-lock' $phase");
        script.Should().Contain("Test-NetRatelTrustedPartialFileAcl $handoffFile.FullName $script:NetRatelStateAncestorAllowance 'seed-handoff-file' $phase");
        script.Should().NotContain("Test-NetRatelProtectedAclMatches $lockItem.FullName");
        script.Should().Contain("($rootWasExplicit -or $ownsRegisteredRoot)");
        script.Should().Contain("$explicitRootAncestor = $rootWasExplicit -and (Test-NetRatelSameOrAncestorPath $canonicalPath $canonicalRoot)");
        script.Should().Contain("New-NetRatelProtectedDirectory $currentPath $trustedSids ($allowLegacyAdministrators -or $allowLegacyAdministratorAncestors)");
        script.Should().Contain("Assert-NetRatelTrustedReadinessPath $path $false $false $true $false $allowLegacyAdministratorsOnParent");
        script.Should().Contain("($isLeaf -and $allowLegacyAdministrators) -or");
        script.Should().Contain("(-not $isLeaf -and $allowLegacyAdministratorAncestors)");
        script.Should().Contain("$script:NetRatelStateAncestorAllowance = [bool]($stateWasExplicit -or ($existingService -and ($ownsConfiguredState -or $ownsDefaultState)))");
        script.Should().Contain("Assert-NetRatelTrustedReadinessPath $readinessDir $false $false $true $false $script:NetRatelStateAncestorAllowance");
        script.Should().Contain("Assert-NetRatelTrustedReadinessPath $readinessFile $true $false $true $false $script:NetRatelStateAncestorAllowance");
        script.Should().Contain("function Write-NetRatelProtectedReadinessRequest");
        script.Should().Contain("[System.IO.FileMode]::CreateNew");
        script.Should().Contain("[System.Security.AccessControl.FileSystemRights]::FullControl");
        script.Should().Contain("Assert-NetRatelTrustedReadinessPath $temporaryPath $true $false $true $false $allowLegacyAdministratorAncestors");
        script.Should().Contain("[System.IO.File]::Move($temporaryPath, $path)");
        script.Should().Contain("Assert-NetRatelTrustedReadinessPath $path $true $false $true $false $allowLegacyAdministratorAncestors");
        script.Should().Contain("Write-NetRatelProtectedReadinessRequest $requestPath $challenge (Get-NetRatelTrustedStateSids) $script:NetRatelStateAncestorAllowance");
        script.Should().NotContain("Set-Content -LiteralPath $requestPath");
        script.Should().Contain("Assert-NetRatelTrustedReadinessPath -path $canonicalState -leafFile:$false");
        script.Should().Contain("$acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])");
        script.Should().Contain("[System.IO.Directory]::CreateDirectory($path, $acl)");
        script.Should().Contain("Protect-NetRatelOwnedStateTree -path $canonicalState -ValidateOnly -phase 'state-preflight'");
        script.Should().Contain("Protect-NetRatelOwnedStateTree -path $StateDir -LockHeld -phase 'locked-state-tree-normalization'");
        script.Should().Contain("Protect-NetRatelOwnedStateTree -path $StateDir -ValidateOnly -LockHeld -RequireProtected -phase 'locked-state-tree-verification'");
        script.Should().Contain("-allowLegacyAdministratorAncestors:$script:NetRatelStateAncestorAllowance `");
        script.Should().Contain("-pathRole $itemRole");
        script.Should().Contain("$currentItem = Get-Item -LiteralPath $item.FullName -Force -ErrorAction Stop");
        script.Should().Contain("reason=object-type-changed");
        script.Should().Contain("reason=path-reparse-point");
        script.Should().Contain("reason=path-outside-tree");
        script.Should().Contain("Assert-NetRatelTrustedReadinessPath -path $currentPath -leafFile:$false -checkLeafWrite:$false");
        script.Should().NotContain("checkAncestorWriteAccess");
        script.Should().Contain("Initialize-NetRatelProtectedInstallDirectories");
        script.Should().Contain("@($RootDir, $UpdaterDir, $VersionsDir, $StagingDir, $FailedDir, $LogDir)");
        var mainBodyStart = script.IndexOf("$tempDir = Join-Path $env:TEMP", StringComparison.Ordinal);
        var statePathInitialization = script.IndexOf("Initialize-NetRatelProtectedStateDirectory", mainBodyStart, StringComparison.Ordinal);
        var installPathPreflight = script.IndexOf("Initialize-NetRatelProtectedInstallDirectories -PreflightOnly", mainBodyStart, StringComparison.Ordinal);
        var lockAcquisition = script.IndexOf("$updateLockPath = Join-Path $StateDir", mainBodyStart, StringComparison.Ordinal);
        var postLockNormalization = script.IndexOf("Protect-NetRatelOwnedStateTree -path $StateDir -LockHeld", mainBodyStart, StringComparison.Ordinal);
        var postLockVerification = script.IndexOf("-RequireProtected -phase 'locked-state-tree-verification'", mainBodyStart, StringComparison.Ordinal);
        var postLockInstallNormalization = script.IndexOf("Initialize-NetRatelProtectedInstallDirectories\n", postLockVerification, StringComparison.Ordinal);
        var artifactDownload = script.IndexOf("$script:InstallerPhase = 'artifact-download-and-verification'", mainBodyStart, StringComparison.Ordinal);
        statePathInitialization.Should().BeGreaterThanOrEqualTo(mainBodyStart);
        installPathPreflight.Should().BeGreaterThan(statePathInitialization);
        lockAcquisition.Should().BeGreaterThan(installPathPreflight);
        postLockNormalization.Should().BeGreaterThan(lockAcquisition);
        postLockVerification.Should().BeGreaterThan(postLockNormalization);
        postLockInstallNormalization.Should().BeGreaterThan(postLockVerification);
        artifactDownload.Should().BeGreaterThan(postLockInstallNormalization);
        var stateTreeFunctionStart = script.IndexOf("function Protect-NetRatelOwnedStateTree", StringComparison.Ordinal);
        var finalTreeTypeGuard = script.IndexOf("reason=object-type-changed", stateTreeFunctionStart, StringComparison.Ordinal);
        var finalTreeReparseGuard = script.IndexOf("reason=path-reparse-point", finalTreeTypeGuard, StringComparison.Ordinal);
        var finalTreeEnumeration = script.IndexOf("Get-ChildItem -LiteralPath $item.FullName", finalTreeReparseGuard, StringComparison.Ordinal);
        stateTreeFunctionStart.Should().BeGreaterThanOrEqualTo(0);
        finalTreeTypeGuard.Should().BeGreaterThan(stateTreeFunctionStart);
        finalTreeReparseGuard.Should().BeGreaterThan(finalTreeTypeGuard);
        finalTreeEnumeration.Should().BeGreaterThan(finalTreeReparseGuard);
        script.Should().Contain("$script:InstallerPhase = 'owned-path-normalization'");
        script.Should().Contain("$script:InstallerLastCompletedPhase = 'update-lock-acquired'");
        script.Should().Contain("Get-CimInstance Win32_Service -Filter \"Name='$serviceNameForSummary'\"");
        script.Should().NotContain("-Path $tempDir, $LogDir");
        script.Should().NotContain("New-Item -ItemType Directory -Path $tempDir, $RootDir, $StateDir");
        script.Should().NotContain("icacls.exe $readinessDir");
        script.Should().Contain("The installed service identity is retained");
        script.Should().Contain("function Test-NetRatelOwnedUpdaterImage");
        script.Should().Contain("Get-CimInstance Win32_Service -Filter \"Name='NetRatel.Update'\" -ErrorAction Stop");
        script.Should().Contain("if ($retiredUpdaterService -and -not (Test-NetRatelOwnedUpdaterImage ([string]$retiredUpdaterService.PathName)))");
        var trustedSidStart = script.IndexOf("function Get-NetRatelTrustedStateSids {", StringComparison.Ordinal);
        var trustedSidEnd = script.IndexOf("function Get-NetRatelProtectedDirectoryAcl", trustedSidStart, StringComparison.Ordinal);
        trustedSidStart.Should().BeGreaterThanOrEqualTo(0);
        trustedSidEnd.Should().BeGreaterThan(trustedSidStart);
        script[trustedSidStart..trustedSidEnd].Should().NotContain("$existingService.StartName");
        var serviceEnvironmentFunction = script.IndexOf("function Get-NetRatelServiceEnvironmentValue", StringComparison.Ordinal);
        var initialRootAssignment = script.IndexOf("$RootDir = if ($env:NetRatel_ROOT)", StringComparison.Ordinal);
        Assert.True(initialRootAssignment >= 0, "Generated Windows installer is missing the initial install-root selection.");
        var initialStateAssignment = script.IndexOf("$StateDir = if ($env:NetRatel_STATE)", initialRootAssignment, StringComparison.Ordinal);
        Assert.True(initialStateAssignment >= 0, "Generated Windows installer is missing the initial update-state selection.");
        var servicePreflight = script.IndexOf("$serviceName = 'NetRatel.Client'", initialStateAssignment, StringComparison.Ordinal);
        Assert.True(servicePreflight >= 0, "Generated service installer is missing its service preflight.");
        var updaterDirectoryAssignment = script.IndexOf("$UpdaterDir = Join-Path $RootDir", servicePreflight, StringComparison.Ordinal);
        var selectedLogDirectory = script.IndexOf("$LogDir = if ($logDirWasExplicit)", servicePreflight, StringComparison.Ordinal);
        var configuredLogLookup = selectedLogDirectory < 0
            ? -1
            : script.LastIndexOf("$configuredServiceLogDir = Get-NetRatelServiceEnvironmentValue 'NetRatel_CLIENT_LOG_DIR'", selectedLogDirectory, StringComparison.Ordinal);
        var processLogOverride = configuredLogLookup < 0
            ? -1
            : script.IndexOf("$logDirWasExplicit = -not [string]::IsNullOrWhiteSpace($env:NetRatel_LOG_DIR)", configuredLogLookup, StringComparison.Ordinal);
        serviceEnvironmentFunction.Should().BeGreaterThan(initialStateAssignment);
        serviceEnvironmentFunction.Should().BeLessThan(servicePreflight);
        servicePreflight.Should().BeLessThan(configuredLogLookup);
        configuredLogLookup.Should().BeLessThan(processLogOverride);
        processLogOverride.Should().BeLessThan(selectedLogDirectory);
        selectedLogDirectory.Should().BeLessThan(updaterDirectoryAssignment);
        script.Should().Contain("$LogDir = if ($logDirWasExplicit) { $env:NetRatel_LOG_DIR } elseif ($configuredServiceLogDir) { $configuredServiceLogDir } else { Join-Path $env:ProgramData \"NetRatel\\logs\" }");
        script.Should().Contain("versions");
        script.Should().Contain("netratel-update.ps1");
        script.Should().Contain("/onboarding-download");
        script.Should().Contain("X-NetRatel-Enrollment-Code");
        script.Should().Contain("X-NetRatel-Artifact-Version");
        script.Should().Contain("X-NetRatel-Artifact-Sha256");
        script.Should().Contain("X-NetRatel-Artifact-Size");
        script.Should().Contain("Get-NetRatelFinalResponseHeaders");
        script.Should().Contain("Receive-NetRatelArtifactWithoutCurl");
        script.Should().Contain("$request.AllowAutoRedirect = $false");
        script.Should().Contain("$request.ReadWriteTimeout = 120000");
        script.Should().Contain("--max-time 120");
        script.Should().Contain("$Version -ne 'latest' -and $reportedVersion -ne $Version");
        script.Should().Contain("$manifestVersion -ne $resolvedVersion");
        script.Should().Contain("Waiting for the LocalSystem service");
        script.Should().NotContain("New-Service -Name $updateServiceName");
        AssertNoRetiredClientDefaults(script);
        script.Should().Contain("Get-NetRatelSha256Hex");
        script.Should().Contain("Expand-NetRatelZip");
        script.Should().Contain("Get-Command Get-FileHash -ErrorAction SilentlyContinue");
        script.Should().Contain("[System.Security.Cryptography.SHA256]::Create()");
        script.Should().Contain("Get-Command Expand-Archive -ErrorAction SilentlyContinue");
        script.Should().Contain("[System.IO.Compression.ZipFile]::ExtractToDirectory");
        script.Should().Contain("[System.Net.ServicePointManager]::SecurityProtocol");
        script.Should().Contain("PowerShell version:");
        script.Should().Contain("$legacyCommandMatch = [regex]::Match($pathName");
        script.Should().Contain("Test-NetRatelOwnedUpdaterImage ([string]$retiredUpdaterService.PathName)");
        script.Should().Contain("$actualSha = Get-NetRatelSha256Hex -Path $zipPath");
        script.Should().Contain("Expand-NetRatelZip -ZipPath $zipPath -DestinationPath $targetDir");
        script.Should().Contain("$stageDir = Join-Path $StagingDir");
        script.Should().Contain("Move-Item -LiteralPath $versionTargetDir -Destination $versionBackupDir");
        script.Should().Contain("Move-Item -LiteralPath $targetDir -Destination $versionTargetDir");
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
    public async Task Build_WindowsReadinessRequiresTwoIntegerAcknowledgementsOnOneConnection()
    {
        var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
            4098, "win-x64", "ENR-ABC123", "https://netratel.example.invalid",
            DateTimeOffset.UtcNow.AddHours(1), InstallAsService: true, SilentInstall: true));
        const string predicateStartMarker = "function Test-NetRatelHasTwoAcknowledgedHeartbeats";
        const string predicateEndMarker = "Write-Host 'Waiting for the LocalSystem service";
        var predicateStart = script.IndexOf(predicateStartMarker, StringComparison.Ordinal);
        var predicateEnd = predicateStart < 0 ? -1 : script.IndexOf(predicateEndMarker, predicateStart, StringComparison.Ordinal);
        Assert.True(predicateStart >= 0 && predicateEnd > predicateStart,
            "The generated Windows installer must expose its two-heartbeat sequence predicate before readiness polling.");
        script.Should().Contain("-not (Test-NetRatelHasTwoAcknowledgedHeartbeats $candidate) -or");
        script.Should().Contain("$candidate.connectionEpoch -eq 0");
        script.Should().Contain("$candidate.connectionId");

        var connectionId = Guid.NewGuid().ToString("D");
        var oldRecord = JsonSerializer.Serialize(new
        {
            schema = "netratel.install-readiness.ready.v1",
            stage = "heartbeat_ready",
            connectionEpoch = 22,
            connectionId
        });
        var harness = string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            script[predicateStart..predicateEnd],
            "$legacyRecord = " + PowerShellLiteral(oldRecord) + " | ConvertFrom-Json",
            "if (Test-NetRatelHasTwoAcknowledgedHeartbeats $legacyRecord) { throw 'An old record without heartbeatSequence was accepted.' }",
            "$invalidValues = @($null, '+2', '-2', '1.5', '18446744073709551616')",
            "foreach ($value in $invalidValues) { if (Test-NetRatelHasTwoAcknowledgedHeartbeats ([pscustomobject]@{ heartbeatSequence = $value })) { throw ('Invalid heartbeat sequence was accepted: ' + $value) } }",
            "$fractionalNumber = [pscustomobject]@{ heartbeatSequence = [decimal]1.5 }",
            "if (Test-NetRatelHasTwoAcknowledgedHeartbeats $fractionalNumber) { throw 'A fractional numeric heartbeat sequence was accepted.' }",
            "$expectedEpoch = [UInt64]22",
            "$expectedConnectionId = " + PowerShellLiteral(connectionId),
            "$sequenceOne = [pscustomobject]@{ heartbeatSequence = [UInt64]1; connectionEpoch = $expectedEpoch; connectionId = $expectedConnectionId }",
            "$sequenceTwo = [pscustomobject]@{ heartbeatSequence = [UInt64]2; connectionEpoch = $expectedEpoch; connectionId = $expectedConnectionId }",
            "$accepted = $null",
            "foreach ($candidate in @($sequenceOne, $sequenceTwo)) {",
            "    if ($candidate.connectionEpoch -ne $expectedEpoch -or $candidate.connectionId -ne $expectedConnectionId) { continue }",
            "    if (-not (Test-NetRatelHasTwoAcknowledgedHeartbeats $candidate)) { continue }",
            "    $accepted = $candidate",
            "    break",
            "}",
            "if (-not $accepted -or $accepted.heartbeatSequence -ne 2) { throw 'Readiness did not wait for sequence 2 on the admitted connection.' }",
            "$reconnectedSequenceOne = [pscustomobject]@{ heartbeatSequence = [UInt64]1; connectionEpoch = [UInt64]23; connectionId = " + PowerShellLiteral(Guid.NewGuid().ToString("D")) + " }",
            "if (Test-NetRatelHasTwoAcknowledgedHeartbeats $reconnectedSequenceOne) { throw 'A new connection sequence 1 was accepted as two acknowledgements.' }");

        var root = Path.Combine(Path.GetTempPath(), $"netratel-readiness-sequence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var harnessPath = Path.Combine(root, "verify-readiness-sequence.ps1");
            await File.WriteAllTextAsync(harnessPath, harness);
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "pwsh";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(harnessPath);
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                throw new System.TimeoutException("Generated Windows readiness sequence predicate did not finish within 20 seconds.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.True(process.ExitCode == 0, $"Generated Windows readiness sequence predicate failed. stdout={stdout}; stderr={stderr}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
        script.Should().Contain("NetRatel client installed and enrolled; no service readiness was requested.");
        script.Should().NotContain("netratel.install-readiness.request.v1");
        script.Should().NotContain("Test-NetRatelHasTwoAcknowledgedHeartbeats");
        script.Should().Contain("$existingServiceEnvironment = @()");
        script.Should().Contain("function Get-NetRatelServiceEnvironmentValue");
        script.Should().NotContain("$serviceName = 'NetRatel.Client'");
        AssertNoRetiredClientDefaults(script);
    }

    [Theory]
    [InlineData(true, true, true, "process")]
    [InlineData(true, false, true, "service")]
    [InlineData(true, false, false, "fallback")]
    [InlineData(false, true, true, "process")]
    [InlineData(false, false, true, "fallback")]
    public async Task Build_PowerShell_LogDirectoryResolutionHonorsProcessServiceAndFallback(
        bool installAsService,
        bool processLogDirectoryIsSet,
        bool serviceLogDirectoryIsSet,
        string expectedSource)
    {
        var service = new ScriptTemplateService();
        var script = service.Build(new DeploymentScriptTemplateRequest(
            TenantId: 4098,
            RuntimeId: "win-x64",
            EnrollmentCode: "ENR-ABC123",
            ApiBaseUrl: "https://netratel.example.invalid",
            ValidToUtc: DateTimeOffset.UtcNow.AddHours(1),
            InstallAsService: installAsService,
            SilentInstall: true));
        var root = Path.Combine(Path.GetTempPath(), $"netratel-installer-log-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var programData = Path.Combine(root, "program-data");
            var processLogDirectory = processLogDirectoryIsSet ? Path.Combine(root, "process-logs") : null;
            var serviceLogDirectory = serviceLogDirectoryIsSet ? Path.Combine(root, "service-logs") : null;
            var expectedLogDirectory = expectedSource switch
            {
                "process" => processLogDirectory,
                "service" => serviceLogDirectory,
                "fallback" => Path.Combine(programData, "NetRatel", "logs"),
                _ => throw new InvalidOperationException($"Unknown expected log-directory source '{expectedSource}'.")
            };

            var selectedLogDirectory = await RunPowerShellLogDirectoryResolverAsync(
                script,
                Path.Combine(root, "client"),
                Path.Combine(root, "update-state"),
                programData,
                processLogDirectory,
                serviceLogDirectory);

            selectedLogDirectory.Should().Be(expectedLogDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Build_WindowsServicePathResolution_PreservesInheritedCustomPathsAndRejectsConflicts()
    {
        var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
            4098, "win-x64", "ENR-ABC123", "https://netratel.example.invalid",
            DateTimeOffset.UtcNow.AddHours(1), true, true, "1.2.3", new string('a', 64)));
        var start = script.IndexOf("function Get-NetRatelCanonicalPath", StringComparison.Ordinal);
        var end = start < 0 ? -1 : script.IndexOf("$rootWasExplicit", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Windows install-path resolvers must remain isolated for behavior testing.");
        var assertionStart = script.IndexOf("function Assert-NetRatelTrustedReadinessPath", end, StringComparison.Ordinal);
        var assertionEnd = assertionStart < 0 ? -1 : script.IndexOf("function Protect-NetRatelOwnedStateTree", assertionStart, StringComparison.Ordinal);
        Assert.True(assertionStart >= 0 && assertionEnd > assertionStart, "The protected-path verifier must remain isolated for behavior testing.");

        var root = Path.Combine(Path.GetTempPath(), $"netratel-installer-paths-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var requestedRoot = Path.Combine(root, "default-root");
        var inheritedRoot = Path.Combine(root, "custom-root", "client");
        var conflictingRoot = Path.Combine(root, "other-root", "client");
        var requestedState = Path.Combine(root, "default-state");
        var inheritedState = Path.Combine(root, "custom-state");
        var conflictingState = Path.Combine(root, "other-state");
        var explicitAdminAncestor = Path.Combine(root, "administrator-owned-parent");
        var explicitRootBelowAncestor = Path.Combine(explicitAdminAncestor, "custom", "client");
        var protectedLeaf = Path.Combine(explicitAdminAncestor, "custom", "client", "versions");
        var customState = Path.Combine(explicitAdminAncestor, "custom", "client", "state");
        var readinessDirectory = Path.Combine(customState, "install-readiness");
        var readinessFile = Path.Combine(readinessDirectory, "request.json");
        var readyFile = Path.Combine(readinessDirectory, "ready.json");
        var requestPath = Path.Combine(inheritedState, "offer request.json");
        var mismatchRequestPath = Path.Combine(inheritedState, "other-request.json");
        var harness = string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            script[start..end],
            "$adminAncestor = " + PowerShellLiteral(explicitAdminAncestor),
            "$customRoot = " + PowerShellLiteral(explicitRootBelowAncestor),
            "$protectedLeaf = " + PowerShellLiteral(protectedLeaf),
            "function Get-NetRatelTrustedStateSids { return @('S-1-5-18') }",
            "function Test-NetRatelLocalAdministratorMemberSid([string] $sid) { return $sid -eq 'S-1-5-21-local-admin' }",
            "function Test-NetRatelTrustedAdministratorSid([string] $sid) { return Test-NetRatelLocalAdministratorMemberSid $sid }",
            "function Test-Path { [CmdletBinding()] param([string]$LiteralPath,[string]$PathType,[switch]$Force) return $true }",
            "function Get-Item { [CmdletBinding()] param([string]$LiteralPath,[switch]$Force) if ($script:inspectionFailurePath -and [string]::Equals($LiteralPath,$script:inspectionFailurePath,[StringComparison]::OrdinalIgnoreCase)) { throw [System.UnauthorizedAccessException]::new('synthetic access denied') }; $isFile = [string]::Equals($LiteralPath, " + PowerShellLiteral(readinessFile) + ", [StringComparison]::OrdinalIgnoreCase) -or [string]::Equals($LiteralPath, " + PowerShellLiteral(readyFile) + ", [StringComparison]::OrdinalIgnoreCase); [pscustomobject]@{ FullName=$LiteralPath; Attributes=$(if ($isFile) { [System.IO.FileAttributes]::Normal } else { [System.IO.FileAttributes]::Directory }); PSIsContainer=(-not $isFile) } }",
            "function Get-Acl { [CmdletBinding()] param([string]$LiteralPath)",
            "    $ownerSid = if ([string]::Equals($LiteralPath, $adminAncestor, [StringComparison]::OrdinalIgnoreCase)) { 'S-1-5-21-local-admin' } else { 'S-1-5-18' }",
            "    $rules = @()",
            "    $adminWriteLeaf = ($script:addAdminWriteToLeaf -and [string]::Equals($LiteralPath, $protectedLeaf, [StringComparison]::OrdinalIgnoreCase)) -or ($script:addAdminWriteToReadinessDirectory -and [string]::Equals($LiteralPath, " + PowerShellLiteral(readinessDirectory) + ", [StringComparison]::OrdinalIgnoreCase)) -or ($script:addAdminWriteToReadinessFile -and ([string]::Equals($LiteralPath, " + PowerShellLiteral(readinessFile) + ", [StringComparison]::OrdinalIgnoreCase) -or [string]::Equals($LiteralPath, " + PowerShellLiteral(readyFile) + ", [StringComparison]::OrdinalIgnoreCase)))",
            "    if ($ownerSid -eq 'S-1-5-21-local-admin' -or $adminWriteLeaf) {",
            "        $rules += [pscustomobject]@{ AccessControlType=[System.Security.AccessControl.AccessControlType]::Allow; InheritanceFlags=[System.Security.AccessControl.InheritanceFlags]::None; PropagationFlags=[System.Security.AccessControl.PropagationFlags]::None; IdentityReference=[pscustomobject]@{ Value='S-1-5-21-local-admin' }; FileSystemRights=[System.Security.AccessControl.FileSystemRights]::WriteData; IsInherited=$false }",
            "    }",
            "    if (($script:addUsersWriteToAncestor -or $script:addUsersDeleteToAncestor) -and [string]::Equals($LiteralPath, $adminAncestor, [StringComparison]::OrdinalIgnoreCase)) {",
            "        $userRights = if ($script:addUsersDeleteToAncestor) { [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles } else { [System.Security.AccessControl.FileSystemRights]::WriteData }",
            "        $rules += [pscustomobject]@{ AccessControlType=[System.Security.AccessControl.AccessControlType]::Allow; InheritanceFlags=[System.Security.AccessControl.InheritanceFlags]::None; PropagationFlags=[System.Security.AccessControl.PropagationFlags]::None; IdentityReference=[pscustomobject]@{ Value='S-1-5-32-545' }; FileSystemRights=$userRights; IsInherited=$true }",
            "    }",
            "    $acl = [pscustomobject]@{ OwnerSid=$ownerSid; Rules=$rules }",
            "    $acl | Add-Member ScriptMethod GetOwner { param($type) return [pscustomobject]@{ Value=$this.OwnerSid } }",
            "    $acl | Add-Member ScriptMethod GetAccessRules { param($includeExplicit,$includeInherited,$sidType) return ,$this.Rules }",
            "    return $acl",
            "}",
            script[assertionStart..assertionEnd],
            "if (-not (Test-NetRatelSameOrAncestorPath $adminAncestor $customRoot)) { throw 'The explicit custom root did not identify its verified administrator-owned ancestor.' }",
            "if (Test-NetRatelSameOrAncestorPath $adminAncestor " + PowerShellLiteral(conflictingRoot) + ") { throw 'An unrelated path was treated as the explicit root descendant.' }",
            "Assert-NetRatelTrustedReadinessPath $protectedLeaf $false $false $true $false $true",
            "Assert-NetRatelTrustedReadinessPath " + PowerShellLiteral(readinessDirectory) + " $false $false $true $false $true",
            "Assert-NetRatelTrustedReadinessPath " + PowerShellLiteral(readinessFile) + " $true $false $true $false $true",
            "$unscopedAncestorError = $null",
            "try { Assert-NetRatelTrustedReadinessPath " + PowerShellLiteral(readinessDirectory) + " $false } catch { $unscopedAncestorError = $_.Exception.Message }",
            "if (-not $unscopedAncestorError) { throw 'A custom administrator-owned ancestor was accepted without explicit path provenance.' }",
            "if ($unscopedAncestorError -notmatch 'phase=path-preflight; role=filesystem-path; scope=ancestor; component=\\d+; reason=untrusted-owner; normalization=not-attempted; exception=RuntimeException') { throw 'The unscoped ancestor rejection did not report its bounded owner-check category.' }",
            "if ($unscopedAncestorError.Contains($adminAncestor) -or $unscopedAncestorError.Contains('S-1-5-21-local-admin')) { throw 'The protected-path diagnostic disclosed a path or SID.' }",
            "$leafOnlyAdministratorError = $null",
            "try { Assert-NetRatelTrustedReadinessPath " + PowerShellLiteral(readinessFile) + " $true $false $true $true } catch { $leafOnlyAdministratorError = $_.Exception.Message }",
            "if ($leafOnlyAdministratorError -notmatch 'scope=ancestor; component=\\d+; reason=untrusted-owner') { throw 'A leaf-only administrator allowance also admitted a shared ancestor.' }",
            "$script:addAdminWriteToLeaf = $true",
            "$leafWriteError = $null",
            "try { Assert-NetRatelTrustedReadinessPath $protectedLeaf $false $false $true $false $true } catch { $leafWriteError = $_.Exception.Message }",
            "if (-not $leafWriteError) { throw 'A protected leaf ACL was allowed to inherit individual-administrator write access.' }",
            "if ($leafWriteError -notmatch 'phase=path-preflight; role=filesystem-path; scope=leaf; component=\\d+; reason=leaf-write; normalization=not-attempted; exception=RuntimeException; aceRights=WriteData; aceRightsValue=2; aceInherited=false; aceInheritance=None; acePropagation=None') { throw 'The protected leaf rejection did not report its bounded write-check category and ACE permissions.' }",
            "$script:addUsersWriteToAncestor = $true",
            "Assert-NetRatelTrustedReadinessPath -path (Join-Path $adminAncestor 'custom') -leafFile:$false -checkLeafWrite:$false -allowLegacyAdministratorAncestors:$true",
            "$script:addUsersDeleteToAncestor = $true",
            "$ancestorDeleteError = $null",
            "try { Assert-NetRatelTrustedReadinessPath -path (Join-Path $adminAncestor 'custom') -leafFile:$false -checkLeafWrite:$false -allowLegacyAdministratorAncestors:$true } catch { $ancestorDeleteError = $_.Exception.Message }",
            "if ($ancestorDeleteError -notmatch 'scope=ancestor; component=\\d+; reason=replacement-access') { throw 'Dangerous replacement access on an ancestor was allowed when checking leaf writes was disabled.' }",
            "$script:addUsersWriteToAncestor = $false",
            "$script:addUsersDeleteToAncestor = $false",
            "$script:addAdminWriteToReadinessDirectory = $true",
            "$readinessDirectoryWriteError = $null",
            "try { Assert-NetRatelTrustedReadinessPath " + PowerShellLiteral(readinessDirectory) + " $false $false $true $false $true } catch { $readinessDirectoryWriteError = $_.Exception.Message }",
            "if (-not $readinessDirectoryWriteError) { throw 'A readiness directory accepted individual-administrator write access.' }",
            "if ($readinessDirectoryWriteError -notmatch 'phase=path-preflight; role=filesystem-path; scope=leaf; component=\\d+; reason=leaf-write; normalization=not-attempted; exception=RuntimeException; aceRights=WriteData; aceRightsValue=2; aceInherited=false; aceInheritance=None; acePropagation=None') { throw 'The readiness-directory rejection did not report its bounded write-check category and ACE permissions.' }",
            "$script:addAdminWriteToReadinessDirectory = $false",
            "$script:addAdminWriteToReadinessFile = $true",
            "$readinessFileWriteError = $null",
            "try { Assert-NetRatelTrustedReadinessPath " + PowerShellLiteral(readinessFile) + " $true $false $true $false $true } catch { $readinessFileWriteError = $_.Exception.Message }",
            "if (-not $readinessFileWriteError) { throw 'A readiness file accepted individual-administrator write access.' }",
            "if ($readinessFileWriteError -notmatch 'phase=path-preflight; role=filesystem-path; scope=leaf; component=\\d+; reason=leaf-write; normalization=not-attempted; exception=RuntimeException; aceRights=WriteData; aceRightsValue=2; aceInherited=false; aceInheritance=None; acePropagation=None') { throw 'The readiness-file rejection did not report its bounded write-check category and ACE permissions.' }",
            "$script:inspectionFailurePath = " + PowerShellLiteral(Path.Combine(root, "synthetic-denied-path")),
            "$inspectionFailureError = $null",
            "try { Assert-NetRatelTrustedReadinessPath $script:inspectionFailurePath $false } catch { $inspectionFailureError = $_.Exception.Message }",
            "if ($inspectionFailureError -notmatch 'reason=path-inspection-failed; normalization=not-attempted; exception=UnauthorizedAccessException') { throw 'The path inspection diagnostic lost its underlying access exception.' }",
            "if ($inspectionFailureError.Contains($script:inspectionFailurePath)) { throw 'The path inspection diagnostic disclosed the inspected path.' }",
            "$script:addAdminWriteToReadinessFile = $false",
            "$root = Resolve-NetRatelInstallRoot " + PowerShellLiteral(requestedRoot) + " " + PowerShellLiteral(inheritedRoot) + " " + PowerShellLiteral(inheritedRoot) + " $false",
            "if (-not [string]::Equals([System.IO.Path]::GetFullPath($root), [System.IO.Path]::GetFullPath(" + PowerShellLiteral(inheritedRoot) + "), [StringComparison]::OrdinalIgnoreCase)) { throw 'Inherited package root was not retained.' }",
            "$rootConflictRejected = $false",
            "try { [void](Resolve-NetRatelInstallRoot " + PowerShellLiteral(requestedRoot) + " " + PowerShellLiteral(inheritedRoot) + " " + PowerShellLiteral(inheritedRoot) + " $true) } catch { $rootConflictRejected = $true }",
            "if (-not $rootConflictRejected) { throw 'Explicit package-root conflict was accepted.' }",
            "$state = Resolve-NetRatelStateDirectory " + PowerShellLiteral(requestedState) + " " + PowerShellLiteral(inheritedState) + " " + PowerShellLiteral(inheritedState) + " $false",
            "if (-not [string]::Equals([System.IO.Path]::GetFullPath($state), [System.IO.Path]::GetFullPath(" + PowerShellLiteral(inheritedState) + "), [StringComparison]::OrdinalIgnoreCase)) { throw 'Inherited update state was not retained.' }",
            "$stateConflictRejected = $false",
            "try { [void](Resolve-NetRatelStateDirectory " + PowerShellLiteral(requestedState) + " " + PowerShellLiteral(inheritedState) + " " + PowerShellLiteral(inheritedState) + " $true) } catch { $stateConflictRejected = $true }",
            "if (-not $stateConflictRejected) { throw 'Explicit update-state conflict was accepted.' }",
            "$request = Resolve-NetRatelUpdateRequestPath $state " + PowerShellLiteral(requestPath) + " '' '' " + PowerShellLiteral(requestPath),
            "if (-not [string]::Equals([System.IO.Path]::GetFullPath($request), [System.IO.Path]::GetFullPath(" + PowerShellLiteral(requestPath) + "), [StringComparison]::OrdinalIgnoreCase)) { throw 'The paired request path was not retained.' }",
            "$requestConflictRejected = $false",
            "try { [void](Resolve-NetRatelUpdateRequestPath $state " + PowerShellLiteral(requestPath) + " '' '' " + PowerShellLiteral(mismatchRequestPath) + ") } catch { $requestConflictRejected = $true }",
            "if (-not $requestConflictRejected) { throw 'Conflicting updater request path was accepted.' }",
            "$defaultRequest = Resolve-NetRatelUpdateRequestPath $state '' '' '' ''",
            "if (-not [string]::Equals($defaultRequest, (Join-Path $state 'request.json'), [StringComparison]::OrdinalIgnoreCase)) { throw 'The default request path was not derived from the effective state directory.' }");
        var harnessPath = Path.Combine(root, "verify-paths.ps1");
        await File.WriteAllTextAsync(harnessPath, harness);

        try
        {
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "pwsh";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(harnessPath);
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Skip("PowerShell is required to execute the Windows installer path resolver.");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                throw new TimeoutException("Windows installer path resolver did not finish within 20 seconds.");
            }

            Assert.True(process.ExitCode == 0,
                "Windows installer path resolver cases failed: " + await process.StandardError.ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Build_WindowsInstallerAcceptsOnlyBoundedProtectedPartialInstallLayouts()
    {
        var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
            4098, "win-x64", "ENR-ABC123", "https://netratel.example.invalid",
            DateTimeOffset.UtcNow.AddHours(1), true, true, "1.2.3", new string('a', 64)));
        var functionStart = script.IndexOf("function Test-NetRatelDirectoryEmpty", StringComparison.Ordinal);
        var functionEnd = functionStart < 0 ? -1 : script.IndexOf("function Test-NetRatelDefaultLogLayout", functionStart, StringComparison.Ordinal);
        Assert.True(functionStart >= 0 && functionEnd > functionStart,
            "Partial-install eligibility helpers must remain isolated for behavior testing.");

        var root = Path.Combine(Path.GetTempPath(), $"netratel-installer-partial-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var harness = string.Join(Environment.NewLine,
                "$ErrorActionPreference = 'Stop'",
                script[functionStart..functionEnd],
                "$script:NetRatelStateAncestorAllowance = $false",
                "function Assert-NetRatelTrustedReadinessPath { param([string]$path,[switch]$leafFile,[switch]$allowMissingLeaf,[switch]$allowInheritedStateWrites,[switch]$checkLeafWrite,[switch]$allowLegacyAdministrators,[switch]$allowLegacyAdministratorAncestors,[string]$pathRole,[string]$phase,[switch]$normalizationAttempted); if ($leafFile -and $path -like '*unsafe-acl*') { throw 'synthetic unsafe ACL' } }",
                "function Test-NetRatelProtectedAclMatches { param([string]$path,[bool]$isDirectory,[string[]]$trustedSids,[string]$pathRole,[string]$phase) return $true }",
                "function Get-NetRatelTrustedStateSids { return @('S-1-5-18','S-1-5-32-544') }",
                "function New-InstallRoot([string]$path) { New-Item -ItemType Directory -Path $path -Force | Out-Null; foreach ($name in @('updater','versions','staging','failed')) { New-Item -ItemType Directory -Path (Join-Path $path $name) | Out-Null } }",
                "function Add-Tree([string]$path) { New-Item -ItemType Directory -Path $path -Force | Out-Null; Set-Content -LiteralPath (Join-Path $path 'payload.bin') -Value 'synthetic bytes' }",
                "$trustedSids = @('S-1-5-18')",
                "$phase = 'layout-test'",
                "$valid = Join-Path " + PowerShellLiteral(root) + " 'valid'",
                "New-InstallRoot $valid",
                "Add-Tree (Join-Path $valid ('staging/install-' + [Guid]::NewGuid().ToString('N')))",
                "Add-Tree (Join-Path $valid 'versions/1.2.3-rc.4+build.5')",
                "Add-Tree (Join-Path $valid ('failed/1.2.3-installer-failed-' + [Guid]::NewGuid().ToString('D')))",
                "Add-Tree (Join-Path $valid ('failed/1.2.2-replaced-' + [Guid]::NewGuid().ToString('N')))",
                "if (-not (Test-NetRatelCustomInstallRootLayout $valid $phase $trustedSids)) { throw 'A generated partial staging, installed-version, or failed-version tree was rejected.' }",
                "$invalidVersion = Join-Path " + PowerShellLiteral(root) + " 'invalid-version'",
                "New-InstallRoot $invalidVersion",
                "Add-Tree (Join-Path $invalidVersion 'versions/not-a-version')",
                "if (Test-NetRatelCustomInstallRootLayout $invalidVersion $phase $trustedSids) { throw 'A non-semantic-version tree was accepted.' }",
                "$invalidFailure = Join-Path " + PowerShellLiteral(root) + " 'invalid-failure'",
                "New-InstallRoot $invalidFailure",
                "Add-Tree (Join-Path $invalidFailure 'failed/1.2.3-installer-failed-not-a-guid')",
                "if (Test-NetRatelCustomInstallRootLayout $invalidFailure $phase $trustedSids) { throw 'A failed tree without the generated attempt identifier was accepted.' }",
                "$unexpected = Join-Path " + PowerShellLiteral(root) + " 'unexpected'",
                "New-InstallRoot $unexpected",
                "Set-Content -LiteralPath (Join-Path $unexpected 'unrelated.txt') -Value 'untrusted'",
                "if (Test-NetRatelCustomInstallRootLayout $unexpected $phase $trustedSids) { throw 'An unrelated root entry was accepted.' }",
                "$oversized = Join-Path " + PowerShellLiteral(root) + " 'oversized'",
                "New-InstallRoot $oversized",
                "for ($index = 0; $index -lt 17; $index++) { Add-Tree (Join-Path (Join-Path $oversized 'versions') ('1.0.' + $index)) }",
                "if (Test-NetRatelCustomInstallRootLayout $oversized $phase $trustedSids) { throw 'An oversized version remnant list was accepted.' }",
                "$updaterRemainder = Join-Path " + PowerShellLiteral(root) + " 'updater-remainder'",
                "New-InstallRoot $updaterRemainder",
                "$updaterScript = Join-Path (Join-Path $updaterRemainder 'updater') 'netratel-update.ps1'",
                "Set-Content -LiteralPath $updaterScript -Value \"throw 'untrusted updater content must not execute during eligibility'\"",
                "if (-not (Test-NetRatelCustomInstallRootLayout $updaterRemainder $phase $trustedSids)) { throw 'The exact protected updater-script crash remnant was rejected.' }",
                "Set-Content -LiteralPath (Join-Path (Join-Path $updaterRemainder 'updater') 'unexpected.ps1') -Value 'unrelated'",
                "if (Test-NetRatelCustomInstallRootLayout $updaterRemainder $phase $trustedSids) { throw 'An unrelated updater entry was accepted.' }",
                "$unsafeUpdaterRemainder = Join-Path " + PowerShellLiteral(root) + " 'unsafe-acl-updater-remainder'",
                "New-InstallRoot $unsafeUpdaterRemainder",
                "Set-Content -LiteralPath (Join-Path (Join-Path $unsafeUpdaterRemainder 'updater') 'netratel-update.ps1') -Value 'synthetic unsafe ACL'",
                "if (Test-NetRatelCustomInstallRootLayout $unsafeUpdaterRemainder $phase $trustedSids) { throw 'An updater remnant with untrusted write or replacement access was accepted.' }",
                "$state = Join-Path " + PowerShellLiteral(root) + " 'partial-state'",
                "New-Item -ItemType Directory -Path $state -Force | Out-Null",
                "if (-not (Test-NetRatelCustomUpdateStateLayout $state $phase)) { throw 'An empty update-state directory was rejected.' }",
                "New-Item -ItemType File -Path (Join-Path $state 'update.lock') | Out-Null",
                "$handoffs = Join-Path $state 'install-handoffs'; New-Item -ItemType Directory -Path $handoffs | Out-Null",
                "$handoffId = '0123456789abcdef0123456789abcdef'",
                "Set-Content -LiteralPath (Join-Path $handoffs ('handoff-' + $handoffId + '.json')) -Value '{synthetic request}'",
                "Set-Content -LiteralPath (Join-Path $handoffs ('handoff-' + $handoffId + '.ps1')) -Value \"throw 'untrusted handoff content must not execute during eligibility'\"",
                "Set-Content -LiteralPath (Join-Path $handoffs ('handoff-' + $handoffId + '.result.json')) -Value '{synthetic result}'",
                "Set-Content -LiteralPath (Join-Path $handoffs ('handoff-' + $handoffId + '.result.json.123.tmp')) -Value '{synthetic temp result}'",
                "if (-not (Test-NetRatelCustomUpdateStateLayout $state $phase)) { throw 'Bounded protected handoff remnants were rejected.' }",
                "Set-Content -LiteralPath (Join-Path $handoffs 'unrelated.txt') -Value 'untrusted'",
                "if (Test-NetRatelCustomUpdateStateLayout $state $phase) { throw 'An unrelated state handoff file was accepted.' }",
                "$unsafeFileState = Join-Path " + PowerShellLiteral(root) + " 'unsafe-acl-file-state'; New-Item -ItemType Directory -Path $unsafeFileState -Force | Out-Null",
                "$unsafeHandoffs = Join-Path $unsafeFileState 'install-handoffs'; New-Item -ItemType Directory -Path $unsafeHandoffs | Out-Null",
                "Set-Content -LiteralPath (Join-Path $unsafeHandoffs ('handoff-' + $handoffId + '.json')) -Value '{synthetic}'",
                "if (Test-NetRatelCustomUpdateStateLayout $unsafeFileState $phase) { throw 'A handoff remnant with untrusted write or replacement access was accepted.' }",
                "$badLock = Join-Path " + PowerShellLiteral(root) + " 'nonempty-lock-state'",
                "New-Item -ItemType Directory -Path $badLock -Force | Out-Null",
                "Set-Content -LiteralPath (Join-Path $badLock 'update.lock') -Value 'not empty'",
                "if (Test-NetRatelCustomUpdateStateLayout $badLock $phase) { throw 'A nonempty update lock was accepted.' }",
                "$unsafeLockState = Join-Path " + PowerShellLiteral(root) + " 'unsafe-acl-lock-state'; New-Item -ItemType Directory -Path $unsafeLockState -Force | Out-Null",
                "New-Item -ItemType File -Path (Join-Path $unsafeLockState 'update.lock') | Out-Null",
                "if (Test-NetRatelCustomUpdateStateLayout $unsafeLockState $phase) { throw 'A lock with untrusted write or replacement access was accepted.' }");
            var harnessPath = Path.Combine(root, "verify-partial-layout.ps1");
            await File.WriteAllTextAsync(harnessPath, harness);

            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "pwsh";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(harnessPath);
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception) { Assert.Skip("PowerShell is required to test partial-install eligibility."); }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                throw new TimeoutException("Windows installer partial-layout checks did not finish within 20 seconds.");
            }

            Assert.True(process.ExitCode == 0,
                "Windows installer partial-layout checks failed: " + await process.StandardError.ReadToEndAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
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
        script.Should().Contain("X-NetRatel-Artifact-Sha256");
        script.Should().Contain("REPORTED_VERSION=$(read_response_header");
        script.Should().Contain("status == \"200\"");
        script.Should().Contain("--max-time 120");
        script.Should().Contain("NetRatelCLIENT__Client__AutoUpdate__Mode=Service");
        script.Should().Contain("Environment=NetRatelCLIENT__Client__ApiBaseUrl=${API_BASE}");
        script.Should().Contain("NetRatelCLIENT__Client__AutoUpdate__StateDirectory=${STATE_DIR}");
        AssertNoRetiredClientDefaults(script);
        script.Should().Contain("EUID");
        script.Should().Contain("for REQUIRED_COMMAND in curl sha256sum systemctl flock python3 install timeout realpath readlink grep awk wc tr; do");
        script.Should().NotContain("unzip");
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
        script.Should().Contain("TARGET_BACKUP=\"${FAILED_DIR}/${RESOLVED_VERSION}-replaced-$(basename \"${TXN_DIR}\")\"");
        script.Should().Contain("if [ \"${TARGET_BACKED_UP}\" = true ]; then");
        script.Should().Contain("if ! mv -- \"${TARGET_BACKUP}\" \"${TARGET_DIR}\"; then rollback_failed=true; fi");
        script.Should().Contain("systemctl disable --now sto-client.service");
        script.Should().Contain("if ! wait_for_service_stopped; then");
        script.Should().Contain("TARGET_BACKED_UP=true");
        script.Should().Contain("Installer activation failed and rollback could not fully restore the previous installation. Recovery snapshot:");
        var zipPreflight = script.IndexOf("Artifact ZIP preflight failed: ", StringComparison.Ordinal);
        zipPreflight.Should().BeGreaterThanOrEqualTo(0);
        var extraction = script.IndexOf("archive.extractall(stage_dir)", zipPreflight, StringComparison.Ordinal);
        extraction.Should().BeGreaterThan(zipPreflight);
        var stopVerification = script.IndexOf("if ! wait_for_service_stopped; then", extraction, StringComparison.Ordinal);
        stopVerification.Should().BeGreaterThan(extraction);
        var targetBackupMove = script.IndexOf("mv \"${TARGET_DIR}\" \"${TARGET_BACKUP}\"", extraction, StringComparison.Ordinal);
        targetBackupMove.Should().BeGreaterThan(extraction);
        targetBackupMove.Should().BeGreaterThan(stopVerification);
        script.Should().Contain("finish_install() {");
        script.Should().Contain("local status=$?");
        script.Should().Contain("trap finish_install EXIT");
        script.Should().Contain("if [ \"${status}\" -ne 0 ] && [ \"${TRANSACTION_MUTATED}\" = true ]; then");
        script.Should().Contain("if [ \"${status}\" -eq 0 ] || [ \"${rollback_failed}\" = false ]; then");
        script.Should().Contain("if [ \"${rollback_failed}\" = true ]; then exit 70; fi");
        script.Should().Contain("exit \"${status}\"");
        script.Should().Contain("install -m 0755 \"${TARGET_DIR}/updater/netratel-update.sh\" \"${UPDATER_DIR}/.netratel-update.sh.$$\"");
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
        script.Should().Contain("X-NetRatel-Artifact-Sha256");
        script.Should().Contain("status == \"200\"");
        script.Should().Contain("--max-time 120");
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
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task Build_MacOS_InstallsExactPackageWithoutServiceOnUnixHost()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("The macOS package installer integration test requires a Unix host.");

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
                "#!/usr/bin/env bash\nheaders=\"\"\nout=\"\"\nwhile [ \"$#\" -gt 0 ]; do case \"$1\" in -D) headers=\"$2\"; shift 2;; -o) out=\"$2\"; shift 2;; *) shift;; esac; done\ncp \"$FAKE_ARCHIVE\" \"$out\"\nprintf 'HTTP/1.1 200 OK\\r\\nX-NetRatel-Artifact-Rid: osx-arm64\\r\\nX-NetRatel-Artifact-Version: 0.4.131-rc.1\\r\\nX-NetRatel-Artifact-Sha256: %s\\r\\nX-NetRatel-Artifact-Size: %s\\r\\n\\r\\n' \"$FAKE_SHA\" \"$FAKE_SIZE\" > \"$headers\"\n");
            SetUnixExecutable(curl);
            var start = new ProcessStartInfo("bash", scriptPath)
            {
                RedirectStandardError = true, UseShellExecute = false
            };
            start.Environment["PATH"] = $"{bin}:{Environment.GetEnvironmentVariable("PATH")}";
            start.Environment["FAKE_ARCHIVE"] = archivePath;
            start.Environment["FAKE_SHA"] = sha;
            start.Environment["FAKE_SIZE"] = new FileInfo(archivePath).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    [Theory]
    [SupportedOSPlatform("linux")]
    [InlineData("https://old-api.example.invalid", "https://split-gateway.example.invalid", "https://split-gateway.example.invalid")]
    [InlineData("https://old-api.example.invalid/api/", "https://old-api.example.invalid", "")]
    public async Task Build_MacOS_Service_Preserves_Only_ExplicitSplitGateway(
        string previousApiBase, string previousGatewayEndpoint, string expectedGatewayEndpoint)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The macOS service installer integration test requires Linux.");

        var root = Path.Combine(Path.GetTempPath(), $"netratel-macos-service-installer-{Guid.NewGuid():N}");
        var bin = Path.Combine(root, "bin");
        var archivePath = Path.Combine(root, "client.zip");
        var scriptPath = Path.Combine(root, "install.sh");
        var plistPath = Path.Combine(root, "existing.plist");
        Directory.CreateDirectory(bin);
        try
        {
            var installRoot = Path.Combine(root, "installed");
            var previousVersion = Path.Combine(installRoot, "versions", "0.4.130");
            Directory.CreateDirectory(previousVersion);
            await File.WriteAllTextAsync(Path.Combine(previousVersion, "clientsettings.json"), $$"""
                {
                  "Client": { "ApiBaseUrl": "{{previousApiBase}}", "TerminalGracefulExitTimeoutMs": 3210 },
                  "Gateway": {
                    "Endpoint": "{{previousGatewayEndpoint}}",
                    "FileGatewayEnabled": false,
                    "ControlGatewayEnabled": true
                  },
                  "Transport": { "Mode": "AkkaPresence" }
                }
                """);
            Directory.CreateSymbolicLink(Path.Combine(installRoot, "current"), previousVersion);
            CreateUnixArtifact(archivePath, "0.4.131-rc.1", "osx-arm64");
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(archivePath))).ToLowerInvariant();
            new XDocument(
                new XElement("plist",
                    new XAttribute("version", "1.0"),
                    new XElement("dict",
                        new XElement("key", "Label"),
                        new XElement("string", "co.za.netratel.client"),
                        new XElement("key", "EnvironmentVariables"),
                        new XElement("dict",
                            new XElement("key", "NetRatelCLIENT__Client__ApiBaseUrl"),
                            new XElement("string", previousApiBase),
                            new XElement("key", "NetRatelCLIENT__Gateway__Endpoint"),
                            new XElement("string", previousGatewayEndpoint),
                            new XElement("key", "NetRatelCLIENT__Gateway__FileGatewayEnabled"),
                            new XElement("string", "false"),
                            new XElement("key", "NetRatelCLIENT__Gateway__ControlGatewayEnabled"),
                            new XElement("string", "true"),
                            new XElement("key", "NetRatelCLIENT__Transport__Mode"),
                            new XElement("string", "AkkaPresence"),
                            new XElement("key", "Custom__ServiceValue"),
                            new XElement("string", "kept & safe")))))
                .Save(plistPath);

            var script = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                4098, "osx-arm64", "ENR-ABC123", "https://example.test",
                DateTimeOffset.UtcNow.AddHours(1), true, true, "0.4.131-rc.1", sha));
            await File.WriteAllTextAsync(scriptPath, script);
            var curl = Path.Combine(bin, "curl");
            await File.WriteAllTextAsync(curl,
                "#!/usr/bin/env bash\nheaders=\"\"\nout=\"\"\nwhile [ \"$#\" -gt 0 ]; do case \"$1\" in -D) headers=\"$2\"; shift 2;; -o) out=\"$2\"; shift 2;; *) shift;; esac; done\ncp \"$FAKE_ARCHIVE\" \"$out\"\nprintf 'HTTP/1.1 200 OK\\r\\nX-NetRatel-Artifact-Rid: osx-arm64\\r\\nX-NetRatel-Artifact-Version: 0.4.131-rc.1\\r\\nX-NetRatel-Artifact-Sha256: %s\\r\\nX-NetRatel-Artifact-Size: %s\\r\\n\\r\\n' \"$FAKE_SHA\" \"$FAKE_SIZE\" > \"$headers\"\n");
            var launchctl = Path.Combine(bin, "launchctl");
            await File.WriteAllTextAsync(launchctl, "#!/usr/bin/env bash\nexit 0\n");
            foreach (var file in Directory.EnumerateFiles(bin))
                SetUnixExecutable(file);

            var start = new ProcessStartInfo("bash", scriptPath)
            {
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.Environment["PATH"] = $"{bin}:{Environment.GetEnvironmentVariable("PATH")}";
            start.Environment["FAKE_ARCHIVE"] = archivePath;
            start.Environment["FAKE_SHA"] = sha;
            start.Environment["FAKE_SIZE"] = new FileInfo(archivePath).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["NetRatel_ROOT"] = installRoot;
            start.Environment["NetRatel_LAUNCHD_PLIST"] = plistPath;
            start.Environment["NetRatel_TEST_ALLOW_NONROOT"] = "true";
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, await process.StandardError.ReadToEndAsync());

            var rewrittenPlist = XDocument.Load(plistPath);
            var topLevel = ReadPlistDictionary(rewrittenPlist.Root!.Element("dict")!);
            var rewrittenEnvironment = ReadPlistDictionary(topLevel["EnvironmentVariables"]);
            if (string.IsNullOrEmpty(expectedGatewayEndpoint))
                rewrittenEnvironment.Should().NotContainKey("NetRatelCLIENT__Gateway__Endpoint");
            else
                rewrittenEnvironment["NetRatelCLIENT__Gateway__Endpoint"].Value.Should().Be(expectedGatewayEndpoint);
            rewrittenEnvironment.Should().NotContainKey("NetRatelCLIENT__Gateway__FileGatewayEnabled");
            rewrittenEnvironment["Custom__ServiceValue"].Value.Should().Be("kept & safe");
            rewrittenEnvironment["NetRatelCLIENT__Client__ApiBaseUrl"].Value.Should().Be("https://example.test");
            rewrittenEnvironment.Should().NotContainKey("NetRatelCLIENT__Gateway__ControlGatewayEnabled");
            rewrittenEnvironment.Should().NotContainKey("NetRatelCLIENT__Transport__Mode");

            using var installedSettings = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(installRoot, "versions", "0.4.131-rc.1", "clientsettings.json")));
            installedSettings.RootElement.GetProperty("Client").GetProperty("TerminalGracefulExitTimeoutMs").GetInt32().Should().Be(3210);
            var installedGateway = installedSettings.RootElement.GetProperty("Gateway");
            if (string.IsNullOrEmpty(expectedGatewayEndpoint))
                installedGateway.TryGetProperty("Endpoint", out _).Should().BeFalse();
            else
                installedGateway.GetProperty("Endpoint").GetString().Should().Be(expectedGatewayEndpoint);
            installedGateway.TryGetProperty("FileGatewayEnabled", out _).Should().BeFalse();
            installedGateway.TryGetProperty("ControlGatewayEnabled", out _).Should().BeFalse();
            installedSettings.RootElement.TryGetProperty("Transport", out _).Should().BeFalse();
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
        script.Should().NotContain(PlistKeyElement("NetRatelCLIENT__Gateway__Endpoint"));
    }

    private static string PlistKeyElement(string name)
        => string.Concat("<", "key>", name, "</", "key>");

    private static async Task<string> RunPowerShellLogDirectoryResolverAsync(
        string script,
        string installRoot,
        string stateDirectory,
        string programData,
        string? processLogDirectory,
        string? serviceLogDirectory)
    {
        const string assignment = "$LogDir = if ($logDirWasExplicit)";
        var assignmentStart = script.IndexOf(assignment, StringComparison.Ordinal);
        Assert.True(assignmentStart >= 0, "Generated Windows installer is missing its log-directory selection.");
        var assignmentEnd = script.IndexOf('\n', assignmentStart);
        var generatedPrefix = assignmentEnd >= 0 ? script[..assignmentEnd] : script;
        var harness = string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            "function Get-CimInstance { [CmdletBinding()] param([string]$ClassName,[string]$Filter) if ($ClassName -eq 'Win32_Service') { return [pscustomobject]@{ PathName=$env:NETRATEL_TEST_SERVICE_PATH } }; return $null }",
            "function Get-ItemProperty { [CmdletBinding()] param([string]$Path) return [pscustomobject]@{ Environment=@($env:NETRATEL_TEST_SERVICE_ENVIRONMENT) } }",
            "function Test-Path { [CmdletBinding()] param([string]$LiteralPath,[string]$PathType,[switch]$Force) return $false }",
            generatedPrefix,
            "Write-Output ('NETRATEL_SELECTED_LOG_DIR=' + $LogDir)");
        var harnessRoot = Path.Combine(Path.GetTempPath(), $"netratel-installer-log-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(harnessRoot);

        try
        {
            var harnessPath = Path.Combine(harnessRoot, "verify-log-directory.ps1");
            await File.WriteAllTextAsync(harnessPath, harness);
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "pwsh";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(harnessPath);
            process.StartInfo.Environment["NetRatel_ROOT"] = installRoot;
            process.StartInfo.Environment["NetRatel_STATE"] = stateDirectory;
            process.StartInfo.Environment["ProgramFiles"] = Path.Combine(harnessRoot, "program-files");
            process.StartInfo.Environment["ProgramData"] = programData;
            process.StartInfo.Environment["NETRATEL_TEST_SERVICE_PATH"] = $"\"{Path.Combine(installRoot, "NetRatel.Client.exe")}\"";
            if (processLogDirectory is null)
                process.StartInfo.Environment.Remove("NetRatel_LOG_DIR");
            else
                process.StartInfo.Environment["NetRatel_LOG_DIR"] = processLogDirectory;
            if (serviceLogDirectory is null)
                process.StartInfo.Environment.Remove("NETRATEL_TEST_SERVICE_ENVIRONMENT");
            else
                process.StartInfo.Environment["NETRATEL_TEST_SERVICE_ENVIRONMENT"] = $"NetRatel_CLIENT_LOG_DIR={serviceLogDirectory}";

            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                throw new TimeoutException("Generated Windows log-directory resolver did not finish within 20 seconds.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.True(process.ExitCode == 0, "Generated Windows log-directory resolver failed: " + stderr);
            const string marker = "NETRATEL_SELECTED_LOG_DIR=";
            var selectedLine = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal));
            Assert.NotNull(selectedLine);
            return selectedLine[marker.Length..];
        }
        finally
        {
            Directory.Delete(harnessRoot, recursive: true);
        }
    }

    private static string PowerShellLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static Dictionary<string, XElement> ReadPlistDictionary(XElement element)
    {
        var entries = element.Elements().ToArray();
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Length; index += 2)
        {
            entries[index].Name.LocalName.Should().Be("key");
            result.Add(entries[index].Value, entries[index + 1]);
        }

        return result;
    }

    private static void AssertBashSyntax(string script)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Bash syntax validation requires a Unix host.");
            return;
        }
        var start = new ProcessStartInfo("bash") { RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
    }

    private static void SetUnixExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }

        Assert.Skip("Unix executable permissions require a Unix host.");
    }

    private static void CreateUnixArtifact(string path, string version, string runtimeId)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("netratel-client-manifest.json").Open()))
            writer.Write(JsonSerializer.Serialize(new
            {
                schema = "netratel.client.manifest.v1",
                product = "NetRatel.Client",
                version,
                runtimeId,
                executable = "NetRatel.Client",
                commitSha = new string('a', 40)
            }));
        using (var writer = new StreamWriter(archive.CreateEntry("NetRatel.Client").Open()))
            writer.Write("#!/usr/bin/env bash\nexit 0\n");
        using var updater = new StreamWriter(archive.CreateEntry("updater/netratel-update.sh").Open());
        updater.Write("#!/usr/bin/env bash\nexit 0\n");
    }
}
