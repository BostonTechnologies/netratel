using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NetRatel.Application.Artifacts;
using NetRatel.Client;
using NetRatel.Infrastructure.Artifacts;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class LinuxInstallerTransactionTests
{
    private const string Version = "1.2.3";
    private const string RuntimeId = "linux-x64";
    private const string ApiBase = "https://netratel.example.invalid";

    [Theory]
    [InlineData("stop_failure", "did not stop", 1)]
    [InlineData("query_failure", "service state could not be verified", 0)]
    [SupportedOSPlatform("linux")]
    public async Task Build_Bash_LeavesOwnedInstallationUntouchedWhenServiceStopCannotBeVerified(
        string failureCase,
        string expectedDiagnostic,
        int expectedStopCalls)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The generated systemd transaction test requires Linux.");

        using var fixture = LinuxInstallerFixture.Create(failureCase);
        var before = fixture.CaptureInstalledState();

        var result = await fixture.RunInstallerAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedDiagnostic, $"{result.StandardOutput}\n{result.StandardError}", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedStopCalls, fixture.CountSystemctlCalls("stop netratel-client.service"));
        fixture.AssertInstalledStateEquals(before);
        Assert.True(fixture.ClientServiceIsActive);
        if (expectedStopCalls == 0)
        {
            Assert.False(fixture.FirstStopSnapshotExists, "an unavailable service query must fail before any stop attempt");
        }
        else
        {
            fixture.AssertInstalledFilesUnchangedAtFirstStop(before);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Build_Bash_ReplacesSameVersionAndPreservesInstalledIdentityConfiguration()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The generated systemd transaction test requires Linux.");

        using var fixture = LinuxInstallerFixture.Create("successful_replacement");
        var oldExecutable = await File.ReadAllBytesAsync(fixture.OldExecutablePath);
        var before = fixture.CaptureInstalledState();
        var result = await fixture.RunInstallerAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"Resolved version: {Version}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Enrollment and online gateway state remain unverified", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(1, fixture.CountSystemctlCalls("stop netratel-client.service"));
        Assert.True(fixture.CountSystemctlCalls("start netratel-client.service") >= 1);
        Assert.True(fixture.ClientServiceIsActive);
        AssertCurrentLinkTargets(fixture.CurrentLinkPath, fixture.OldVersionDirectory);
        fixture.AssertInstalledFilesUnchangedAtFirstStop(before);

        Assert.NotEqual(oldExecutable, await File.ReadAllBytesAsync(fixture.OldExecutablePath));
        using var installedManifest = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.OldManifestPath));
        Assert.Equal(Version, installedManifest.RootElement.GetProperty("version").GetString());
        Assert.Equal(new string('b', 40), installedManifest.RootElement.GetProperty("commitSha").GetString());

        using var installedSettings = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.OldSettingsPath));
        Assert.Equal("fixture-existing-agent", installedSettings.RootElement.GetProperty("AgentId").GetString());
        Assert.Equal("fixture-device-key", installedSettings.RootElement.GetProperty("DeviceKey").GetString());
        Assert.Equal(ApiBase, installedSettings.RootElement.GetProperty("Client").GetProperty("ApiBaseUrl").GetString());

        var installedUnit = await File.ReadAllTextAsync(fixture.ClientUnitPath);
        Assert.Contains($"WorkingDirectory=\"{fixture.InstallRoot}/current\"", installedUnit, StringComparison.Ordinal);
        Assert.Contains($"ExecStart=\"{fixture.LauncherPath}\"", installedUnit, StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.LauncherPath));
        Assert.True((File.GetUnixFileMode(fixture.LauncherPath) & UnixFileMode.UserExecute) != 0);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Build_Bash_RestoresExactOwnedInstallationWhenCandidateServiceFailsToStart()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The generated systemd transaction test requires Linux.");

        using var fixture = LinuxInstallerFixture.Create("activation_failure");
        var before = fixture.CaptureInstalledState();

        var result = await fixture.RunInstallerAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("previous installation was restored", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture.CountSystemctlCalls("stop netratel-client.service"));
        Assert.True(fixture.CountSystemctlCalls("start netratel-client.service") >= 2);
        Assert.True(fixture.ClientServiceIsActive, "rollback must restart the previously active service");
        fixture.AssertInstalledStateEquals(before);
        fixture.AssertInstalledFilesUnchangedAtFirstStop(before);
    }

    [Theory]
    [InlineData("supported_environment_file")]
    [InlineData("supported_dropin")]
    [SupportedOSPlatform("linux")]
    public async Task Build_Bash_PreservesSupportedServiceCustomization(string customization)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The generated systemd transaction test requires Linux.");
        using var fixture = LinuxInstallerFixture.Create(customization);
        var customizationBytes = File.ReadAllBytes(fixture.CustomizationPath);
        var result = await fixture.RunInstallerAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(customizationBytes, File.ReadAllBytes(fixture.CustomizationPath));
        Assert.Contains("Custom__Value=kept value", await File.ReadAllTextAsync(fixture.ClientUnitPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("conflicting_environment_file", "requested endpoint conflicts")]
    [InlineData("conflicting_dropin_identity", "service identity is not root")]
    [InlineData("unsafe_archive", "unsafe path")]
    [InlineData("wrong_checksum", "SHA-256 verification")]
    [SupportedOSPlatform("linux")]
    public async Task Build_Bash_RejectsUnsafeOrUnsupportedInputBeforeStoppingService(string failureCase, string diagnostic)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The generated systemd transaction test requires Linux.");
        using var fixture = LinuxInstallerFixture.Create(failureCase);
        var before = fixture.CaptureInstalledState();
        var result = await fixture.RunInstallerAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(diagnostic, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.CountSystemctlCalls("stop netratel-client.service"));
        fixture.AssertInstalledStateEquals(before);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Build_Bash_PreservesInstalledCustomUpdaterPathsAndPolicy()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The generated systemd transaction test requires Linux.");
        using var fixture = LinuxInstallerFixture.Create("installed_update_paths");
        var result = await fixture.RunInstallerAsync();
        Assert.Equal(0, result.ExitCode);
        var clientUnit = await File.ReadAllTextAsync(fixture.ClientUnitPath);
        var updaterUnit = await File.ReadAllTextAsync(fixture.UpdateUnitPath);
        Assert.Contains($"NetRatelCLIENT__Client__AutoUpdate__RequestPath={fixture.StateDirectory}/custom-request.json", clientUnit, StringComparison.Ordinal);
        Assert.Contains($"NetRatelCLIENT__Client__AutoUpdate__ReadyPath={fixture.StateDirectory}/custom-ready.json", clientUnit, StringComparison.Ordinal);
        Assert.Contains("NetRatelCLIENT__Client__AutoUpdate__Mode=Disabled", clientUnit, StringComparison.Ordinal);
        Assert.Contains($"NetRatel_UPDATE_REQUEST={fixture.StateDirectory}/custom-request.json", updaterUnit, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.StateDirectory, "update.lock")));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Build_Bash_RepairsUserModeSameVersionWithoutAServiceOrCurrentPointer()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The generated user installer transaction test requires Linux.");
        using var fixture = LinuxInstallerFixture.Create("user_same_version");
        var result = await fixture.RunInstallerAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Online gateway state remains unverified", result.StandardOutput, StringComparison.Ordinal);
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.OldSettingsPath));
        Assert.Equal("fixture-existing-agent", settings.RootElement.GetProperty("AgentId").GetString());
        Assert.False(File.Exists(fixture.ClientUnitPath));
        Assert.False(Directory.Exists(fixture.CurrentLinkPath));
        Assert.Equal(0, fixture.CountSystemctlCalls("stop"));
    }

    [Theory]
    [InlineData("https://requested-gateway.example.invalid")]
    [InlineData(ApiBase)]
    [SupportedOSPlatform("linux")]
    public async Task Explicit_gateway_is_written_to_settings_and_systemd_environment(string gateway)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The generated systemd transaction test requires Linux.");
        using var fixture = LinuxInstallerFixture.Create("successful_replacement", gateway);
        var result = await fixture.RunInstallerAsync();
        Assert.Equal(0, result.ExitCode);
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.OldSettingsPath));
        Assert.Equal(gateway, settings.RootElement.GetProperty("Gateway").GetProperty("Endpoint").GetString());
        Assert.Contains($"NetRatelCLIENT__Gateway__Endpoint={gateway}", await File.ReadAllTextAsync(fixture.ClientUnitPath));
        var configuration = new ConfigurationBuilder().AddJsonFile(fixture.OldSettingsPath).Build();
        var loaded = ClientConfigurationLoader.LoadGatewayOptions(configuration, ApiBase, null, null, fixture.OldVersionDirectory, []);
        Assert.Equal(gateway, loaded.Endpoint);
    }

    [Theory]
    [InlineData("flat_settings_with_api")]
    [InlineData("flat_settings_without_api")]
    [InlineData("flat_settings_empty_client")]
    [SupportedOSPlatform("linux")]
    public async Task Build_Bash_PreservesFlatSettingsAsAnEffectiveRuntimeLayer(string configurationCase)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The generated systemd transaction test requires Linux.");
        using var fixture = LinuxInstallerFixture.Create(configurationCase);
        var result = await fixture.RunInstallerAsync();
        Assert.Equal(0, result.ExitCode);

        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.OldSettingsPath));
        if (configurationCase == "flat_settings_empty_client")
            Assert.Empty(settings.RootElement.GetProperty("Client").EnumerateObject());
        else
            Assert.False(settings.RootElement.TryGetProperty("Client", out _));
        Assert.Equal(ApiBase, settings.RootElement.GetProperty("ApiBaseUrl").GetString());
        Assert.Equal("https://gateway.example.invalid", settings.RootElement.GetProperty("Gateway").GetProperty("Endpoint").GetString());

        var loaded = ClientConfigurationLoader.Load(configuration: null, fixture.OldVersionDirectory, Array.Empty<string>());
        Assert.Equal(ApiBase, loaded.ApiBaseUrl);
        Assert.Equal(3210, loaded.TerminalGracefulExitTimeoutMs);
        Assert.False(loaded.EnableNativeUnixPty);
        Assert.Equal("fixture-existing-agent", loaded.AgentId);
        Assert.Equal("Disabled", loaded.AutoUpdate.Mode);
        Assert.Equal("Prerelease", loaded.AutoUpdate.Channel);
        Assert.Equal(fixture.StateDirectory, loaded.AutoUpdate.StateDirectory);
        Assert.Equal(Path.Combine(fixture.StateDirectory, "flat-request.json"), loaded.AutoUpdate.RequestPath);
        Assert.Equal(Path.Combine(fixture.StateDirectory, "flat-ready.json"), loaded.AutoUpdate.ReadyPath);
    }

    private static void AssertCurrentLinkTargets(string currentLinkPath, string expectedTarget)
    {
        var resolved = new DirectoryInfo(currentLinkPath).ResolveLinkTarget(returnFinalTarget: true);
        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(expectedTarget), Path.GetFullPath(resolved!.FullName));
    }

    [SupportedOSPlatform("linux")]
    private sealed class LinuxInstallerFixture : IDisposable
    {
        private const UnixFileMode PrivateDirectoryMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        private const UnixFileMode ExecutableFileMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        private readonly string _root;
        private readonly string _systemctlCallsPath;
        private readonly string _activeServiceMarkerPath;
        private readonly string _preStopHashesPath;
        private readonly string _preStopLinkPath;
        private readonly string _artifactPath;
        private readonly string _failureCase;
        private readonly byte[] _candidateExecutable;
        private bool _disposed;

        private LinuxInstallerFixture(string failureCase, string? gateway = null)
        {
            _root = Path.Combine(Path.GetTempPath(), $"netratel-linux-transaction-{Guid.NewGuid():N}");
            _failureCase = failureCase;
            try
            {
                CreatePrivateDirectory(_root);
                BinDirectory = Path.Combine(_root, "bin");
                InstallRoot = Path.Combine(_root, "client");
                StateDirectory = Path.Combine(_root, "state");
                UnitDirectory = Path.Combine(_root, "units");
                OldVersionDirectory = Path.Combine(InstallRoot, "versions", Version);
                OldExecutablePath = Path.Combine(OldVersionDirectory, "NetRatel.Client");
                OldManifestPath = Path.Combine(OldVersionDirectory, "netratel-client-manifest.json");
                OldSettingsPath = Path.Combine(OldVersionDirectory, "clientsettings.json");
                CurrentLinkPath = Path.Combine(InstallRoot, "current");
                LauncherPath = Path.Combine(InstallRoot, "netratel-client-start.sh");
                UpdaterPath = Path.Combine(InstallRoot, "updater", "netratel-update.sh");
                ClientUnitPath = Path.Combine(UnitDirectory, "netratel-client.service");
                UpdateUnitPath = Path.Combine(UnitDirectory, "netratel-update.service");
                _systemctlCallsPath = Path.Combine(_root, "systemctl.calls");
                _activeServiceMarkerPath = Path.Combine(_root, "client-service.active");
                _preStopHashesPath = Path.Combine(_root, "pre-stop-hashes.txt");
                _preStopLinkPath = Path.Combine(_root, "pre-stop-link.txt");
                _artifactPath = Path.Combine(_root, "package.zip");
                ScriptPath = Path.Combine(_root, "install.sh");
                _candidateExecutable = Encoding.UTF8.GetBytes("#!/usr/bin/env bash\nexit 0\n");

                foreach (var directory in new[]
                {
                    BinDirectory,
                    InstallRoot,
                    Path.Combine(InstallRoot, "versions"),
                    OldVersionDirectory,
                    Path.Combine(InstallRoot, "updater"),
                    StateDirectory,
                    UnitDirectory
                })
                {
                    CreatePrivateDirectory(directory);
                }

                WritePrivateFile(OldExecutablePath, Encoding.UTF8.GetBytes("#!/usr/bin/env bash\n# previous owned executable\nexit 0\n"), ExecutableFileMode);
                WritePrivateFile(OldManifestPath, CreateManifest('a'), PrivateFileMode);
                WritePrivateFile(OldSettingsPath, Encoding.UTF8.GetBytes($$"""
                    {
                      "Client": { "ApiBaseUrl": "{{ApiBase}}" },
                      "AgentId": "fixture-existing-agent",
                      "DeviceKey": "fixture-device-key"
                    }
                    """), PrivateFileMode);
                Directory.CreateSymbolicLink(CurrentLinkPath, OldVersionDirectory);

                WritePrivateFile(LauncherPath, Encoding.UTF8.GetBytes($"#!/usr/bin/env bash\nROOT_DIR=\"{InstallRoot}\"\nexec \"{InstallRoot}/current/NetRatel.Client\" --service\n"), ExecutableFileMode);
                WritePrivateFile(UpdaterPath, Encoding.UTF8.GetBytes("#!/usr/bin/env bash\n# previous updater\nexit 0\n"), ExecutableFileMode);
                WritePrivateFile(ClientUnitPath, Encoding.UTF8.GetBytes(string.Join("\n", new[]
                {
                    "[Unit]",
                    "Description=Previous NetRatel Client",
                    "[Service]",
                    $"WorkingDirectory={InstallRoot}/current",
                    $"ExecStart={LauncherPath}",
                    $"Environment=NetRatelCLIENT__Client__ApiBaseUrl={ApiBase}"
                })), PrivateFileMode);
                WritePrivateFile(UpdateUnitPath, Encoding.UTF8.GetBytes(string.Join("\n", new[]
                {
                    "[Unit]",
                    "Description=Previous NetRatel Client updater",
                    "[Service]",
                    $"ExecStart={UpdaterPath}"
                })), PrivateFileMode);
                if (failureCase == "installed_update_paths")
                {
                    WritePrivateFile(OldSettingsPath, JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        Client = new
                        {
                            ApiBaseUrl = ApiBase,
                            AutoUpdate = new
                            {
                                Mode = "Disabled", StateDirectory,
                                RequestPath = Path.Combine(StateDirectory, "custom-request.json"),
                                ReadyPath = Path.Combine(StateDirectory, "custom-ready.json")
                            }
                        }
                    }), PrivateFileMode);
                }
                if (failureCase == "user_same_version")
                {
                    File.Delete(ClientUnitPath);
                    File.Delete(UpdateUnitPath);
                    Directory.Delete(CurrentLinkPath);
                }
                if (failureCase.StartsWith("flat_settings_", StringComparison.Ordinal))
                {
                    var settings = new Dictionary<string, object>
                    {
                        ["AgentId"] = "fixture-existing-agent",
                        ["TerminalGracefulExitTimeoutMs"] = 3210,
                        ["EnableNativeUnixPty"] = false,
                        ["AutoUpdate"] = new
                        {
                            Mode = "Disabled", Channel = "Prerelease", StateDirectory,
                            RequestPath = Path.Combine(StateDirectory, "flat-request.json"),
                            ReadyPath = Path.Combine(StateDirectory, "flat-ready.json")
                        },
                        ["Gateway"] = new { Endpoint = "https://gateway.example.invalid" }
                    };
                    if (failureCase == "flat_settings_with_api") settings["ApiBaseUrl"] = "https://previous.example.invalid";
                    if (failureCase == "flat_settings_empty_client") settings["Client"] = new Dictionary<string, object>();
                    WritePrivateFile(OldSettingsPath, JsonSerializer.SerializeToUtf8Bytes(settings), PrivateFileMode);
                }

                CustomizationPath = Path.Combine(_root, "custom.env");
                if (failureCase is "supported_environment_file" or "conflicting_environment_file")
                {
                    File.AppendAllText(ClientUnitPath, $"\nEnvironmentFile={CustomizationPath}\n");
                    var customApi = failureCase == "conflicting_environment_file" ? "https://other.example.invalid" : ApiBase;
                    WritePrivateFile(CustomizationPath, Encoding.UTF8.GetBytes($"Custom__Value='kept value'\nNetRatelCLIENT__Client__ApiBaseUrl={customApi}\n"), PrivateFileMode);
                }
                else if (failureCase is "supported_dropin" or "conflicting_dropin_identity")
                {
                    var dropinDirectory = Path.Combine(UnitDirectory, "netratel-client.service.d");
                    CreatePrivateDirectory(dropinDirectory);
                    CustomizationPath = Path.Combine(dropinDirectory, "custom.conf");
                    WritePrivateFile(CustomizationPath, Encoding.UTF8.GetBytes(failureCase == "supported_dropin"
                        ? "[Service]\nRestartSec=13\nEnvironment=Custom__Value=\"kept value\"\n"
                        : "[Service]\nUser=other-service-user\n"), PrivateFileMode);
                }

                WritePrivateFile(_artifactPath, CreatePackage(_candidateExecutable, failureCase == "unsafe_archive"), PrivateFileMode);
                var artifactBytes = File.ReadAllBytes(_artifactPath);
                var sha = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant();
                var size = artifactBytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var installer = new ScriptTemplateService().Build(new DeploymentScriptTemplateRequest(
                    TenantId: 4098,
                    RuntimeId,
                    EnrollmentCode: "ENR-SYNTHETIC-LINUX-TRANSACTION",
                    ApiBaseUrl: ApiBase,
                    ValidToUtc: DateTimeOffset.UtcNow.AddHours(1),
                    InstallAsService: failureCase != "user_same_version",
                    SilentInstall: true,
                    ArtifactVersion: Version,
                    ArtifactSha256: sha,
                    GatewayEndpoint: gateway));
                WritePrivateFile(ScriptPath, Encoding.UTF8.GetBytes(installer), ExecutableFileMode);

                WritePrivateFile(Path.Combine(BinDirectory, "curl"), Encoding.UTF8.GetBytes(CreateCurlStub()), ExecutableFileMode);
                WritePrivateFile(Path.Combine(BinDirectory, "systemctl"), Encoding.UTF8.GetBytes(CreateSystemctlStub()), ExecutableFileMode);
                WritePrivateFile(Path.Combine(BinDirectory, "journalctl"), Encoding.UTF8.GetBytes("#!/usr/bin/env bash\nexit 0\n"), ExecutableFileMode);
                WritePrivateFile(_activeServiceMarkerPath, Encoding.UTF8.GetBytes("active"), PrivateFileMode);
            }
            catch
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                throw;
            }
        }

        public string BinDirectory { get; }
        public string InstallRoot { get; }
        public string StateDirectory { get; }
        public string UnitDirectory { get; }
        public string OldVersionDirectory { get; }
        public string OldExecutablePath { get; }
        public string OldManifestPath { get; }
        public string OldSettingsPath { get; }
        public string CurrentLinkPath { get; }
        public string LauncherPath { get; }
        public string UpdaterPath { get; }
        public string ClientUnitPath { get; }
        public string UpdateUnitPath { get; }
        public string ScriptPath { get; }
        public string CustomizationPath { get; }
        public bool ClientServiceIsActive => File.Exists(_activeServiceMarkerPath);
        public bool FirstStopSnapshotExists => File.Exists(_preStopHashesPath);

        public static LinuxInstallerFixture Create(string failureCase, string? gateway = null)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The installer transaction fixture requires Linux.");
            return new LinuxInstallerFixture(failureCase, gateway);
        }

        public async Task<ProcessResult> RunInstallerAsync()
        {
            var startInfo = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(ScriptPath);
            startInfo.Environment["PATH"] = $"{BinDirectory}:/usr/bin:/bin";
            startInfo.Environment["NetRatel_ROOT"] = InstallRoot;
            startInfo.Environment["NetRatel_STATE"] = StateDirectory;
            startInfo.Environment["NetRatel_SYSTEMD_UNIT_DIR"] = UnitDirectory;
            startInfo.Environment["NetRatel_TEST_ALLOW_NONROOT"] = "true";
            startInfo.Environment["FIXTURE_ARCHIVE"] = _artifactPath;
            startInfo.Environment["FIXTURE_SHA"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_artifactPath))).ToLowerInvariant();
            startInfo.Environment["FIXTURE_SIZE"] = new FileInfo(_artifactPath).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["FIXTURE_CALLS"] = _systemctlCallsPath;
            startInfo.Environment["FIXTURE_UNIT"] = ClientUnitPath;
            startInfo.Environment["FIXTURE_CASE"] = _failureCase;
            startInfo.Environment["FIXTURE_DROPIN"] = _failureCase.Contains("dropin", StringComparison.Ordinal) ? CustomizationPath : "";
            startInfo.Environment["FIXTURE_ACTIVE"] = _activeServiceMarkerPath;
            startInfo.Environment["FIXTURE_OLD_EXECUTABLE"] = OldExecutablePath;
            startInfo.Environment["FIXTURE_OLD_MANIFEST"] = OldManifestPath;
            startInfo.Environment["FIXTURE_OLD_SETTINGS"] = OldSettingsPath;
            startInfo.Environment["FIXTURE_OLD_LAUNCHER"] = LauncherPath;
            startInfo.Environment["FIXTURE_OLD_UPDATER"] = UpdaterPath;
            startInfo.Environment["FIXTURE_OLD_CLIENT_UNIT"] = ClientUnitPath;
            startInfo.Environment["FIXTURE_OLD_UPDATE_UNIT"] = UpdateUnitPath;
            startInfo.Environment["FIXTURE_CURRENT_LINK"] = CurrentLinkPath;
            startInfo.Environment["FIXTURE_PRESTOP_HASHES"] = _preStopHashesPath;
            startInfo.Environment["FIXTURE_PRESTOP_LINK"] = _preStopLinkPath;
            startInfo.Environment["FIXTURE_START_FAILED"] = Path.Combine(_root, "candidate-start-failed");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the generated Linux installer.");
            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
                throw new TimeoutException("The generated Linux installer did not finish within 30 seconds.");
            }

            return new ProcessResult(
                process.ExitCode,
                await standardOutputTask.ConfigureAwait(false),
                await standardErrorTask.ConfigureAwait(false));
        }

        public InstalledStateSnapshot CaptureInstalledState()
        {
            var files = new[] { OldExecutablePath, OldManifestPath, OldSettingsPath, LauncherPath, UpdaterPath, ClientUnitPath, UpdateUnitPath }
                .ToDictionary(path => path, CaptureFile, StringComparer.Ordinal);
            return new InstalledStateSnapshot(files, new DirectoryInfo(CurrentLinkPath).LinkTarget);
        }

        public void AssertInstalledStateEquals(InstalledStateSnapshot expected)
        {
            foreach (var (path, expectedFile) in expected.Files)
            {
                var actual = CaptureFile(path);
                Assert.Equal(expectedFile.Exists, actual.Exists);
                if (!expectedFile.Exists) continue;
                Assert.Equal(expectedFile.Bytes, actual.Bytes);
                Assert.Equal(expectedFile.Mode, actual.Mode);
            }

            Assert.Equal(expected.CurrentLinkTarget, new DirectoryInfo(CurrentLinkPath).LinkTarget);
        }

        public void AssertInstalledFilesUnchangedAtFirstStop(InstalledStateSnapshot expected)
        {
            Assert.True(File.Exists(_preStopHashesPath), "the owned service stop must capture the installed files before cutover");
            var paths = new[] { OldExecutablePath, OldManifestPath, OldSettingsPath, LauncherPath, UpdaterPath, ClientUnitPath, UpdateUnitPath };
            var expectedHashes = string.Join("\n", paths.Select(path =>
            {
                var bytes = expected.Files[path].Bytes;
                Assert.NotNull(bytes);
                var hash = Convert.ToHexString(SHA256.HashData(bytes!)).ToLowerInvariant();
                return $"{hash}  {path}";
            })) + "\n";
            Assert.Equal(expectedHashes, File.ReadAllText(_preStopHashesPath));
            Assert.Equal(expected.CurrentLinkTarget, File.ReadAllText(_preStopLinkPath).TrimEnd('\r', '\n'));
        }

        public int CountSystemctlCalls(string callPrefix)
        {
            if (!File.Exists(_systemctlCallsPath)) return 0;
            return File.ReadAllLines(_systemctlCallsPath)
                .Count(line => line.StartsWith(callPrefix, StringComparison.Ordinal));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static InstalledFileSnapshot CaptureFile(string path)
        {
            if (!File.Exists(path)) return new InstalledFileSnapshot(false, null, null);
            return new InstalledFileSnapshot(true, File.ReadAllBytes(path), File.GetUnixFileMode(path));
        }

        private static void CreatePrivateDirectory(string path)
        {
            Directory.CreateDirectory(path);
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }

        private static void WritePrivateFile(string path, byte[] contents, UnixFileMode mode)
        {
            File.WriteAllBytes(path, contents);
            File.SetUnixFileMode(path, mode);
        }

        private static byte[] CreateManifest(char commitCharacter) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "netratel.client.manifest.v1",
            product = "NetRatel.Client",
            version = Version,
            runtimeId = RuntimeId,
            executable = "NetRatel.Client",
            commitSha = new string(commitCharacter, 40)
        });

        private static byte[] CreatePackage(byte[] executableBytes, bool unsafePath)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteZipEntry(archive, "netratel-client-manifest.json", CreateManifest('b'));
                WriteZipEntry(archive, "NetRatel.Client", executableBytes);
                WriteZipEntry(archive, "updater/netratel-update.sh", Encoding.UTF8.GetBytes("#!/usr/bin/env bash\n# candidate updater\nexit 0\n"));
                if (unsafePath) WriteZipEntry(archive, "../outside", Encoding.UTF8.GetBytes("outside"));
            }
            return output.ToArray();
        }

        private static void WriteZipEntry(ZipArchive archive, string name, byte[] contents)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(contents);
        }

        private static string CreateCurlStub() => """
            #!/usr/bin/env bash
            set -euo pipefail
            umask 077
            headers=""
            output=""
            while [ "$#" -gt 0 ]; do
              case "$1" in
                -D) headers="$2"; shift 2;;
                -o) output="$2"; shift 2;;
                *) shift;;
              esac
            done
            cp -- "$FIXTURE_ARCHIVE" "$output"
            if [ "$FIXTURE_CASE" = "wrong_checksum" ]; then printf 'different bytes' >> "$output"; fi
            printf 'HTTP/1.1 200 OK\r\nX-NetRatel-Artifact-Rid: linux-x64\r\nX-NetRatel-Artifact-Version: 1.2.3\r\nX-NetRatel-Artifact-Sha256: %s\r\nX-NetRatel-Artifact-Size: %s\r\n\r\n' "$FIXTURE_SHA" "$FIXTURE_SIZE" > "$headers"
            """;

        private static string CreateSystemctlStub() => """
            #!/usr/bin/env bash
            set -euo pipefail
            umask 077
            printf '%s\n' "$*" >> "$FIXTURE_CALLS"
            case "$1" in
              show)
                shift
                property=""
                unit=""
                while [ "$#" -gt 0 ]; do
                  case "$1" in
                    -p) property="$2"; shift 2;;
                    -p*) property="${1#-p}"; shift;;
                    --property) property="$2"; shift 2;;
                    --property=*) property="${1#*=}"; shift;;
                    --value) shift;;
                    *) unit="$1"; shift;;
                  esac
                done
                case "$unit" in
                  netratel-client.service|netratel-update.service) ;;
                  *) exit 4;;
                esac
                case "$property" in
                  FragmentPath)
                    if [ "$unit" = "netratel-client.service" ]; then printf '%s\n' "$FIXTURE_UNIT"; else printf '%s\n' "$FIXTURE_OLD_UPDATE_UNIT"; fi;;
                  LoadState) printf 'loaded\n';;
                  MainPID)
                    [ "$unit" = "netratel-client.service" ] || exit 2
                    if [ -f "$FIXTURE_ACTIVE" ]; then printf '123\n'; else printf '0\n'; fi;;
                  DropInPaths)
                    if [ "$unit" = "netratel-client.service" ]; then printf '%s\n' "$FIXTURE_DROPIN"; fi;;
                  *) exit 2;;
                esac;;
              is-active)
                if [ "$FIXTURE_CASE" = "query_failure" ]; then exit 42; fi
                if [ -f "$FIXTURE_ACTIVE" ]; then exit 0; else exit 3; fi;;
              is-enabled) exit 0;;
              stop)
                if [ "$2" = "netratel-client.service" ] && [ ! -f "$FIXTURE_PRESTOP_HASHES" ]; then
                  sha256sum -- \
                    "$FIXTURE_OLD_EXECUTABLE" "$FIXTURE_OLD_MANIFEST" "$FIXTURE_OLD_SETTINGS" \
                    "$FIXTURE_OLD_LAUNCHER" "$FIXTURE_OLD_UPDATER" \
                    "$FIXTURE_OLD_CLIENT_UNIT" "$FIXTURE_OLD_UPDATE_UNIT" > "$FIXTURE_PRESTOP_HASHES"
                  readlink -- "$FIXTURE_CURRENT_LINK" > "$FIXTURE_PRESTOP_LINK"
                fi
                if [ "$FIXTURE_CASE" = "stop_failure" ]; then exit 1; fi
                rm -f -- "$FIXTURE_ACTIVE";;
              start)
                if [ "$FIXTURE_CASE" = "activation_failure" ] && [ ! -f "$FIXTURE_START_FAILED" ]; then
                  : > "$FIXTURE_START_FAILED"
                  exit 1
                fi
                : > "$FIXTURE_ACTIVE";;
              cat) exit 4;;
              *) exit 0;;
            esac
            """;
    }

    private sealed record InstalledStateSnapshot(
        IReadOnlyDictionary<string, InstalledFileSnapshot> Files,
        string? CurrentLinkTarget);

    [SupportedOSPlatform("linux")]
    private sealed record InstalledFileSnapshot(bool Exists, byte[]? Bytes, UnixFileMode? Mode);
    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
