using FluentAssertions;
using NetRatel.Shared.Contracts.FileSystem;
using Xunit;

namespace NetRatel.Tests.Shared;

public sealed class RemoteFilePathTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/etc", "/etc")]
    [InlineData(" /var/log/ ", "/var/log")]
    [InlineData("C:", "C:\\")]
    [InlineData("c:\\", "C:\\")]
    [InlineData("C:/Users/Administrator/", "C:\\Users\\Administrator")]
    public void TryNormalize_UsesTheRemotePathFlavor(string input, string expected)
    {
        RemoteFilePath.TryNormalize(input, out var path).Should().BeTrue();
        path.Should().Be(expected);
    }

    [Theory]
    [InlineData("/", false, null)]
    [InlineData("/etc", true, "/")]
    [InlineData("/etc/netratel", true, "/etc")]
    [InlineData("C:\\", false, null)]
    [InlineData("C:\\Users", true, "C:\\")]
    [InlineData("C:\\Users\\Administrator", true, "C:\\Users")]
    public void TryGetParent_DoesNotUseTheWebHostPathRules(string input, bool hasParent, string? expected)
    {
        RemoteFilePath.TryGetParent(input, out var parent).Should().Be(hasParent);
        if (hasParent)
        {
            parent.Should().Be(expected);
        }
    }

    [Theory]
    [InlineData("C:Users")]
    [InlineData("relative/path")]
    [InlineData("\0")]
    public void TryNormalize_RejectsDriveRelativeAndNonAbsolutePaths(string input) =>
        RemoteFilePath.TryNormalize(input, out _).Should().BeFalse();
}
