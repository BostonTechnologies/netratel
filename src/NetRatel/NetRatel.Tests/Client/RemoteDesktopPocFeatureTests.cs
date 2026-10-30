using System;
using System.Text;
using AwesomeAssertions;
using NetRatel.Client.Service.RemoteDesktop;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteDesktopPocFeatureTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("1", true)]
    public void DesktopPoc_IsDefaultOffUnlessExplicitlyEnabled(string? setting, bool expected) =>
        RemoteDesktopPocFeature.IsEnabled(setting).Should().Be(expected);

    [Fact]
    public void LegacyCleanup_IsScopedToResidentHelpersInNetratelVersionDirectory()
    {
        var startInfo = RemoteDesktopUserHelperTask.BuildLegacyHelperCleanupStartInfo();
        startInfo.ArgumentList.Should().Contain("-EncodedCommand");
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(startInfo.ArgumentList[^1]));
        script.Should().Contain("NetRatel\\Client\\versions\\");
        script.Should().Contain("--remote-desktop-user-helper");
        script.Should().Contain("ExecutablePath.StartsWith");
        script.Should().NotContain("Stop-Service");
    }
}
