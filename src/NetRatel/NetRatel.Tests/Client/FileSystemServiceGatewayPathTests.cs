using AwesomeAssertions;
using NetRatel.Client.Services;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class FileSystemServiceGatewayPathTests
{
    [Fact]
    public void Enumeration_ReturnsOnlyImmediateEntriesAndTheirFilesystemTimestamps()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-file-metadata-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "folder");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "nested.txt"), "not enumerated");
        var modified = new DateTime(2026, 1, 15, 10, 20, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(directory, modified);
        try
        {
            var entries = new FileSystemService().EnumerateEntries(root).ToArray();
            var folder = entries.Should().ContainSingle().Which;
            folder.IsDirectory.Should().BeTrue();
            folder.SizeBytes.Should().Be(0, "folder size is not calculated recursively");
            folder.LastModifiedUtc.Should().Be(new DateTimeOffset(modified));
            folder.CreatedUtc.Should().NotBeNull();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MutationGuardRejectsRootsAndFinalLinksWithoutChangingTheirTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-file-mutation-{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "alias");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        var files = new FileSystemService();
        try
        {
            files.IsGatewayMutationTargetAllowed(root, root, [root]).Should().BeFalse();
            var filesystemRoot = Path.GetPathRoot(root)!;
            files.IsGatewayMutationTargetAllowed(filesystemRoot, filesystemRoot, []).Should().BeFalse();
            files.IsGatewayMutationTargetAllowed(link, target, [root]).Should().BeFalse();
            files.IsGatewayMutationTargetAllowed(target, target, [root]).Should().BeTrue();
            Directory.Exists(target).Should().BeTrue();
            new DirectoryInfo(link).LinkTarget.Should().NotBeNull();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
