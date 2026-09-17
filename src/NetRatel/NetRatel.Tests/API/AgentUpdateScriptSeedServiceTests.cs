using FluentAssertions;
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
        source.Should().Contain("NetRatelCLIENT__Transport__Mode=AkkaPresence");
        source.Should().Contain("NetRatelCLIENT__Gateway__RequiredPresenceAuthority=akka");
        source.Should().Contain("NetRatelCLIENT__Gateway__TelemetryAuthorityEnabled=true");
        source.Should().Contain("NetRatelCLIENT__Gateway__CommandAuthorityEnabled=true");
        source.Should().Contain("NetRatelCLIENT__Gateway__JobAuthorityEnabled=true");
        source.Should().Contain("NetRatelCLIENT__Gateway__ControlGatewayEnabled=true");
        source.Should().Contain("NetRatelCLIENT__Gateway__FileGatewayEnabled=true");
        source.Should().Contain("NetRatelCLIENT__Gateway__LogGatewayEnabled=true");
        source.Should().Contain("NetRatelCLIENT__Gateway__RemoteSupportGatewayEnabled=true");
        source.Should().Contain("NetRatelCLIENT__Gateway__TerminalGatewayEnabled=true");
        source.Should().Contain("NetRatelCLIENT__Gateway__TerminalAuthorityEnabled=true");
        source.Should().Contain("netratel-client-start.sh");
        source.Should().Contain("NetRatel.Client");
        source.Should().Contain("RestartPreventExitStatus=78");
        source.Should().Contain("systemctl is-active --quiet netratel-client.service");
        source.Should().Contain("journalctl -u netratel-client.service -n 80 --no-pager");
        source.Should().NotContain("sudo");
    }
}
