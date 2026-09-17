using FluentAssertions;
using NetRatel.Client.Services;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class FileSystemServiceGatewayPathTests
{
    [Fact]
    public void TryResolveGatewayPath_AdmitsAnExistingFileBeneathTheResolvedRoot()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"netratel-file-policy-{Guid.NewGuid():N}");
        var root = Path.Combine(temporaryRoot, "approved");
        var file = Path.Combine(root, "logs", "agent.log");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "safe");

        try
        {
            var resolved = new FileSystemService().TryResolveGatewayPath(
                file,
                [root],
                requireExisting: true,
                out var resolvedPath);

            resolved.Should().BeTrue();
            resolvedPath.Should().Be(Path.GetFullPath(file));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolveGatewayPath_RejectsASymbolicLinkThatEscapesTheApprovedRoot()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"netratel-file-policy-{Guid.NewGuid():N}");
        var root = Path.Combine(temporaryRoot, "approved");
        var outside = Path.Combine(temporaryRoot, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "not-readable-through-the-root");
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);

        try
        {
            var resolved = new FileSystemService().TryResolveGatewayPath(
                Path.Combine(root, "escape", "secret.txt"),
                [root],
                requireExisting: true,
                out _);

            resolved.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolveGatewayPath_RejectsANewDirectoryBeneathASymbolicLinkThatEscapesTheApprovedRoot()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"netratel-file-policy-{Guid.NewGuid():N}");
        var root = Path.Combine(temporaryRoot, "approved");
        var outside = Path.Combine(temporaryRoot, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);

        try
        {
            var resolved = new FileSystemService().TryResolveGatewayPath(
                Path.Combine(root, "escape", "new-directory"),
                [root],
                requireExisting: false,
                out _);

            resolved.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }
}
