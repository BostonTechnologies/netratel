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
    [Theory]
    [InlineData("win-x64", "ps1")]
    [InlineData("win-arm64", "ps1")]
    [InlineData("linux-x64", "sh")]
    [InlineData("osx-x64", "sh")]
    [InlineData("osx-arm64", "sh")]
    public void Build_RendersSupportedPlatformWithoutUnresolvedInputs(string runtimeId, string extension)
    {
        var service = new ScriptTemplateService();
        var script = service.Build(Request(runtimeId));

        service.GetFileExtension(runtimeId).Should().Be(extension);
        script.Should().Contain(runtimeId);
        script.Should().Contain("ENR-OFFLINE-TEST");
        script.Should().Contain("1.2.3");
        script.Should().Contain(new string('a', 64));
        script.Should().Contain("https://api.example.test");
        script.Should().NotMatchRegex("@@[A-Z0-9_]+@@");
        script.Should().Contain("/onboarding-download");
        script.Should().Contain("X-NetRatel-Enrollment-Code");
        script.Should().ContainEquivalentOf("X-NetRatel-Artifact-Sha256");
        script.Should().Contain("netratel-client-manifest.json");
        AssertNoRetiredDefaults(script);
    }

    [Theory]
    [InlineData("linux-x64", true)]
    [InlineData("linux-x64", false)]
    [InlineData("osx-x64", true)]
    [InlineData("osx-x64", false)]
    [InlineData("osx-arm64", true)]
    [InlineData("osx-arm64", false)]
    public async Task Build_UnixOutputsHaveValidBashSyntax(string runtimeId, bool installAsService)
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Bash parsing requires a Unix host.");
        var script = new ScriptTemplateService().Build(Request(runtimeId) with { InstallAsService = installAsService });
        var result = await RunBashAsync(script, syntaxOnly: true);
        result.ExitCode.Should().Be(0, result.Error);
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("osx-arm64")]
    public async Task Build_UnixEnrollmentInputRemainsLiteralWithoutShellExecution(string runtimeId)
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Bash literal evaluation requires a Unix host.");
        const string input = "ENR-@@ARTIFACT_VERSION@@'\";$(printf SHOULD-NOT-RUN)`printf ALSO-NOT-RUN`\\value";
        var script = new ScriptTemplateService().Build(Request(runtimeId) with { EnrollmentCode = input });
        var assignment = script.Split('\n').Single(line => line.StartsWith("ENROLLMENT_CODE=", StringComparison.Ordinal));

        var result = await RunBashAsync(assignment + "\nprintf '%s' \"$ENROLLMENT_CODE\"");

        result.ExitCode.Should().Be(0, result.Error);
        result.Output.Should().Be(input);
    }

    [Fact]
    public void Build_WindowsEnrollmentInputUsesAnInertPowerShellLiteral()
    {
        const string input = "ENR-@@ARTIFACT_VERSION@@'\";$(Write-Output SHOULD-NOT-RUN)`value\\path";
        var script = new ScriptTemplateService().Build(Request("win-x64") with { EnrollmentCode = input });
        script.Should().Contain("$EnrollmentCode = '" + input.Replace("'", "''", StringComparison.Ordinal) + "'");
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    [InlineData("osx-arm64")]
    public void Build_NormalizesApiAndPreservesOnlyExplicitGatewayInput(string runtimeId)
    {
        var script = new ScriptTemplateService().Build(Request(runtimeId) with
        {
            ApiBaseUrl = "https://api.example.test/api/",
            GatewayEndpoint = "https://gateway.example.test/"
        });

        script.Should().Contain("https://api.example.test");
        script.Should().NotContain("https://api.example.test/api/");
        script.Should().Contain("https://gateway.example.test");
        script.Should().NotContain("https://gateway.example.test/");
    }

    [Theory]
    [InlineData("freebsd-x64")]
    [InlineData("windows")]
    [InlineData("win-x64; Write-Output unsafe")]
    public void Build_RejectsUnsupportedRuntime(string runtimeId)
    {
        var build = () => new ScriptTemplateService().Build(Request(runtimeId));
        build.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0, "ENR-TEST")]
    [InlineData(-1, "ENR-TEST")]
    [InlineData(21, "")]
    [InlineData(21, "ENR-TEST\nHeader: injected")]
    [InlineData(21, "ENR-TEST\rHeader: injected")]
    public void Build_RejectsInvalidTenantOrEnrollmentHeader(int tenantId, string enrollmentCode)
    {
        var build = () => new ScriptTemplateService().Build(Request("win-x64") with
        {
            TenantId = tenantId, EnrollmentCode = enrollmentCode
        });
        build.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("../1.2.3", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("1.2.3; echo unsafe", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("1.2.3", "abc123")]
    [InlineData("1.2.3", "gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Build_RejectsInvalidVersionAndIntegrityInputs(string version, string sha256)
    {
        var build = () => new ScriptTemplateService().Build(Request("linux-x64") with
        {
            ArtifactVersion = version, ArtifactSha256 = sha256
        });
        build.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    [InlineData("osx-arm64")]
    public void Build_PreservesPrereleaseAndBuildMetadataInTheImmutableVersion(string runtimeId)
    {
        var script = new ScriptTemplateService().Build(Request(runtimeId) with
        {
            ArtifactVersion = "1.2.3-rc.1+build.7"
        });

        script.Should().Contain("'1.2.3-rc.1+build.7'");
    }

    [Theory]
    [InlineData("https://user:password@api.example.test", null)]
    [InlineData("https://api.example.test/base", null)]
    [InlineData("https://api.example.test?grant=secret", null)]
    [InlineData("https://api.example.test", "http://gateway.example.test")]
    [InlineData("https://api.example.test", "https://gateway.example.test/netratel.gateway.v1.")]
    public void Build_RejectsUnsafeOrAmbiguousEndpointInputs(string api, string? gateway)
    {
        var build = () => new ScriptTemplateService().Build(Request("win-x64") with
        {
            ApiBaseUrl = api, GatewayEndpoint = gateway
        });
        build.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_WindowsReportsLocalInstallationSeparatelyFromOnlineState(bool installAsService)
    {
        var script = new ScriptTemplateService().Build(Request("win-x64") with { InstallAsService = installAsService });

        script.Should().NotContain("netratel.install-readiness.request.v1");
        script.Should().NotContain("heartbeat_ready");
        script.Should().NotContain("BoundedServiceControlRunner");
        script.Should().NotContain("fully enrolled and online");
        script.Should().Contain("unverified");
    }

    private static DeploymentScriptTemplateRequest Request(string runtimeId) =>
        new(21, runtimeId, "ENR-OFFLINE-TEST", "https://api.example.test", DateTimeOffset.UtcNow.AddHours(1),
            true, true, "1.2.3", new string('a', 64));

    private static void AssertNoRetiredDefaults(string script)
    {
        foreach (var setting in new[]
        {
            "Transport__Mode=AkkaPresence", "Gateway__RequiredPresenceAuthority=akka",
            "Gateway__TelemetryShadowEnabled=true", "Gateway__TelemetryAuthorityEnabled=true",
            "Gateway__CommandAuthorityEnabled=true", "Gateway__JobAuthorityEnabled=true",
            "Gateway__TerminalAuthorityEnabled=true", "Gateway__ControlGatewayEnabled=true",
            "Gateway__FileGatewayEnabled=true", "Gateway__LogGatewayEnabled=true",
            "Gateway__RemoteSupportGatewayEnabled=true", "Gateway__TerminalGatewayEnabled=true"
        }) script.Should().NotContain("NetRatelCLIENT__" + setting);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunBashAsync(string script, bool syntaxOnly = false)
    {
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        if (syntaxOnly) start.ArgumentList.Add("-n");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
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
            AssertNoRetiredDefaults(script);
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
