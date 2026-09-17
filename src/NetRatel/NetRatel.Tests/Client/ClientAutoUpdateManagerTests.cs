using FluentAssertions;
using NetRatel.Client.Service.Updates;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientUpdateVersioningTests
{
    [Theory]
    [InlineData("0.4.10", "0.4.9", true)]
    [InlineData("0.4.10+build.2", "0.4.9", true)]
    [InlineData("0.4.10", "0.4.10", false)]
    [InlineData("0.4.9", "0.4.10", false)]
    [InlineData("0.4.102", "0.4.102-rc.1", true)]
    [InlineData("0.4.102-rc.2", "0.4.102-rc.1", true)]
    [InlineData("0.4.102-rc.1", "0.4.102", false)]
    [InlineData("0.1.0-rc.1", "0.5.6-rc.5", false)]
    [InlineData("not-a-version", "0.4.10", false)]
    public void IsNewerVersion_UsesSemanticCore(string candidate, string current, bool expected)
    {
        ClientUpdateVersioning.IsNewerVersion(candidate, current).Should().Be(expected);
    }

    [Fact]
    public void ResolveRuntimeId_UsesConfiguredValue_WhenPresent()
    {
        ClientUpdateVersioning.ResolveRuntimeId("LINUX-X64").Should().Be("linux-x64");
    }

    [Fact]
    public void AkkaCoordinator_IsTheOnlyClientUpdateTransport()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var coordinator = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/Service/Updates/AkkaClientAutoUpdateCoordinator.cs"));

        File.Exists(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/Service/Updates/ClientAutoUpdateManager.cs")).Should().BeFalse();
        coordinator.Should().Contain("StartUpdater");
        coordinator.Should().NotContain("Spacetime");
    }

    [Theory]
    [InlineData("0.4.121-rc.1+9a5b48a39f279ae20e320e2d7f847a057ef28d3c", "0.4.121-rc.1")]
    [InlineData("0.4.121-rc.1", "0.4.121-rc.1")]
    public void PublishedPrereleaseVersion_NormalizesWithoutUsingAssemblyVersion(string informationalVersion, string expected)
    {
        ClientUpdateVersioning.NormalizePublishedVersion(informationalVersion).Should().Be(expected);
    }

    [Fact]
    public void PresenceClient_SendsImmediateActivationHeartbeatBeforeExtensions()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var presence = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/Service/Gateway/AgentGatewayPresenceClient.cs"));

        presence.Should().Contain("await SendHeartbeatAsync(++sequence).ConfigureAwait(false);");
        presence.IndexOf("await SendHeartbeatAsync(++sequence).ConfigureAwait(false);", StringComparison.Ordinal)
            .Should().BeLessThan(presence.IndexOf("runForPresenceSession?.Invoke", StringComparison.Ordinal));
    }

    [Fact]
    public void AkkaCoordinator_Closes_The_Staged_Package_Before_It_Is_Renamed()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var coordinator = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/Service/Updates/AkkaClientAutoUpdateCoordinator.cs"));

        coordinator.Should().Contain(
            "await using (var destination = new FileStream(packagePath + \".tmp\", FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))");
        coordinator.Should().Contain(
            "}\n\n                // Windows will not rename a file whose writer is still open with FileShare.None.\n                File.Move(packagePath + \".tmp\", packagePath, true);");
    }

    [Fact]
    public void WindowsUpdater_Sets_And_Verifies_The_Service_ImagePath_Without_ScExe_Quoting()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var updater = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/tools/netratel-update.ps1"));

        updater.Should().Contain("function Set-NetRatelServiceImagePath");
        updater.Should().Contain("Set-ItemProperty -Path $serviceKey -Name ImagePath -Value $ImagePath");
        updater.Should().Contain("Windows service ImagePath verification failed");
        updater.Should().Contain("Set-NetRatelServiceImagePath $script:ActivePath");
        updater.Should().Contain("Set-NetRatelServiceImagePath $script:PreviousPath");
        updater.Should().NotContain("sc.exe config");
    }
}
