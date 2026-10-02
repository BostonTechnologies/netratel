using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NetRatel.API.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentUpdateScriptSeedServiceTests
{
    [Fact]
    public void Seeded_installers_share_verified_artifact_rendering_without_installer_receipts()
    {
        foreach (var field in new[] { "WindowsScript", "LinuxScript" })
        {
            var script = GetSeedScript(field);
            script.Should().Contain("onboarding-download");
            script.Should().Contain("X-NetRatel-Enrollment-Code");
            script.Should().Contain("X-NetRatel-Tenant-Id");
            script.Should().Contain("netratel.client.manifest.v1");
            script.Should().NotContain("synthetic-seed-input");
            script.Should().NotContain("heartbeat_ready");
            script.Should().NotContain("ReadyRecord");
            script.Should().NotContain("NETRATEL_SEED_ENROLLMENT_CODE_PLACEHOLDER");
            script.Should().NotContain("@@SEED_");
        }
        var windows = GetSeedScript("WindowsScript");
        windows.Should().Contain("Set-NetRatelSeedHandoffResult -State 'installed_started'");
        windows.Should().Contain("Complete-NetRatelSeedFailure $_");
        windows.Should().Contain("Remove-NetRatelSeedHandoffFiles");
        windows.Should().NotContain("Initialize-NetRatelSeedHandoff");
        windows.Should().NotContain("Assert-NetRatelTrustedReadinessPath");
        windows.Should().NotContain("New-NetRatelProtectedDirectory");
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

    [Theory]
    [InlineData("https://api.example.invalid", "https://api.example.invalid")]
    [InlineData("https://gateway.example.invalid", "https://gateway.example.invalid")]
    [InlineData("", "")]
    public async Task Windows_seed_keeps_an_explicit_gateway_even_when_it_matches_the_new_api(string gateway, string expected)
    {
        var script = GetSeedScript("WindowsScript");
        var startIndex = script.IndexOf("function Get-NetRatelSeedGatewayEndpoint", StringComparison.Ordinal);
        var endIndex = script.IndexOf("function Wait-NetRatelSeedOriginExit", startIndex, StringComparison.Ordinal);
        var helper = script[startIndex..endIndex];
        var path = Path.Combine(Path.GetTempPath(), "netratel-seed-endpoint-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            await File.WriteAllTextAsync(path, "param([string]$Gateway)\n" + helper +
                "[Console]::Write((Get-NetRatelSeedGatewayEndpoint $Gateway 'https://api.example.invalid'))\n");
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", path, "-Gateway", gateway })
                start.ArgumentList.Add(argument);
            var result = await RunBoundedProcessAsync(start);
            result.ExitCode.Should().Be(0, result.StandardError);
            result.StandardOutput.Should().Be(expected);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("linux")]
    public async Task Linux_seed_preserves_quoted_custom_paths_and_hands_off_private_parameters_without_secret_arguments(bool updaterOnlyState)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The temporary-filesystem handoff probe requires Linux.");
        var fixture = await FixtureAsync(updaterOnlyState);
        try
        {
            var result = await DispatchAsync(fixture);
            result.ExitCode.Should().Be(0, result.StandardError);
            result.StandardOutput.Should().Contain("detached root worker");
            result.StandardOutput.Should().NotContain(fixture.Secret);
            result.StandardError.Should().NotContain(fixture.Secret);
            var handoff = Path.Combine(fixture.State, "install-handoffs");
            File.GetUnixFileMode(handoff).Should().Be((UnixFileMode)0x1C0);
            var requestPath = Directory.EnumerateFiles(handoff, "*.json").Single();
            var request = JsonNode.Parse(await File.ReadAllTextAsync(requestPath))!;
            request["api_base"]!.GetValue<string>().Should().Be("https://api.example.invalid");
            request["root_dir"]!.GetValue<string>().Should().Be(fixture.Client);
            request["enrollment_code"]!.GetValue<string>().Should().Be(fixture.Secret);
            foreach (var file in Directory.EnumerateFiles(handoff))
                (File.GetUnixFileMode(file) & (UnixFileMode)0x1FF).Should().Be((UnixFileMode)0x180);
            var args = await File.ReadAllTextAsync(fixture.Arguments);
            args.Should().Contain("worker");
            args.Should().NotContain(fixture.Secret);
            args.Should().NotContain("--enroll");
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    [Theory]
    [InlineData("installer")]
    [InlineData("unit")]
    [InlineData("identity")]
    [SupportedOSPlatform("linux")]
    public async Task Linux_worker_rejects_changed_handoff_inputs_before_running_installer(string changed)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The temporary-filesystem handoff probe requires Linux.");
        var fixture = await FixtureAsync();
        try
        {
            (await DispatchAsync(fixture)).ExitCode.Should().Be(0);
            var requestPath = Directory.EnumerateFiles(Path.Combine(fixture.State, "install-handoffs"), "*.json").Single();
            var installer = Path.ChangeExtension(requestPath, ".sh");
            if (changed == "installer") await File.AppendAllTextAsync(installer, "\n# tampered\n");
            if (changed == "unit") await File.AppendAllTextAsync(fixture.Unit, "\n# changed\n");
            var start = WorkerStart(fixture, requestPath);
            if (changed == "identity") start.Environment["FAKE_SYSTEMD_PID"] = "2147483647";
            var result = await RunBoundedProcessAsync(start);
            result.ExitCode.Should().Be(70);
            result.StandardError.Should().Contain("Linux update handoff failed:");
            result.StandardError.Should().NotContain(fixture.Secret);
            Directory.EnumerateFiles(Path.GetDirectoryName(requestPath)!).Should().BeEmpty();
            new DirectoryInfo(Path.Combine(fixture.Client, "current")).ResolveLinkTarget(true)!.Name.Should().Be("1.2.3");
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Linux_worker_delivers_secret_by_environment_after_origin_exit_and_cleans_only_attempt_files()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("The temporary-filesystem handoff probe requires Linux.");
        var fixture = await FixtureAsync();
        try
        {
            (await DispatchAsync(fixture)).ExitCode.Should().Be(0);
            var requestPath = Directory.EnumerateFiles(Path.Combine(fixture.State, "install-handoffs"), "*.json").Single();
            var installer = Path.ChangeExtension(requestPath, ".sh");
            // This trusted owner replaces its private staged installer with a small execution probe.
            var stub = "#!/usr/bin/env bash\numask 077\nprintf '%s' \"$NETRATEL_SEED_ENROLLMENT_CODE\" > \"$NetRatel_STATE/observed-secret\"\n";
            await File.WriteAllTextAsync(installer, stub);
            var request = JsonNode.Parse(await File.ReadAllTextAsync(requestPath))!;
            request["installer_sha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(installer))).ToLowerInvariant();
            await File.WriteAllTextAsync(requestPath, request.ToJsonString());
            var sentinel = Path.Combine(fixture.State, "install-handoffs", "unrelated.json");
            await File.WriteAllTextAsync(sentinel, "preserve");
            var result = await RunBoundedProcessAsync(WorkerStart(fixture, requestPath));
            result.ExitCode.Should().Be(0, result.StandardError);
            (await File.ReadAllTextAsync(Path.Combine(fixture.State, "observed-secret"))).Should().Be(fixture.Secret);
            result.StandardOutput.Should().NotContain(fixture.Secret);
            result.StandardError.Should().NotContain(fixture.Secret);
            File.Exists(requestPath).Should().BeFalse();
            (await File.ReadAllTextAsync(sentinel)).Should().Be("preserve");
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    private sealed record Fixture(string Root, string Bin, string Client, string State, string Unit, string Script, string Arguments, string Secret);

    [SupportedOSPlatform("linux")]
    private static async Task<Fixture> FixtureAsync(bool updaterOnlyState = false)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "netratel seed-" + Guid.NewGuid().ToString("N"))).FullName;
        File.SetUnixFileMode(root, (UnixFileMode)0x1C0);
        var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        var client = Directory.CreateDirectory(Path.Combine(root, "client")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        File.SetUnixFileMode(state, (UnixFileMode)0x1C0);
        var versions = Directory.CreateDirectory(Path.Combine(client, "versions", "1.2.3")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(client, "current"), versions);
        var units = Directory.CreateDirectory(Path.Combine(root, "systemd")).FullName;
        var unit = Path.Combine(units, "netratel-client.service");
        await File.WriteAllTextAsync(unit, $$"""
[Service]
WorkingDirectory="{{client}}/current"
ExecStart="{{client}}/netratel-client-start.sh"
Environment="NetRatelCLIENT__Client__AutoUpdate__StateDirectory={{state}}"
""");
        if (updaterOnlyState)
        {
            var unitText = await File.ReadAllTextAsync(unit);
            await File.WriteAllTextAsync(unit, unitText[..unitText.IndexOf("Environment=", StringComparison.Ordinal)]);
            await File.WriteAllTextAsync(Path.Combine(units, "netratel-update.service"),
                "[Service]\nEnvironment=\"NetRatel_UPDATE_STATE=" + state + "\"\n");
        }
        var script = Path.Combine(root, "seed.sh");
        var text = GetSeedScript("LinuxScript");
        var marker = "\n#| END\n";
        await File.WriteAllTextAsync(script, text[(text.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
        await ExecutableAsync(Path.Combine(bin, "systemctl"), """
#!/usr/bin/env bash
case "$3" in
  FragmentPath) printf '%s\n' "$FAKE_SYSTEMD_FRAGMENT" ;;
  DropInPaths) printf '\n' ;;
  MainPID) printf '%s\n' "$FAKE_SYSTEMD_PID" ;;
esac
""");
        await ExecutableAsync(Path.Combine(bin, "systemd-run"), "#!/usr/bin/env bash\nprintf '%s\\n' \"$@\" > \"$FAKE_SYSTEMD_RUN_ARGS_PATH\"\n");
        return new Fixture(root, bin, client, state, unit, script, Path.Combine(root, "arguments.txt"), "ENR-DO-NOT-LOG-9C70");
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> DispatchAsync(Fixture fixture)
    {
        var start = Start(fixture, "/bin/bash");
        start.ArgumentList.Add(fixture.Script);
        start.Environment["ApiBase"] = "https://api.example.invalid/api";
        start.Environment["GatewayEndpoint"] = "https://gateway.example.invalid";
        start.Environment["TenantId"] = "4098";
        start.Environment["EnrollmentCode"] = fixture.Secret;
        start.Environment["Version"] = "1.2.4";
        return await RunBoundedProcessAsync(start);
    }

    private static ProcessStartInfo WorkerStart(Fixture fixture, string request)
    {
        var start = Start(fixture, "/usr/bin/python3");
        start.ArgumentList.Add(Path.ChangeExtension(request, ".py"));
        start.ArgumentList.Add("worker");
        start.ArgumentList.Add(request);
        return start;
    }

    private static ProcessStartInfo Start(Fixture fixture, string executable)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["PATH"] = fixture.Bin + ":/usr/bin:/bin";
        start.Environment["NetRatel_TEST_ALLOW_NONROOT"] = "true";
        start.Environment["FAKE_SYSTEMD_FRAGMENT"] = fixture.Unit;
        start.Environment["FAKE_SYSTEMD_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["FAKE_SYSTEMD_RUN_ARGS_PATH"] = fixture.Arguments;
        return start;
    }

    [SupportedOSPlatform("linux")]
    private static async Task ExecutableAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content);
        File.SetUnixFileMode(path, (UnixFileMode)0x1C0);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunBoundedProcessAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }

    private static string GetSeedScript(string fieldName)
    {
        var seed = typeof(AgentUpdateScriptSeedService).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        return (string)seed.GetType().GetProperty("Content")!.GetValue(seed)!;
    }
}
