using FluentAssertions;
using System.Diagnostics;
using System.Reflection;
using NetRatel.API.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentUpdateScriptSeedServiceTests
{
    [Fact]
    public void Seeded_Update_Scripts_Use_Onboarding_Download_And_Required_Params()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.API/Services/AgentUpdateScriptSeedService.cs"));

        source.Should().Contain("/Windows/NetRatel");
        source.Should().Contain("/Linux/NetRatel");
        source.Should().Contain("Update Client To Latest");
        source.Should().Contain("X-NetRatel-Tenant-Id");
        source.Should().Contain("X-NetRatel-Enrollment-Code");
        source.Should().Contain("onboarding-download");
        source.Should().Contain("\"EnrollmentCode\"");
        source.Should().Contain("Get-NetRatelSha256Hex");
        source.Should().Contain("Expand-NetRatelZip");
        source.Should().Contain("[System.Security.Cryptography.SHA256]::Create()");
        source.Should().Contain("[System.IO.Compression.ZipFile]::ExtractToDirectory");
        source.Should().Contain("$actualSha = Get-NetRatelSha256Hex -Path $zipPath");
        source.Should().Contain("Expand-NetRatelZip -ZipPath $zipPath -DestinationPath $targetDir");
        source.Should().NotContain("(Get-FileHash -Path $zipPath -Algorithm SHA256).Hash");
        source.Should().NotContain("Expand-Archive -Path $zipPath -DestinationPath $targetDir -Force");
        source.Should().Contain("This NetRatel systemd installer must be run as root.");
        source.Should().Contain("for required_command in curl unzip sha256sum systemctl");
        source.Should().Contain("BundleExtractDir=");
        source.Should().Contain("mkdir -p \"$RootDir\" \"$StateDir\" \"$BundleExtractDir\"");
        source.Should().Contain("DOTNET_BUNDLE_EXTRACT_BASE_DIR=\"$BundleExtractDir\" \"$client_exe\" --enroll");
        source.Should().Contain("\"$client_exe\" --auth-check >/dev/null 2>&1");
        source.Should().Contain("0|10|12");
        source.Should().Contain("Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=$BundleExtractDir");
        source.Should().Contain("Environment=NetRatelCLIENT__Client__ApiBaseUrl=$ApiBase");
        source.Should().NotContain("Environment=NetRatelCLIENT__Transport__Mode=AkkaPresence");
        source.Should().NotContain("Environment=NetRatelCLIENT__Gateway__Endpoint=$ApiBase");
        source.Should().NotContain("Environment=NetRatelCLIENT__Gateway__RequiredPresenceAuthority=akka");
        source.Should().NotContain("Environment=NetRatelCLIENT__Gateway__TelemetryAuthorityEnabled=true");
        source.Should().NotContain("Environment=NetRatelCLIENT__Gateway__CommandAuthorityEnabled=true");
        source.Should().NotContain("Environment=NetRatelCLIENT__Gateway__JobAuthorityEnabled=true");
        source.Should().NotContain("Environment=NetRatelCLIENT__Gateway__ControlGatewayEnabled=true");
        source.Should().NotContain("Environment=NetRatelCLIENT__Gateway__FileGatewayEnabled=true");
        source.Should().NotContain("Environment=NetRatelCLIENT__Gateway__LogGatewayEnabled=true");
        source.Should().NotContain("Environment=NetRatelCLIENT__Gateway__RemoteSupportGatewayEnabled=true");
        source.Should().NotContain("Environment=NetRatelCLIENT__Gateway__TerminalGatewayEnabled=true");
        source.Should().NotContain("Environment=NetRatelCLIENT__Gateway__TerminalAuthorityEnabled=true");
        source.Should().Contain("\"systemctl\", \"show\", \"-p\", \"FragmentPath\", \"--value\", \"netratel-client.service\"");
        source.Should().Contain("EnvironmentFile=");
        source.Should().Contain("netratel-client-start.sh");
        source.Should().Contain("NetRatel.Client");
        source.Should().Contain("RestartPreventExitStatus=78");
        source.Should().Contain("systemctl is-active --quiet netratel-client.service");
        source.Should().Contain("journalctl -u netratel-client.service -n 80 --no-pager");
        source.Should().NotContain("sudo");
    }

    [Fact]
    public async Task Seeded_Linux_Update_Helper_Preserves_Explicit_Gateway_And_False_OptOut()
    {
        if (!OperatingSystem.IsLinux()) return;

        var seedField = typeof(AgentUpdateScriptSeedService).GetField("LinuxScript", BindingFlags.Static | BindingFlags.NonPublic);
        var seed = seedField!.GetValue(null)!;
        var script = (string)seed.GetType().GetProperty("Content", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(seed)!;
        const string helperMarker = "preserved_client_environment=\"$(python3 - <<'PY'\n";
        var helperStart = script.IndexOf(helperMarker, StringComparison.Ordinal);
        helperStart.Should().BeGreaterThanOrEqualTo(0);
        var pythonStart = helperStart + helperMarker.Length;
        var pythonEnd = script.IndexOf("\nPY\n)\"", pythonStart, StringComparison.Ordinal);
        pythonEnd.Should().BeGreaterThan(pythonStart);
        var python = script[pythonStart..pythonEnd];

        var root = Path.Combine(Path.GetTempPath(), $"netratel-seeded-update-{Guid.NewGuid():N}");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        try
        {
            var existingUnit = Path.Combine(root, "existing.service");
            await File.WriteAllTextAsync(existingUnit, """
[Service]
Environment=NetRatelCLIENT__Gateway__Endpoint=https://split-gateway.example.invalid
Environment=NetRatelCLIENT__Gateway__FileGatewayEnabled=false
Environment=NetRatelCLIENT__Gateway__ControlGatewayEnabled=true
Environment=NetRatelCLIENT__Transport__Mode=AkkaPresence
Environment=Custom__ServiceValue="kept value"
EnvironmentFile=-/etc/netratel-client.env
""");
            var systemctl = Path.Combine(bin, "systemctl");
            await File.WriteAllTextAsync(systemctl, "#!/usr/bin/env bash\nprintf '%s\\n' \"$FAKE_SYSTEMD_FRAGMENT\"\n");
            File.SetUnixFileMode(systemctl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var helperScript = Path.Combine(root, "preserve.sh");
            await File.WriteAllTextAsync(helperScript, $"#!/usr/bin/env bash\nset -euo pipefail\npreserved_client_environment=\"$(python3 - <<'PY'\n{python}\nPY\n)\"\nprintf '%s\\n' \"$preserved_client_environment\"\n");
            File.SetUnixFileMode(helperScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var start = new ProcessStartInfo("bash", helperScript)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.Environment["PATH"] = $"{bin}:/usr/bin:/bin";
            start.Environment["FAKE_SYSTEMD_FRAGMENT"] = existingUnit;
            using var process = Process.Start(start)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0, stderr);
            stdout.Should().Contain("Environment=NetRatelCLIENT__Gateway__Endpoint=https://split-gateway.example.invalid");
            stdout.Should().Contain("Environment=NetRatelCLIENT__Gateway__FileGatewayEnabled=false");
            stdout.Should().Contain("Environment=Custom__ServiceValue=\"kept value\"");
            stdout.Should().Contain("EnvironmentFile=-/etc/netratel-client.env");
            stdout.Should().NotContain("Environment=NetRatelCLIENT__Gateway__ControlGatewayEnabled=true");
            stdout.Should().NotContain("Environment=NetRatelCLIENT__Transport__Mode=AkkaPresence");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
