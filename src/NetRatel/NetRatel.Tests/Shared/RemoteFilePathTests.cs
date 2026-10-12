using AwesomeAssertions;
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

    [Theory]
    [InlineData("/var/data", "new folder", "/var/data/new folder")]
    [InlineData("/var/data ", " reports ", "/var/data / reports ")]
    [InlineData("/", "logs..archive", "/logs..archive")]
    [InlineData("C:/Data", "new folder", "C:\\Data\\new folder")]
    public void TryJoinDirectory_PreservesRemotePathRules(string parent, string name, string expected)
    {
        RemoteFilePath.TryJoinDirectory(parent, name, out var path).Should().BeTrue();
        path.Should().Be(expected);
    }

    [Theory]
    [InlineData("/data", "..")]
    [InlineData("/data", "../escape")]
    [InlineData("/data", "nested/child")]
    [InlineData("/data/../other", "new")]
    [InlineData("C:\\Data", "NUL.txt")]
    [InlineData("C:\\Data", "folder:stream")]
    [InlineData("C:\\Data", "trailing.")]
    public void TryJoinDirectory_RejectsTraversalAndRemoteDeviceNames(string parent, string name) =>
        RemoteFilePath.TryJoinDirectory(parent, name, out _).Should().BeFalse();

    [Theory]
    [InlineData("/data/reports ", "/data")]
    [InlineData("/data/ ", "/data")]
    [InlineData("/data /reports", "/data ")]
    public void DirectoryMutationPaths_PreserveExactUnixWhitespace(string input, string expectedParent)
    {
        RemoteFilePath.TryNormalizeDirectory(input, out var path).Should().BeTrue();
        path.Should().Be(input);
        RemoteFilePath.TryGetDirectoryParent(path, out var parent).Should().BeTrue();
        parent.Should().Be(expectedParent);
    }
}
